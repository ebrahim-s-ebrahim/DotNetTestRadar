using System.CommandLine;
using System.Text.Json;
using Litmus.Abstractions;
using Litmus.Models;
using Litmus.Output;
using Litmus.Services;
using Spectre.Console;

namespace Litmus.Commands;

public class AnalyzeCommand
{
    public static Command Create(IFileSystem fileSystem, IProcessRunner processRunner)
    {
        var solutionOption = new Option<string?>("--solution")
        {
            Description = "Path to a .sln or .slnx file. Auto-detected if a single .sln/.slnx exists in the current directory."
        };

        var coverageOption = new Option<string>("--coverage")
        {
            Description = "Path to a Cobertura XML coverage file",
            Required = true
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

        var command = new Command("analyze", "Analyze .NET solution for high-risk files and starting priority")
        {
            solutionOption,
            coverageOption,
            sinceOption,
            topOption,
            excludeOption,
            outputOption,
            baselineOption,
            noColorOption,
            formatOption,
            verboseOption,
            quietOption,
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
                CoveragePath = parseResult.GetValue(coverageOption)!,
                Since = parseResult.GetValue(sinceOption) ?? DateTime.Today.AddYears(-1),
                Top = parseResult.GetValue(topOption),
                ExcludePatterns = parseResult.GetValue(excludeOption)?.ToList() ?? [],
                OutputPath = parseResult.GetValue(outputOption),
                BaselinePath = parseResult.GetValue(baselineOption),
                NoColor = parseResult.GetValue(noColorOption),
                Format = parseResult.GetValue(formatOption)!,
                Verbose = parseResult.GetValue(verboseOption),
                Quiet = parseResult.GetValue(quietOption),
                Detailed = parseResult.GetValue(detailedOption),
                Explain = parseResult.GetValue(explainOption),
                NoGroup = parseResult.GetValue(noGroupOption),
                FailOnThreshold = parseResult.GetValue(failOnThresholdOption)
            };

            return Execute(options, fileSystem, processRunner);
        });

        return command;
    }

    private static async Task<int> Execute(AnalysisOptions options, IFileSystem fileSystem, IProcessRunner processRunner)
    {
        if (options.Verbose && options.Quiet)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --verbose and --quiet cannot be used together.");
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

        // Validate coverage file
        if (!fileSystem.FileExists(options.CoveragePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Coverage file not found: " + options.CoveragePath);
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

        // Parse coverage from the provided file
        var coverageParser = new CoverageParser(fileSystem);
        CoverageResult coverageResult;
        try
        {
            coverageResult = coverageParser.Parse(options.CoveragePath);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }

        return await RunAnalysis(options, coverageResult, fileSystem, processRunner);
    }

    /// <summary>
    /// Runs the core analysis pipeline (solution parsing → churn | complexity | dependency →
    /// scoring → rendering) using an already-parsed coverage result. Called by both
    /// <see cref="AnalyzeCommand"/> (coverage from file) and <see cref="ScanCommand"/>
    /// (coverage auto-generated from dotnet test).
    /// Analyzers run in parallel for performance.
    /// </summary>
    internal static async Task<int> RunAnalysis(
        AnalysisOptions options,
        CoverageResult coverageResult,
        IFileSystem fileSystem,
        IProcessRunner processRunner)
    {
        try
        {
            // Step 0: Parse solution
            var solutionParser = new SolutionParser(fileSystem, processRunner);
            SolutionParseResult parseResult;
            try
            {
                parseResult = solutionParser.Parse(options.SolutionPath);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("rev-parse") || ex.Message.Contains("git repository"))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] The solution path must be inside a git repository.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                return 1;
            }

            if (parseResult.ProjectDirectories.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Warning:[/] No project directories found in the solution.");
                return 0;
            }

            // Compute effective exclusion patterns once for all analyzers
            var effectivePatterns = FileFilterHelper.GetEffectivePatterns(options.ExcludePatterns);

            // Steps 1-3: Run churn, complexity, and dependency analyzers in parallel
            var churnAnalyzer = new GitChurnAnalyzer(processRunner);
            var complexityAnalyzer = new ComplexityAnalyzer(fileSystem);
            var dependencyAnalyzer = new DependencyAnalyzer(fileSystem);

            // Pre-count .cs files for progress tracking
            var totalFiles = 0;
            foreach (var projectDir in parseResult.ProjectDirectories)
            {
                var fullDir = Path.Combine(parseResult.GitRoot, projectDir);
                if (fileSystem.DirectoryExists(fullDir))
                    totalFiles += fileSystem.GetFiles(fullDir, "*.cs", recursive: true).Count();
            }

            ChurnResult churnResult = null!;
            ComplexityResult complexityResult = null!;
            DependencyResult dependencyResult = null!;

            if (options.Format == "table" && totalFiles > 0 && !options.Quiet)
            {
                await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(false)
                    .StartAsync(async ctx =>
                    {
                        var churnProgress = ctx.AddTask("Git churn", maxValue: 1);
                        var complexityProgress = ctx.AddTask("Complexity analysis", maxValue: totalFiles);
                        var dependencyProgress = ctx.AddTask("Coupling analysis", maxValue: totalFiles);

                        var ct = Task.Run(() =>
                        {
                            var result = churnAnalyzer.Analyze(
                                parseResult.GitRoot,
                                parseResult.SolutionDirectory,
                                parseResult.ProjectDirectories,
                                options.Since,
                                options.ExcludePatterns);
                            churnProgress.Increment(1);
                            return result;
                        });

                        var cxt = Task.Run(() => complexityAnalyzer.Analyze(
                            parseResult.GitRoot,
                            parseResult.ProjectDirectories,
                            effectivePatterns,
                            () => complexityProgress.Increment(1)));

                        var dt = Task.Run(() => dependencyAnalyzer.Analyze(
                            parseResult.GitRoot,
                            parseResult.ProjectDirectories,
                            effectivePatterns,
                            () => dependencyProgress.Increment(1)));

                        await Task.WhenAll(ct, cxt, dt);

                        churnResult = ct.Result;
                        complexityResult = cxt.Result;
                        dependencyResult = dt.Result;
                    });
            }
            else
            {
                var ct = Task.Run(() => churnAnalyzer.Analyze(
                    parseResult.GitRoot,
                    parseResult.SolutionDirectory,
                    parseResult.ProjectDirectories,
                    options.Since,
                    options.ExcludePatterns));

                var cxt = Task.Run(() => complexityAnalyzer.Analyze(
                    parseResult.GitRoot,
                    parseResult.ProjectDirectories,
                    effectivePatterns));

                var dt = Task.Run(() => dependencyAnalyzer.Analyze(
                    parseResult.GitRoot,
                    parseResult.ProjectDirectories,
                    effectivePatterns));

                await Task.WhenAll(ct, cxt, dt);

                churnResult = ct.Result;
                complexityResult = cxt.Result;
                dependencyResult = dt.Result;
            }

            // Step 4: Risk scoring + starting priority
            var riskScorer = new RiskScorer();
            var reports = riskScorer.Score(
                churnResult,
                coverageResult,
                complexityResult,
                dependencyResult,
                parseResult.SolutionDirectory,
                parseResult.GitRoot);

            if (reports.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Warning:[/] No .cs files found after applying all filters.");
                return 0;
            }

            // Load baseline if provided
            Dictionary<string, double>? baseline = null;
            if (options.BaselinePath != null)
            {
                try
                {
                    var baselineJson = fileSystem.ReadAllText(options.BaselinePath);
                    var items = JsonSerializer.Deserialize<List<JsonElement>>(baselineJson)!;
                    baseline = items.ToDictionary(
                        e => e.GetProperty("file").GetString()!,
                        e => e.GetProperty("startingPriority").GetDouble());
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Failed to load baseline: {ex.Message.EscapeMarkup()}");
                    return 1;
                }
            }

            // Identify files with no coverage entry (before rendering, so we can warn early)
            var filesWithNoCoverageEntry = reports
                .Where(r => r.CoverageRate == 0.0 &&
                       !coverageResult.FileCoverage.Keys.Any(k =>
                           k.EndsWith(r.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                           r.File.Replace('\\', '/').EndsWith(k, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.File)
                .ToList();

            // Early warning if most files have no coverage entry — likely a configuration issue
            if (!options.Quiet && !options.NoCoverage && filesWithNoCoverageEntry.Count > 0)
            {
                var mismatchPct = (double)filesWithNoCoverageEntry.Count / reports.Count * 100;
                if (mismatchPct > 25)
                {
                    AnsiConsole.MarkupLine(
                        $"\n[bold yellow]Warning:[/] {filesWithNoCoverageEntry.Count} of {reports.Count} files ({mismatchPct:F0}%) had no entry in the coverage report.\n" +
                        "This usually means the coverage file doesn't match the solution structure.\n" +
                        "Check that the coverage was generated from the same solution.\n");
                }
            }

            // Step 5: Build method details for --detailed mode
            Dictionary<string, List<MethodDetail>>? methodDetails = null;
            if (options.Detailed && options.Format == "table")
            {
                methodDetails = new Dictionary<string, List<MethodDetail>>();
                var solutionRelPath = Path.GetRelativePath(parseResult.GitRoot, parseResult.SolutionDirectory)
                    .Replace('\\', '/');
                var detailCount = Math.Min(5, options.Top);

                foreach (var report in reports.Take(detailCount))
                {
                    var gitRootRelPath = report.File;
                    if (solutionRelPath != ".")
                        gitRootRelPath = solutionRelPath + "/" + report.File;

                    var methodComplexities = complexityResult.MethodComplexity
                        .GetValueOrDefault(gitRootRelPath, []);

                    var methodCoverages = CoverageParser.GetMethodCoverageForFile(coverageResult, report.File);
                    var coverageLookup = new Dictionary<string, double>();
                    foreach (var (name, rate) in methodCoverages)
                        coverageLookup[name] = rate;

                    var details = new List<MethodDetail>();
                    foreach (var (name, complexity) in methodComplexities)
                    {
                        details.Add(new MethodDetail
                        {
                            Name = name,
                            Complexity = complexity,
                            CoverageRate = coverageLookup.TryGetValue(name, out var rate) ? rate : null
                        });
                    }

                    details = details.OrderByDescending(m => m.Complexity).ToList();

                    if (details.Count > 0)
                        methodDetails[report.File] = details;
                }
            }

            // Step 6: Render
            var renderer = new ReportRenderer(fileSystem);
            var totalSkippedFiles = complexityResult.SkippedFiles + dependencyResult.SkippedFiles;
            renderer.Render(reports, options.Top, options.NoColor, options.OutputPath, totalSkippedFiles, baseline,
                options.Format, options.Verbose, options.Quiet, options.Since, methodDetails,
                options.Explain, options.NoGroup);

            if (!options.Quiet && options.NoCoverage)
            {
                AnsiConsole.MarkupLine(
                    "\n[dim]Coverage skipped — all files treated as 0%. " +
                    "Add tests and re-run without --no-coverage for full analysis.[/]");
            }

            if (!options.Quiet && !options.NoCoverage)
            {
                // List individual files with no coverage entry (post-table detail)
                if (filesWithNoCoverageEntry.Count > 0)
                {
                    AnsiConsole.MarkupLine($"\n[yellow]Note:[/] {filesWithNoCoverageEntry.Count} file(s) had no entry in the coverage report (treated as 0% coverage):");
                    foreach (var f in filesWithNoCoverageEntry.Take(10))
                    {
                        AnsiConsole.MarkupLine($"  - {f.EscapeMarkup()}");
                    }
                    if (filesWithNoCoverageEntry.Count > 10)
                        AnsiConsole.MarkupLine($"  ... and {filesWithNoCoverageEntry.Count - 10} more.");
                }
            }

            // --fail-on-threshold: exit 1 if any file exceeds the threshold
            if (options.FailOnThreshold.HasValue)
            {
                var threshold = options.FailOnThreshold.Value;
                var failing = reports
                    .Where(r => r.RiskScore > threshold || r.StartingPriority > threshold)
                    .ToList();

                if (failing.Count > 0)
                {
                    if (!options.Quiet)
                    {
                        AnsiConsole.MarkupLine(
                            $"\n[red]Threshold exceeded:[/] {failing.Count} file(s) have a Risk Score or Starting Priority above {threshold:F2}.");
                    }
                    return 1;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }
}
