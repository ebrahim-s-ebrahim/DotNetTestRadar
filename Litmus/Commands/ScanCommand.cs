using System.CommandLine;
using Litmus.Abstractions;
using Litmus.Models;
using Litmus.Services;
using Spectre.Console;

namespace Litmus.Commands;

public class ScanCommand
{
    public static Command Create(IFileSystem fileSystem, IProcessRunner processRunner)
    {
        var solutionOption = new Option<string?>("--solution")
        {
            Description = "Path to a .sln or .slnx file. Auto-detected if a single .sln/.slnx exists in the current directory."
        };

        var testsDirOption = new Option<string?>("--tests-dir")
        {
            Description = "Directory or project file to run tests from. " +
                          "Defaults to the solution file, which runs all test projects in the solution."
        };

        var sinceOption = new Option<DateTime?>("--since")
        {
            Description = "Limit git history to commits after this date (ISO format)"
        };

        var topOption = new Option<int>("--top")
        {
            Description = "Number of top files to display",
            DefaultValueFactory = _ => 20
        };

        var excludeOption = new Option<string[]>("--exclude")
        {
            Description = "Glob pattern(s) to exclude files. Repeatable.",
            AllowMultipleArgumentsPerToken = true
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = "Export results to a file (format determined by extension: .json, .csv, or .html)"
        };

        var baselineOption = new Option<string?>("--baseline")
        {
            Description = "Path to a previous JSON export to compare against"
        };

        var noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable colored output"
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format for stdout: table, json, csv, or html (independent of --output file format)",
            DefaultValueFactory = _ => "table"
        };
        formatOption.AcceptOnlyFromAmong("table", "json", "csv", "html");

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Show detailed intermediate scores for each file"
        };

        var quietOption = new Option<bool>("--quiet")
        {
            Description = "Suppress all output except errors (exit code only)"
        };

        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Maximum time in minutes to wait for dotnet test to complete (default: 10)",
            DefaultValueFactory = _ => 10
        };

        var coverageToolOption = new Option<string>("--coverage-tool")
        {
            Description = "Tool for collecting code coverage: coverlet (default) or dotnet-coverage",
            DefaultValueFactory = _ => "coverlet"
        };
        coverageToolOption.AcceptOnlyFromAmong("coverlet", "dotnet-coverage");

        var noCoverageOption = new Option<bool>("--no-coverage")
        {
            Description = "Skip test execution and coverage collection. " +
                          "Ranks files by churn, complexity, and testability only."
        };

        var failOnThresholdOption = new Option<double?>("--fail-on-threshold")
        {
            Description = "Exit with code 1 if any file's Risk Score or Starting Priority exceeds this value (0.0-2.0)"
        };

        var detailedOption = new Option<bool>("--detailed")
        {
            Description = "Expand top-ranked files with per-method coverage and complexity"
        };

        var explainOption = new Option<bool>("--explain")
        {
            Description = "Show a plain-English explanation of each file's risk and priority drivers"
        };

        var noGroupOption = new Option<bool>("--no-group")
        {
            Description = "Show a flat table instead of grouping by priority level"
        };

        var command = new Command(
            "scan",
            "Run dotnet test, collect code coverage, and analyze the solution in one step")
        {
            solutionOption,
            testsDirOption,
            sinceOption,
            topOption,
            excludeOption,
            outputOption,
            baselineOption,
            noColorOption,
            formatOption,
            verboseOption,
            quietOption,
            timeoutOption,
            coverageToolOption,
            noCoverageOption,
            failOnThresholdOption,
            detailedOption,
            explainOption,
            noGroupOption
        };

        command.SetAction(parseResult =>
        {
            var (solutionPath, solutionError) = CommandHelpers.ResolveSolutionPath(
                parseResult.GetValue(solutionOption), fileSystem);

            if (solutionError != null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(solutionError)}");
                return Task.FromResult(1);
            }

            var options = new AnalysisOptions
            {
                SolutionPath = solutionPath!,
                CoveragePath = string.Empty,  // filled in at runtime after dotnet test
                Since = parseResult.GetValue(sinceOption) ?? DateTime.Today.AddYears(-1),
                Top = parseResult.GetValue(topOption),
                ExcludePatterns = parseResult.GetValue(excludeOption)?.ToList() ?? [],
                OutputPath = parseResult.GetValue(outputOption),
                BaselinePath = parseResult.GetValue(baselineOption),
                NoColor = parseResult.GetValue(noColorOption),
                Format = parseResult.GetValue(formatOption)!,
                Verbose = parseResult.GetValue(verboseOption),
                Quiet = parseResult.GetValue(quietOption),
                NoCoverage = parseResult.GetValue(noCoverageOption),
                Detailed = parseResult.GetValue(detailedOption),
                Explain = parseResult.GetValue(explainOption),
                NoGroup = parseResult.GetValue(noGroupOption),
                FailOnThreshold = parseResult.GetValue(failOnThresholdOption)
            };

            var testsDir = parseResult.GetValue(testsDirOption);
            var timeoutMinutes = parseResult.GetValue(timeoutOption);
            var coverageTool = parseResult.GetValue(coverageToolOption)!;
            return Execute(options, testsDir, timeoutMinutes, coverageTool, fileSystem, processRunner);
        });

        return command;
    }

    private static async Task<int> Execute(
        AnalysisOptions options,
        string? testsDir,
        int timeoutMinutes,
        string coverageTool,
        IFileSystem fileSystem,
        IProcessRunner processRunner)
    {
        if (options.Verbose && options.Quiet)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --verbose and --quiet cannot be used together.");
            return 1;
        }

        if (options.NoCoverage && testsDir != null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --no-coverage and --tests-dir cannot be used together.");
            return 1;
        }

        if (options.NoCoverage && coverageTool != "coverlet")
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --no-coverage and --coverage-tool cannot be used together.");
            return 1;
        }

        // Validate solution file
        if (!fileSystem.FileExists(options.SolutionPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Solution file not found: " + options.SolutionPath);
            return 1;
        }

        var ext = Path.GetExtension(options.SolutionPath).ToLowerInvariant();
        if (ext is not ".sln" and not ".slnx")
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Solution file must be a .sln or .slnx file.");
            return 1;
        }

        // Validate --tests-dir if provided
        if (testsDir != null && !fileSystem.FileExists(testsDir) && !fileSystem.DirectoryExists(testsDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Tests path not found: {testsDir}");
            return 1;
        }

        // Validate baseline file if provided
        if (options.BaselinePath != null)
        {
            if (!fileSystem.FileExists(options.BaselinePath))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Baseline file not found: " + options.BaselinePath);
                return 1;
            }
            if (!options.BaselinePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] --baseline requires a JSON file (from a previous --output report.json run).\n" +
                    $"The file '{Markup.Escape(Path.GetFileName(options.BaselinePath))}' has an unsupported extension.");
                return 1;
            }
        }

        // --no-coverage fast path: skip test execution entirely
        if (options.NoCoverage)
        {
            try
            {
                processRunner.Run("git", "--version", ".");
            }
            catch
            {
                AnsiConsole.MarkupLine("[red]Error:[/] git must be installed and available on PATH.");
                return 1;
            }

            if (!options.Quiet)
                AnsiConsole.MarkupLine("[bold]Analyzing solution (no coverage — ranking by churn, complexity, and testability)...[/]");

            var emptyCoverage = new CoverageResult();
            return await AnalyzeCommand.RunAnalysis(options, emptyCoverage, fileSystem, processRunner);
        }

        // Check dotnet availability
        try
        {
            processRunner.Run("dotnet", "--version", ".");
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]Error:[/] dotnet SDK must be installed and available on PATH.");
            return 1;
        }

        // Check git availability
        try
        {
            processRunner.Run("git", "--version", ".");
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]Error:[/] git must be installed and available on PATH.");
            return 1;
        }

        // Check dotnet-coverage availability if selected
        if (coverageTool == "dotnet-coverage")
        {
            try
            {
                processRunner.Run("dotnet-coverage", "--version", ".");
            }
            catch
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] dotnet-coverage must be installed when using --coverage-tool dotnet-coverage.\n" +
                    "Install it with: dotnet tool install --global dotnet-coverage");
                return 1;
            }
        }

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(options.SolutionPath)) ?? ".";
        // Trim trailing separators so that a path like "dir\" doesn't produce
        // an escaped quote (dir\") in the argument string, which breaks Windows
        // command-line parsing.
        var testTarget = (testsDir ?? options.SolutionPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tempDir = Path.Combine(Path.GetTempPath(), $"testradar-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            // Step 1: Run dotnet test with coverage collection
            if (!options.Quiet)
                AnsiConsole.MarkupLine("[bold]Step 1/2:[/] Running tests and collecting coverage...");

            string? testErrorDetail = null;
            int exitCode;
            var testTimeoutMs = timeoutMinutes * 60 * 1000;

            if (coverageTool == "dotnet-coverage")
            {
                // dotnet-coverage runs externally — no data collector, avoids coverlet hangs
                var coverageOutputPath = Path.Combine(tempDir, "coverage.cobertura.xml");
                var dcArgs = $"collect \"dotnet test \\\"{testTarget}\\\"\" -f cobertura -o \"{coverageOutputPath}\"";

                if (!options.Quiet && options.Verbose)
                    AnsiConsole.MarkupLine("[dim]Using dotnet-coverage for collection[/]");

                try
                {
                    exitCode = RunTestWithLiveOutput(processRunner, "dotnet-coverage", dcArgs,
                        solutionDir, options, testTimeoutMs);
                }
                catch (TimeoutException)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] dotnet-coverage did not complete within {timeoutMinutes} minute(s) and was terminated.\n" +
                        "Use --timeout <minutes> to increase the limit.");
                    return 1;
                }
            }
            else
            {
                // Default: coverlet via dotnet test --collect
                var testArgs = $"test \"{testTarget}\" " +
                               $"--collect:\"XPlat Code Coverage\" " +
                               $"--results-directory \"{tempDir}\"";

                // Show per-test results so the user sees continuous progress
                if (options.Verbose)
                    testArgs += " --logger \"console;verbosity=detailed\"";
                else if (!options.Quiet)
                    testArgs += " --logger \"console;verbosity=normal\"";

                try
                {
                    exitCode = RunTestWithLiveOutput(processRunner, "dotnet", testArgs,
                        solutionDir, options, testTimeoutMs);
                }
                catch (TimeoutException)
                {
                    // Auto-fallback: try dotnet-coverage if available
                    bool hasDotnetCoverage;
                    try
                    {
                        processRunner.Run("dotnet-coverage", "--version", ".");
                        hasDotnetCoverage = true;
                    }
                    catch
                    {
                        hasDotnetCoverage = false;
                    }

                    if (hasDotnetCoverage)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Warning:[/] Coverlet timed out after {timeoutMinutes} minute(s). Retrying with dotnet-coverage...");

                        var coverageOutputPath = Path.Combine(tempDir, "coverage.cobertura.xml");
                        var dcArgs = $"collect \"dotnet test \\\"{testTarget}\\\"\" -f cobertura -o \"{coverageOutputPath}\"";

                        try
                        {
                            exitCode = RunTestWithLiveOutput(processRunner, "dotnet-coverage", dcArgs,
                                solutionDir, options, testTimeoutMs);
                        }
                        catch (TimeoutException)
                        {
                            AnsiConsole.MarkupLine(
                                $"[red]Error:[/] dotnet-coverage also timed out after {timeoutMinutes} minute(s).\n" +
                                "Use --timeout <minutes> to increase the limit.");
                            return 1;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]Error:[/] dotnet test did not complete within {timeoutMinutes} minute(s) and was terminated.\n" +
                            "This is typically caused by the coverage data collector (coverlet) hanging.\n" +
                            "Try using --coverage-tool dotnet-coverage as an alternative.\n" +
                            "Use --timeout <minutes> to increase the limit.");
                        return 1;
                    }
                }
            }

            if (exitCode != 0)
            {
                // Tests failing is not a fatal error — coverage files may still be present
                testErrorDetail = $"dotnet test exited with code {exitCode}";
                AnsiConsole.MarkupLine("[yellow]Warning:[/] Some tests failed. Coverage data may be incomplete.");
            }
            else if (!options.Quiet)
            {
                AnsiConsole.MarkupLine("[green]Tests passed.[/]");
            }

            // Discover all coverage.cobertura.xml files produced by the test run
            var coverageFiles = fileSystem
                .GetFiles(tempDir, "coverage.cobertura.xml", recursive: true)
                .ToList();

            if (coverageFiles.Count == 0)
            {
                if (testErrorDetail != null)
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] No coverage files were generated because some tests failed.\n" +
                        "Fix the failing tests and re-run. If tests pass but coverage is still missing,\n" +
                        "ensure your test projects reference the coverlet.collector package:\n" +
                        "  dotnet add <test-project> package coverlet.collector\n" +
                        "Or try: --coverage-tool dotnet-coverage");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] No coverage.cobertura.xml files were generated.\n" +
                        "Ensure your test projects reference the coverlet.collector package:\n" +
                        "  dotnet add <test-project> package coverlet.collector\n" +
                        "Or try: --coverage-tool dotnet-coverage\n\n" +
                        "If this codebase has no tests yet, re-run with [bold]--no-coverage[/] to rank\n" +
                        "files by churn, complexity, and testability without coverage data.");
                }
                return 1;
            }

            // Parse each coverage file and merge — taking the highest rate per source file
            // so that a class covered by any test project counts as covered
            var coverageParser = new CoverageParser(fileSystem);
            CoverageResult coverageResult;
            try
            {
                var allResults = coverageFiles.Select(f => coverageParser.Parse(f)).ToList();
                coverageResult = CoverageParser.Merge(allResults);

                if (coverageFiles.Count > 1 && !options.Quiet)
                    AnsiConsole.MarkupLine($"[dim]Merged coverage from {coverageFiles.Count} test project(s).[/]");
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                return 1;
            }

            // Step 2: Run the full analysis pipeline
            if (!options.Quiet)
                AnsiConsole.MarkupLine($"[bold]Step 2/2:[/] Analyzing solution (churn since {options.Since:yyyy-MM-dd})...");
            return await AnalyzeCommand.RunAnalysis(options, coverageResult, fileSystem, processRunner);
        }
        finally
        {
            // Best-effort cleanup of the temporary results directory
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Don't fail the command if cleanup fails
            }
        }
    }

    private static int RunTestWithLiveOutput(
        IProcessRunner processRunner, string executable, string args,
        string workingDir, AnalysisOptions options, int timeoutMs)
    {
        if (options.Quiet)
        {
            return processRunner.RunWithLiveOutput(executable, args, workingDir,
                timeoutMs: timeoutMs);
        }

        if (options.Verbose)
        {
            return processRunner.RunWithLiveOutput(executable, args, workingDir,
                line => AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line)}[/]"),
                timeoutMs);
        }

        // Spinner keeps animating so the user knows the tool isn't stuck.
        // Fall back to plain output if the console doesn't support interactive mode
        // (e.g. redirected output or concurrent Spectre.Console operations).
        var exitCode = 0;
        try
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Running tests...", ctx =>
                {
                    exitCode = processRunner.RunWithLiveOutput(executable, args, workingDir,
                        line => { if (!string.IsNullOrWhiteSpace(line)) ctx.Status(Markup.Escape(line)); },
                        timeoutMs);
                });
        }
        catch (InvalidOperationException)
        {
            exitCode = processRunner.RunWithLiveOutput(executable, args, workingDir,
                timeoutMs: timeoutMs);
        }
        return exitCode;
    }
}
