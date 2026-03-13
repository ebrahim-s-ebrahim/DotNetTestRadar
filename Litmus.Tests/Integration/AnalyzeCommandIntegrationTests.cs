using System.Text.Json;
using Litmus.Abstractions;
using Litmus.Commands;
using Litmus.Models;
using Litmus.Services;
using FluentAssertions;
using Spectre.Console;

namespace Litmus.Tests.Integration;

[Collection("AnsiConsole")]
public class AnalyzeCommandIntegrationTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _processRunner;

    public AnalyzeCommandIntegrationTests()
    {
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(TextWriter.Null)
        });
        _fixture = new IntegrationTestFixture();
        _fileSystem = new FileSystemWrapper();
        _processRunner = new ProcessRunner();
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<int> RunAnalysis(AnalysisOptions? overrides = null)
    {
        var options = overrides ?? new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true
        };

        var coverageParser = new CoverageParser(_fileSystem);
        var coverageResult = coverageParser.Parse(options.CoveragePath);

        return await AnalyzeCommand.RunAnalysis(options, coverageResult, _fileSystem, _processRunner);
    }

    private async Task<List<JsonElement>> RunAndReadJsonResults(AnalysisOptions? overrides = null)
    {
        var outputPath = Path.Combine(_fixture.RootDir, $"results-{Guid.NewGuid():N}.json");
        var options = overrides ?? new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true
        };
        options.OutputPath = outputPath;

        var coverageParser = new CoverageParser(_fileSystem);
        var coverageResult = coverageParser.Parse(options.CoveragePath);
        var exitCode = await AnalyzeCommand.RunAnalysis(options, coverageResult, _fileSystem, _processRunner);

        exitCode.Should().Be(0, "the analysis pipeline should succeed");

        var json = File.ReadAllText(outputPath);
        return JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    }

    [Fact]
    public async Task HappyPath_ProducesRankedResults()
    {
        var results = await RunAndReadJsonResults();

        // Should have exactly 3 source files (test project filtered out)
        results.Should().HaveCount(3);

        // OrderService has the highest RiskScore (most churn + lowest coverage + highest complexity).
        // However, its high dependency score (DateTime.Now, new HttpClient) lowers its StartingPriority,
        // so it may not rank #1 by priority — that's correct tool behavior.
        var orderService = results.First(r => r.GetProperty("file").GetString()!.Contains("OrderService"));
        var otherRiskScores = results
            .Where(r => !r.GetProperty("file").GetString()!.Contains("OrderService"))
            .Select(r => r.GetProperty("riskScore").GetDouble());

        orderService.GetProperty("riskScore").GetDouble()
            .Should().BeGreaterThanOrEqualTo(otherRiskScores.Max(),
                "OrderService has highest churn, lowest coverage, and highest complexity");
    }

    [Fact]
    public async Task RiskScores_AreNonZero_ForFilesWithChurn()
    {
        var results = await RunAndReadJsonResults();

        // All files were committed, so all should have some churn
        results.Should().OnlyContain(r => r.GetProperty("riskScore").GetDouble() >= 0);

        // Files with git history should have risk > 0 (they have churn and < 100% coverage)
        var orderService = results.First(r => r.GetProperty("file").GetString()!.Contains("OrderService"));
        orderService.GetProperty("riskScore").GetDouble().Should().BeGreaterThan(0);

        // SimpleService has 90% coverage — its risk should be lower than OrderService (10% coverage)
        var simpleService = results.First(r => r.GetProperty("file").GetString()!.Contains("SimpleService"));
        simpleService.GetProperty("riskScore").GetDouble()
            .Should().BeLessThan(orderService.GetProperty("riskScore").GetDouble());
    }

    [Fact]
    public async Task DependencySignals_DetectedCorrectly()
    {
        var results = await RunAndReadJsonResults();

        // OrderService uses DateTime.Now and new HttpClient() — infrastructure calls
        var orderService = results.First(r => r.GetProperty("file").GetString()!.Contains("OrderService"));
        orderService.GetProperty("infrastructureCalls").GetInt32().Should().BeGreaterThan(0);

        // DataAccess uses new SqlConnection() — counted as infrastructure call (Signal 1)
        var dataAccess = results.First(r => r.GetProperty("file").GetString()!.Contains("DataAccess"));
        dataAccess.GetProperty("infrastructureCalls").GetInt32().Should().BeGreaterThan(0);

        // SimpleService has no infra calls or instantiations
        var simpleService = results.First(r => r.GetProperty("file").GetString()!.Contains("SimpleService"));
        simpleService.GetProperty("infrastructureCalls").GetInt32().Should().Be(0);
        simpleService.GetProperty("directInstantiations").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ExcludePattern_FiltersFiles()
    {
        var results = await RunAndReadJsonResults(new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            ExcludePatterns = ["**/DataAccess*"]
        });

        results.Should().HaveCount(2);
        results.Should().NotContain(r => r.GetProperty("file").GetString()!.Contains("DataAccess"));
    }

    [Fact]
    public async Task JsonExport_ProducesValidOutput()
    {
        var results = await RunAndReadJsonResults();

        // Verify all 19 expected fields are present on each result
        var expectedFields = new[]
        {
            "file", "commits", "weightedChurn", "coverageRate",
            "cyclomaticComplexity", "riskScore", "riskLevel",
            "infrastructureCalls", "directInstantiations", "concreteConstructorParams",
            "staticCalls", "asyncSeamCalls", "concreteCasts", "isRegistrationFile",
            "rawCouplingScore", "couplingNorm",
            "couplingLevel", "startingPriority", "priorityLevel"
        };

        foreach (var result in results)
        {
            foreach (var field in expectedFields)
            {
                result.TryGetProperty(field, out _).Should().BeTrue($"field '{field}' should be present");
            }
        }
    }

    [Fact]
    public async Task CsvExport_ProducesValidOutput()
    {
        var outputPath = Path.Combine(_fixture.RootDir, $"results-{Guid.NewGuid():N}.csv");
        var options = new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            OutputPath = outputPath
        };

        var exitCode = await RunAnalysis(options);
        exitCode.Should().Be(0);

        var lines = File.ReadAllLines(outputPath);
        lines.Length.Should().BeGreaterThanOrEqualTo(4); // header + 3 data rows

        // Header should have 19 columns
        var headerColumns = lines[0].Split(',');
        headerColumns.Should().HaveCount(19);
        headerColumns[0].Should().Be("file");

        // Data rows should also have 19 columns
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cols = lines[i].Split(',');
            cols.Should().HaveCount(19, $"data row {i} should have 19 columns");
        }
    }

    [Fact]
    public async Task TestProject_IsExcludedFromResults()
    {
        var results = await RunAndReadJsonResults();

        results.Should().NotContain(r =>
            r.GetProperty("file").GetString()!.Contains("MyApp.Tests", StringComparison.OrdinalIgnoreCase));
        results.Should().NotContain(r =>
            r.GetProperty("file").GetString()!.Contains("SampleTest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FormatJson_WritesJsonToStdout()
    {
        var options = new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            Format = "json"
        };

        // Capture stdout
        var originalOut = Console.Out;
        string captured;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var exitCode = await RunAnalysis(options);
            exitCode.Should().Be(0);

            captured = sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Should be valid JSON with all 3 source files
        var results = JsonSerializer.Deserialize<List<JsonElement>>(captured)!;
        results.Should().HaveCount(3);

        // Verify expected fields
        var first = results[0];
        first.TryGetProperty("file", out _).Should().BeTrue();
        first.TryGetProperty("startingPriority", out _).Should().BeTrue();
        first.TryGetProperty("riskScore", out _).Should().BeTrue();
    }

    [Fact]
    public async Task FormatCsv_WritesCsvToStdout()
    {
        var options = new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            Format = "csv"
        };

        var originalOut = Console.Out;
        string captured;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var exitCode = await RunAnalysis(options);
            exitCode.Should().Be(0);

            captured = sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var lines = captured.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(4); // header + 3 data rows
        lines[0].Should().StartWith("file,");
        lines[0].Split(',').Should().HaveCount(19);
    }

    [Fact]
    public async Task Baseline_ComparesAgainstPreviousRun()
    {
        // First run: generate baseline JSON
        var baselinePath = Path.Combine(_fixture.RootDir, "baseline.json");
        var firstRunOptions = new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            OutputPath = baselinePath
        };

        var exitCode = await RunAnalysis(firstRunOptions);
        exitCode.Should().Be(0);
        File.Exists(baselinePath).Should().BeTrue();

        // Second run: use baseline, export new results with delta
        var resultsPath = Path.Combine(_fixture.RootDir, "results-with-delta.json");
        var secondRunOptions = new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            BaselinePath = baselinePath,
            OutputPath = resultsPath
        };

        exitCode = await RunAnalysis(secondRunOptions);
        exitCode.Should().Be(0);

        // Parse results — delta should be present and ~0 (same data)
        var json = File.ReadAllText(resultsPath);
        var results = JsonSerializer.Deserialize<List<JsonElement>>(json)!;

        foreach (var r in results)
        {
            r.TryGetProperty("delta", out var deltaEl).Should().BeTrue("delta field should be present with baseline");
            Math.Abs(deltaEl.GetDouble()).Should().BeLessThan(0.01,
                "delta should be near zero when comparing identical runs");
        }
    }

    [Fact]
    public async Task FormatTable_WithProgress_CompletesSuccessfully()
    {
        // Default format is "table" which triggers progress bars.
        // Verify the pipeline still completes correctly with progress enabled.
        var results = await RunAndReadJsonResults(new AnalysisOptions
        {
            SolutionPath = _fixture.SolutionPath,
            CoveragePath = _fixture.CoveragePath,
            Since = DateTime.Today.AddYears(-1),
            Top = 20,
            NoColor = true,
            Format = "table"
        });

        results.Should().HaveCount(3);
    }
}
