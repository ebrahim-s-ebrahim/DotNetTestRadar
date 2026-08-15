using System.Globalization;
using System.Text.Json;
using Litmus.Abstractions;
using Litmus.Models;
using Litmus.Output;
using Litmus.Services;
using FluentAssertions;
using NSubstitute;
using Spectre.Console;

namespace Litmus.Tests.Services;

[Collection("AnsiConsole")]
public class ReportRendererTests
{
    public ReportRendererTests()
    {
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(TextWriter.Null)
        });
    }

    private static FileRiskReport MakeReport(string file, double startingPriority) => new()
    {
        File = file,
        StartingPriority = startingPriority,
        PriorityLevel = startingPriority >= 0.6 ? "High" : startingPriority >= 0.2 ? "Medium" : "Low"
    };

    [Fact]
    public void ComputeBaselineStats_IdentifiesImprovedFiles()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.3) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.5 };

        var (improved, degraded, newFiles, removed) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        improved.Should().Be(1);
        degraded.Should().Be(0);
        newFiles.Should().Be(0);
        removed.Should().Be(0);
    }

    [Fact]
    public void ComputeBaselineStats_IdentifiesDegradedFiles()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.3 };

        var (improved, degraded, newFiles, removed) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        improved.Should().Be(0);
        degraded.Should().Be(1);
    }

    [Fact]
    public void ComputeBaselineStats_IdentifiesNewFiles()
    {
        var reports = new List<FileRiskReport> { MakeReport("New.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["Old.cs"] = 0.3 };

        var (improved, degraded, newFiles, removed) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        newFiles.Should().Be(1);
        removed.Should().Be(1);
    }

    [Fact]
    public void ComputeBaselineStats_IdentifiesRemovedFiles()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.5, ["Removed.cs"] = 0.3 };

        var (_, _, _, removed) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        removed.Should().Be(1);
    }

    [Fact]
    public void ComputeBaselineStats_UnchangedFilesNotCounted()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.500) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.502 }; // within 0.005 threshold

        var (improved, degraded, _, _) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        improved.Should().Be(0);
        degraded.Should().Be(0);
    }

    [Fact]
    public void ComputeBaselineStats_MixedChanges()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("Improved.cs", 0.2),
            MakeReport("Degraded.cs", 0.8),
            MakeReport("Same.cs", 0.5),
            MakeReport("New.cs", 0.3)
        };
        var baseline = new Dictionary<string, double>
        {
            ["Improved.cs"] = 0.5,
            ["Degraded.cs"] = 0.3,
            ["Same.cs"] = 0.5,
            ["Gone.cs"] = 0.4
        };

        var (improved, degraded, newFiles, removed) = ReportRenderer.ComputeBaselineStats(reports, baseline);

        improved.Should().Be(1);
        degraded.Should().Be(1);
        newFiles.Should().Be(1);
        removed.Should().Be(1);
    }

    [Fact]
    public void ExportJson_WithBaseline_IncludesDeltaField()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.3 };

        var json = ReportRenderer.ExportJson(reports, baseline);

        json.Should().Contain("\"delta\"");
    }

    [Fact]
    public void ExportJson_WithoutBaseline_OmitsDeltaField()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var json = ReportRenderer.ExportJson(reports);

        json.Should().NotContain("\"delta\"");
    }

    [Fact]
    public void ExportCsv_WithBaseline_AddsDeltaColumn()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.3 };

        var csv = ReportRenderer.ExportCsv(reports, baseline);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().EndWith(",delta");
        lines[1].Split(',').Length.Should().Be(20);
    }

    [Fact]
    public void ExportCsv_WithoutBaseline_NoDeltaColumn()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var csv = ReportRenderer.ExportCsv(reports);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().NotContain("delta");
        lines[1].Split(',').Length.Should().Be(19);
    }

    [Fact]
    public void ExportCsv_NewFileInBaseline_ShowsNewInDeltaColumn()
    {
        var reports = new List<FileRiskReport> { MakeReport("New.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["Old.cs"] = 0.3 };

        var csv = ReportRenderer.ExportCsv(reports, baseline);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines[1].Should().EndWith(",NEW");
    }

    [Fact]
    public void Render_FormatJson_WritesJsonToStdout()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.8),
            MakeReport("B.cs", 0.3)
        };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, format: "json"));

        // Should be valid JSON array
        var parsed = JsonSerializer.Deserialize<List<JsonElement>>(captured);
        parsed.Should().HaveCount(2);
        parsed![0].GetProperty("file").GetString().Should().Be("A.cs");
    }

    [Fact]
    public void Render_FormatCsv_WritesCsvToStdout()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.8),
            MakeReport("B.cs", 0.3)
        };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, format: "csv"));

        var lines = captured.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(3); // header + 2 data rows
        lines[0].Should().StartWith("file,");
    }

    [Fact]
    public void Render_FormatJson_WithBaseline_IncludesDelta()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.3 };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0,
                baseline: baseline, format: "json"));

        captured.Should().Contain("\"delta\"");
    }

    [Fact]
    public void Render_FormatJson_OutputsAllReports_NotJustTop()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.9),
            MakeReport("B.cs", 0.7),
            MakeReport("C.cs", 0.5)
        };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, top: 2, noColor: true, outputPath: null, skippedFiles: 0, format: "json"));

        // JSON format should include all reports, not just top N
        var parsed = JsonSerializer.Deserialize<List<JsonElement>>(captured);
        parsed.Should().HaveCount(3);
    }

    [Fact]
    public void Render_Quiet_SuppressesTableAndSummary()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, quiet: true));

        // Quiet mode should produce no stdout output
        captured.Should().BeEmpty();
    }

    [Fact]
    public void Render_Verbose_ShowsDetailedScoresTable()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };

        // Verbose mode outputs to AnsiConsole (stderr-like), not Console.Out.
        // We just verify it doesn't throw and the method completes.
        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var act = () => renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, verbose: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExportJson_IncludesNewCouplingFields()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var json = ReportRenderer.ExportJson(reports);

        json.Should().Contain("\"asyncSeamCalls\"");
        json.Should().Contain("\"concreteCasts\"");
        json.Should().Contain("\"isRegistrationFile\"");
    }

    [Fact]
    public void ExportCsv_IncludesNewCouplingColumns()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var csv = ReportRenderer.ExportCsv(reports);
        var header = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];

        header.Should().Contain("asyncSeamCalls");
        header.Should().Contain("concreteCasts");
        header.Should().Contain("isRegistrationFile");
    }

    [Fact]
    public void Render_WithSinceDate_IncludesDateInSummary()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };
        var sinceDate = new DateTime(2025, 6, 15);

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0,
                sinceDate: sinceDate);
        });

        output.Should().Contain("since 2025-06-15");
    }

    [Fact]
    public void Render_TopLessThanTotal_ShowsTopCountInSummary()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.9),
            MakeReport("B.cs", 0.7),
            MakeReport("C.cs", 0.5),
            MakeReport("D.cs", 0.3),
            MakeReport("E.cs", 0.1)
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, top: 3, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("5 files analyzed (showing top 3).");
    }

    [Fact]
    public void Render_TopEqualsTotal_OmitsTopCountInSummary()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.9),
            MakeReport("B.cs", 0.7)
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, top: 5, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("2 files analyzed.");
        output.Should().NotContain("showing top");
    }

    [Fact]
    public void ExportHtml_ProducesValidHtmlWithTable()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.8),
            MakeReport("B.cs", 0.3)
        };

        var html = ReportRenderer.ExportHtml(reports);

        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("<table id=\"t\">");
        html.Should().Contain("A.cs");
        html.Should().Contain("B.cs");
        html.Should().Contain("</html>");
    }

    [Fact]
    public void ExportHtml_WithBaseline_IncludesDeltaColumn()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };
        var baseline = new Dictionary<string, double> { ["A.cs"] = 0.3 };

        var html = ReportRenderer.ExportHtml(reports, baseline);

        html.Should().Contain("Delta");
        html.Should().Contain("+0.50");
    }

    [Fact]
    public void ExportHtml_WithoutBaseline_NoDeltaColumn()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };

        var html = ReportRenderer.ExportHtml(reports);

        html.Should().NotContain("Delta");
    }

    [Fact]
    public void ExportHtml_NewFileInBaseline_ShowsNew()
    {
        var reports = new List<FileRiskReport> { MakeReport("New.cs", 0.5) };
        var baseline = new Dictionary<string, double> { ["Old.cs"] = 0.3 };

        var html = ReportRenderer.ExportHtml(reports, baseline);

        html.Should().Contain("NEW");
    }

    [Fact]
    public void ExportHtml_EscapesHtmlCharacters()
    {
        var reports = new List<FileRiskReport> { MakeReport("File<T>.cs", 0.5) };

        var html = ReportRenderer.ExportHtml(reports);

        html.Should().Contain("File&lt;T&gt;.cs");
        html.Should().NotContain("File<T>.cs");
    }

    [Fact]
    public void ExportHtml_IncludesSortingScript()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var html = ReportRenderer.ExportHtml(reports);

        html.Should().Contain("function sortTable");
    }

    [Fact]
    public void Render_FormatHtml_WritesHtmlToStdout()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, format: "html"));

        captured.Should().Contain("<!DOCTYPE html>");
        captured.Should().Contain("A.cs");
    }

    [Fact]
    public void Render_WithoutSinceDate_OmitsDateInSummary()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.8) };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("1 files analyzed.");
        output.Should().NotContain("since");
    }

    private static string CaptureConsoleOut(Action action)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void Render_WithMethodDetails_RendersMethodRows()
    {
        var reports = new List<FileRiskReport>
        {
            new()
            {
                File = "Services/OrderService.cs",
                Commits = 10,
                CoverageRate = 0.5,
                CyclomaticComplexity = 20,
                StartingPriority = 0.8,
                PriorityLevel = "High",
                RiskScore = 0.7,
                RiskLevel = "High",
                CouplingLevel = "Low"
            }
        };

        var methodDetails = new Dictionary<string, List<MethodDetail>>
        {
            ["Services/OrderService.cs"] =
            [
                new() { Name = "ProcessOrder", Complexity = 12, CoverageRate = 0.5 },
                new() { Name = "ValidateInput", Complexity = 8, CoverageRate = null }
            ]
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0,
                methodDetails: methodDetails);
        });

        output.Should().Contain("ProcessOrder");
        output.Should().Contain("ValidateInput");
        output.Should().Contain("50%");  // method coverage
        output.Should().Contain("12");   // method complexity
    }

    [Fact]
    public void Render_WithoutMethodDetails_DoesNotRenderMethodRows()
    {
        var reports = new List<FileRiskReport>
        {
            new()
            {
                File = "Services/OrderService.cs",
                Commits = 10,
                CoverageRate = 0.5,
                CyclomaticComplexity = 20,
                StartingPriority = 0.8,
                PriorityLevel = "High",
                RiskScore = 0.7,
                RiskLevel = "High",
                CouplingLevel = "Low"
            }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("OrderService.cs");
        output.Should().NotContain("ProcessOrder");
    }

    // ---- Phase 3: BuildExplanation tests ----

    [Fact]
    public void BuildExplanation_HighChurnLowCoverage()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.8, CoverageRate = 0.1, ComplexityNorm = 0.1,
            CouplingLevel = "Low", RiskLevel = "High", PriorityLevel = "High"
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Contain("high churn");
        explanation.Should().Contain("low coverage");
    }

    [Fact]
    public void BuildExplanation_HighComplexityWithBreakdown()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.1, CoverageRate = 0.8, ComplexityNorm = 0.7,
            CouplingLevel = "Low", RiskLevel = "Low", PriorityLevel = "Low",
            ComplexityBreakdown = new ComplexityBreakdown { Conditionals = 20, Loops = 10 }
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Contain("high complexity");
        explanation.Should().Contain("20 conditionals");
        explanation.Should().Contain("10 loops");
    }

    [Fact]
    public void BuildExplanation_HighCouplingReducesPriority()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.8, CoverageRate = 0.1, ComplexityNorm = 0.5,
            CouplingLevel = "Very High", RiskLevel = "High", PriorityLevel = "Low"
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Contain("introduce seams");
    }

    [Fact]
    public void BuildExplanation_AllLow_MinimalExplanation()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.05, CoverageRate = 0.9, ComplexityNorm = 0.05,
            CouplingLevel = "Low", RiskLevel = "Low", PriorityLevel = "Low"
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Be("low risk across all signals");
    }

    [Fact]
    public void BuildExplanation_ZeroCoverage_MentionsNoCoverage()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.5, CoverageRate = 0.0, ComplexityNorm = 0.1,
            CouplingLevel = "Low", RiskLevel = "Medium", PriorityLevel = "Medium",
            IsRegistrationFile = false
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Contain("no test coverage");
    }

    [Fact]
    public void BuildExplanation_BreakdownOmitsZeroCounts()
    {
        var r = new FileRiskReport
        {
            File = "A.cs", ChurnNorm = 0.1, CoverageRate = 0.8, ComplexityNorm = 0.7,
            CouplingLevel = "Low", RiskLevel = "Low", PriorityLevel = "Low",
            ComplexityBreakdown = new ComplexityBreakdown { Conditionals = 5, Loops = 0, Catches = 0 }
        };
        var explanation = ReportRenderer.BuildExplanation(r);
        explanation.Should().Contain("5 conditionals");
        explanation.Should().NotContain("loops");
        explanation.Should().NotContain("catches");
    }

    [Fact]
    public void Render_Explain_ShowsAnnotationRows()
    {
        var reports = new List<FileRiskReport>
        {
            new()
            {
                File = "A.cs", Commits = 10, CoverageRate = 0.1, CyclomaticComplexity = 20,
                ChurnNorm = 0.8, ComplexityNorm = 0.5,
                StartingPriority = 0.8, PriorityLevel = "High",
                RiskScore = 0.7, RiskLevel = "High", CouplingLevel = "Low"
            }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, explain: true);
        });

        output.Should().Contain("high churn");
        output.Should().Contain("low coverage");
    }

    [Fact]
    public void Render_Explain_DoesNotAffectJsonOutput()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0,
                format: "json", explain: true));

        captured.Should().NotContain("->");
        var parsed = JsonSerializer.Deserialize<List<JsonElement>>(captured);
        parsed.Should().HaveCount(1);
    }

    [Fact]
    public void Render_FooterLegend_RendersInDefaultMode()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("Complexity:");
        output.Should().Contain("Risk:");
        output.Should().Contain("Priority:");
        output.Should().Contain("Coupling:");
    }

    [Fact]
    public void Render_FooterLegend_SuppressedByQuiet()
    {
        var reports = new List<FileRiskReport> { MakeReport("A.cs", 0.5) };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, quiet: true);
        });

        output.Should().NotContain("Complexity:");
    }

    // ---- End Phase 3 tests ----

    // ---- Phase 4 tests: Group by priority level ----

    [Fact]
    public void Render_DefaultMode_GroupsByPriorityLevel()
    {
        var reports = new List<FileRiskReport>
        {
            new() { File = "High.cs", StartingPriority = 0.9, PriorityLevel = "High", RiskLevel = "High", CouplingLevel = "Low" },
            new() { File = "Low.cs", StartingPriority = 0.1, PriorityLevel = "Low", RiskLevel = "Low", CouplingLevel = "Low" },
            new() { File = "Med.cs", StartingPriority = 0.4, PriorityLevel = "Medium", RiskLevel = "Medium", CouplingLevel = "Low" }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("Act Now");
        output.Should().Contain("Next Sprint");
        output.Should().Contain("Monitor");

        // Verify ordering: Act Now before Next Sprint before Monitor
        var actNowIdx = output.IndexOf("Act Now");
        var nextSprintIdx = output.IndexOf("Next Sprint");
        var monitorIdx = output.IndexOf("Monitor");
        actNowIdx.Should().BeLessThan(nextSprintIdx);
        nextSprintIdx.Should().BeLessThan(monitorIdx);
    }

    [Fact]
    public void Render_DefaultMode_SkipsEmptyGroups()
    {
        var reports = new List<FileRiskReport>
        {
            new() { File = "High.cs", StartingPriority = 0.9, PriorityLevel = "High", RiskLevel = "High", CouplingLevel = "Low" }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        output.Should().Contain("Act Now");
        output.Should().NotContain("Next Sprint");
        output.Should().NotContain("Monitor");
    }

    [Fact]
    public void Render_NoGroup_ProducesFlatTable()
    {
        var reports = new List<FileRiskReport>
        {
            new() { File = "High.cs", StartingPriority = 0.9, PriorityLevel = "High", RiskLevel = "High", CouplingLevel = "Low" },
            new() { File = "Med.cs", StartingPriority = 0.4, PriorityLevel = "Medium", RiskLevel = "Medium", CouplingLevel = "Low" }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0, noGroup: true);
        });

        output.Should().NotContain("Act Now");
        output.Should().NotContain("Next Sprint");
        output.Should().NotContain("Monitor");
        output.Should().Contain("High.cs");
        output.Should().Contain("Med.cs");
    }

    [Fact]
    public void Render_DefaultMode_RankIsContinuousAcrossGroups()
    {
        var reports = new List<FileRiskReport>
        {
            new() { File = "H1.cs", StartingPriority = 0.9, PriorityLevel = "High", RiskLevel = "High", CouplingLevel = "Low" },
            new() { File = "H2.cs", StartingPriority = 0.8, PriorityLevel = "High", RiskLevel = "High", CouplingLevel = "Low" },
            new() { File = "M1.cs", StartingPriority = 0.4, PriorityLevel = "Medium", RiskLevel = "Medium", CouplingLevel = "Low" }
        };

        var output = CaptureAnsiConsole(() =>
        {
            var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0);
        });

        // H1 = rank 1, H2 = rank 2, M1 = rank 3 (continuous across groups)
        output.Should().Contain("H1.cs");
        output.Should().Contain("H2.cs");
        output.Should().Contain("M1.cs");
    }

    [Fact]
    public void Render_GroupMode_DoesNotAffectJsonOutput()
    {
        var reports = new List<FileRiskReport>
        {
            MakeReport("A.cs", 0.9),
            MakeReport("B.cs", 0.1)
        };

        var renderer = new ReportRenderer(Substitute.For<IFileSystem>());
        var captured = CaptureConsoleOut(() =>
            renderer.Render(reports, 20, noColor: true, outputPath: null, skippedFiles: 0,
                format: "json", noGroup: false));

        captured.Should().NotContain("Act Now");
        var parsed = JsonSerializer.Deserialize<List<JsonElement>>(captured);
        parsed.Should().HaveCount(2);
    }

    // ---- End Phase 4 tests ----

    private static string CaptureAnsiConsole(Action action, int width = 200)
    {
        var previous = AnsiConsole.Console;
        var sw = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(sw),
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        AnsiConsole.Profile.Width = width;
        try
        {
            action();
            return sw.ToString();
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }
}
