using System.Globalization;
using System.Text;
using System.Text.Json;
using Litmus.Abstractions;
using Litmus.Models;
using Litmus.Services;
using Spectre.Console;
using System.Collections.Generic;

namespace Litmus.Output;

public class ReportRenderer
{
    private readonly IFileSystem _fileSystem;

    public ReportRenderer(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public void Render(List<FileRiskReport> reports, int top, bool noColor, string? outputPath, int skippedFiles,
        Dictionary<string, double>? baseline = null, string format = "table",
        bool verbose = false, bool quiet = false, DateTime? sinceDate = null,
        Dictionary<string, List<MethodDetail>>? methodDetails = null,
        bool explain = false, bool noGroup = false)
    {
        // Structured stdout formats: write JSON, CSV, or HTML to Console.Out and skip the table
        if (format is "json" or "csv" or "html")
        {
            var content = format switch
            {
                "json" => ExportJson(reports, baseline),
                "csv" => ExportCsv(reports, baseline),
                "html" => ExportHtml(reports, baseline),
                _ => throw new ArgumentException($"Unsupported format: {format}")
            };
            Console.Out.Write(content);

            // Still export to file if --output was also provided
            if (outputPath != null)
                ExportResults(reports, outputPath, baseline);

            return;
        }

        // Quiet mode: skip all table/summary output, only do file export
        if (quiet)
        {
            if (outputPath != null)
                ExportResults(reports, outputPath, baseline);
            return;
        }

        var topReports = reports.Take(top).ToList();
        var hasBaseline = baseline != null;

        if (noGroup)
        {
            // Flat table (--no-group)
            var table = CreateTable(hasBaseline);
            for (var i = 0; i < topReports.Count; i++)
                AddFileRow(table, topReports[i], i + 1, noColor, hasBaseline, baseline, methodDetails, explain);
            AnsiConsole.Write(table);
        }
        else
        {
            // Grouped by priority level (default)
            var groups = new (string label, string level)[]
            {
                ("Act Now", "High"),
                ("Next Sprint", "Medium"),
                ("Monitor", "Low")
            };

            var rank = 0;
            foreach (var (label, level) in groups)
            {
                var groupReports = topReports.Where(r => r.PriorityLevel == level).ToList();
                if (groupReports.Count == 0) continue;

                AnsiConsole.Write(new Rule($"[bold]{label}[/]").LeftJustified());

                var table = CreateTable(hasBaseline);
                foreach (var r in groupReports)
                {
                    rank++;
                    AddFileRow(table, r, rank, noColor, hasBaseline, baseline, methodDetails, explain);
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
            }
        }

        var highPriorityCount = reports.Count(r => r.PriorityLevel == "High");
        var mediumPriorityCount = reports.Count(r => r.PriorityLevel == "Medium");
        var highRiskNeedSeams = reports.Count(r => r.RiskLevel == "High" && r.PriorityLevel != "High");

        var sinceStr = sinceDate.HasValue ? $" since {sinceDate.Value:yyyy-MM-dd}" : "";
        var topStr = top < reports.Count ? $" (showing top {top})" : "";
        var summary = $"{reports.Count} files analyzed{sinceStr}{topStr}. {highPriorityCount} high-priority (start today), {mediumPriorityCount} medium-priority (next sprint).";
        if (highRiskNeedSeams > 0)
            summary += $" {highRiskNeedSeams} high-risk file(s) need seam introduction before testing.";
        if (skippedFiles > 0)
            summary += $" {skippedFiles} file(s) skipped.";

        AnsiConsole.MarkupLine(summary);

        // Baseline comparison summary
        if (hasBaseline)
        {
            RenderBaselineSummary(reports, baseline!);
        }

        // Footer legend
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Complexity: sum of conditionals, loops, catches, switch cases, logical ops (&&/||/??) per file[/]");
        AnsiConsole.MarkupLine("[dim]Risk:       churn x coverage gaps x complexity \u2014 Low | Medium | High[/]");
        AnsiConsole.MarkupLine("[dim]Priority:   risk adjusted for coupling \u2014 Low | Medium | High[/]");
        AnsiConsole.MarkupLine("[dim]Coupling:   how tightly bound to concrete dependencies \u2014 Low | Medium | High | Very High[/]");

        // Verbose: show detailed intermediate scores
        if (verbose)
        {
            AnsiConsole.WriteLine();
            var detailTable = new Table();
            detailTable.Border(TableBorder.Simple);
            detailTable.AddColumn("File");
            detailTable.AddColumn("ChurnNorm");
            detailTable.AddColumn("Coverage");
            detailTable.AddColumn("CplxNorm");
            detailTable.AddColumn("Infra");
            detailTable.AddColumn("New");
            detailTable.AddColumn("Params");
            detailTable.AddColumn("Static");
            detailTable.AddColumn("Async");
            detailTable.AddColumn("Casts");
            detailTable.AddColumn("CplScore");
            detailTable.AddColumn("CplNorm");
            detailTable.AddColumn("RegFile");
            detailTable.AddColumn("Risk");
            detailTable.AddColumn("Priority");

            foreach (var r in topReports)
            {
                detailTable.AddRow(
                    r.File.EscapeMarkup(),
                    $"{r.ChurnNorm:F4}",
                    $"{r.CoverageRate:F4}",
                    $"{r.ComplexityNorm:F4}",
                    r.InfrastructureCalls.ToString(),
                    r.DirectInstantiations.ToString(),
                    r.ConcreteConstructorParams.ToString(),
                    r.StaticCalls.ToString(),
                    r.AsyncSeamCalls.ToString(),
                    r.ConcreteCasts.ToString(),
                    $"{r.RawCouplingScore:F2}",
                    $"{r.CouplingNorm:F4}",
                    r.IsRegistrationFile ? "Yes" : "",
                    $"{r.RiskScore:F2}",
                    $"{r.StartingPriority:F2}");
            }

            AnsiConsole.MarkupLine("[bold]Detailed Scores[/]");
            AnsiConsole.Write(detailTable);
        }

        if (outputPath != null)
        {
            ExportResults(reports, outputPath, baseline);
        }
    }

    private static Table CreateTable(bool hasBaseline)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Rank");
        table.AddColumn("File");
        table.AddColumn("Commits");
        table.AddColumn("Coverage");
        table.AddColumn("Complexity");
        table.AddColumn("Coupling");
        table.AddColumn("Risk");
        table.AddColumn("Priority");
        if (hasBaseline)
            table.AddColumn("Delta");
        return table;
    }

    private static void AddFileRow(Table table, FileRiskReport r, int rank, bool noColor,
        bool hasBaseline, Dictionary<string, double>? baseline,
        Dictionary<string, List<MethodDetail>>? methodDetails, bool explain)
    {
        var coverageStr = $"{r.CoverageRate * 100:F0}%";
        var columnCount = hasBaseline ? 9 : 8;

        // Row color is driven by PriorityLevel
        var rowStyle = r.PriorityLevel switch
        {
            "High" when !noColor => "red",
            "Medium" when !noColor => "yellow",
            _ => "default"
        };

        // Risk column gets independent coloring
        var riskStyle = r.RiskLevel switch
        {
            "High" when !noColor => "red",
            "Medium" when !noColor => "yellow",
            _ => rowStyle
        };

        // Coupling level styled to indicate cost of introducing seams
        var cplStyle = r.CouplingLevel switch
        {
            "Very High" when !noColor => "red",
            "High" when !noColor => "yellow",
            _ => rowStyle
        };

        var columns = new List<Markup>
        {
            new($"[{rowStyle}]{rank}[/]"),
            new($"[{rowStyle}]{r.File.EscapeMarkup()}[/]"),
            new($"[{rowStyle}]{r.Commits}[/]"),
            new($"[{rowStyle}]{coverageStr}[/]"),
            new($"[{rowStyle}]{r.CyclomaticComplexity}[/]"),
            new($"[{cplStyle}]{r.CouplingLevel}[/]"),
            new($"[{riskStyle}]{r.RiskLevel}[/]"),
            new($"[{rowStyle}]{r.PriorityLevel}[/]")
        };

        if (hasBaseline)
        {
            var deltaMarkup = FormatDelta(r.File, r.StartingPriority, baseline!, noColor);
            columns.Add(deltaMarkup);
        }

        table.AddRow(columns);

        // Method-level detail rows (--detailed)
        if (methodDetails != null && methodDetails.TryGetValue(r.File, out var methods))
        {
            foreach (var method in methods)
            {
                var methodCoverage = method.CoverageRate.HasValue
                    ? $"{method.CoverageRate.Value * 100:F0}%"
                    : "\u2014";

                var methodColumns = new List<Markup>
                {
                    new(""),
                    new($"[dim]  {method.Name.EscapeMarkup()}[/]"),
                    new($"[dim]\u2014[/]"),
                    new($"[dim]{methodCoverage}[/]"),
                    new($"[dim]{method.Complexity}[/]"),
                    new(""),
                    new(""),
                    new("")
                };

                if (hasBaseline)
                    methodColumns.Add(new Markup(""));

                table.AddRow(methodColumns);
            }
        }

        // Explanation row (--explain)
        if (explain)
        {
            var explanation = BuildExplanation(r);
            var explainColumns = new List<Markup> { new("") };
            // Span explanation across remaining columns
            for (var c = 1; c < columnCount; c++)
                explainColumns.Add(c == 1 ? new Markup($"[dim italic]  {explanation.EscapeMarkup()}[/]") : new Markup(""));
            table.AddRow(explainColumns);
        }
    }

    internal static Markup FormatDelta(string file, double currentPriority, Dictionary<string, double> baseline, bool noColor)
    {
        if (!baseline.TryGetValue(file, out var previousPriority))
            return new Markup(noColor ? "NEW" : "[dim]NEW[/]");

        var delta = currentPriority - previousPriority;

        if (Math.Abs(delta) < 0.005)
            return new Markup("\u2014"); // em dash

        var sign = delta > 0 ? "+" : "";
        var deltaStr = $"{sign}{delta:F2}";

        if (noColor)
            return new Markup(deltaStr);

        // Positive delta = priority went up = file got worse = red
        // Negative delta = priority went down = file improved = green
        var style = delta > 0 ? "red" : "green";
        return new Markup($"[{style}]{deltaStr}[/]");
    }

    internal static string BuildExplanation(FileRiskReport r)
    {
        var drivers = new List<string>();

        // Churn signal
        if (r.ChurnNorm >= 0.5) drivers.Add("high churn");
        else if (r.ChurnNorm >= 0.2) drivers.Add("moderate churn");

        // Coverage signal
        if (r.CoverageRate == 0.0 && !r.IsRegistrationFile)
            drivers.Add("no test coverage");
        else if (r.CoverageRate < 0.3) drivers.Add("low coverage");
        else if (r.CoverageRate < 0.6) drivers.Add("moderate coverage");

        // Complexity signal with breakdown
        string? complexityStr = null;
        if (r.ComplexityNorm >= 0.5) complexityStr = "high complexity";
        else if (r.ComplexityNorm >= 0.2) complexityStr = "moderate complexity";

        if (complexityStr != null)
        {
            var breakdownParts = FormatComplexityBreakdown(r.ComplexityBreakdown);
            if (breakdownParts != null)
                complexityStr += $" ({breakdownParts})";
            drivers.Add(complexityStr);
        }

        var result = string.Join(" + ", drivers);

        // Coupling context
        if (r.CouplingLevel is "High" or "Very High")
        {
            var levelRank = r.PriorityLevel switch { "High" => 2, "Medium" => 1, _ => 0 };
            var riskRank = r.RiskLevel switch { "High" => 2, "Medium" => 1, _ => 0 };
            if (levelRank < riskRank)
                result += $"; {r.CouplingLevel.ToLowerInvariant()} coupling — introduce seams before testing";
            else
                result += $"; {r.CouplingLevel.ToLowerInvariant()} coupling reduces priority";
        }

        return result.Length > 0 ? result : "low risk across all signals";
    }

    private static string? FormatComplexityBreakdown(ComplexityBreakdown? breakdown)
    {
        if (breakdown == null) return null;

        var parts = new List<(string label, int count)>();
        if (breakdown.Conditionals > 0) parts.Add(("conditionals", breakdown.Conditionals));
        if (breakdown.Loops > 0) parts.Add(("loops", breakdown.Loops));
        if (breakdown.LogicalOps > 0) parts.Add(("logical ops", breakdown.LogicalOps));
        if (breakdown.Switches > 0) parts.Add(("switch cases", breakdown.Switches));
        if (breakdown.Catches > 0) parts.Add(("catches", breakdown.Catches));

        if (parts.Count == 0) return null;

        // Show top 3 by count
        var top = parts.OrderByDescending(p => p.count).Take(3);
        return string.Join(", ", top.Select(p => $"{p.count} {p.label}"));
    }

    internal static (int Improved, int Degraded, int New, int Removed) ComputeBaselineStats(
        List<FileRiskReport> reports, Dictionary<string, double> baseline)
    {
        var improved = 0;
        var degraded = 0;
        var newFiles = 0;

        foreach (var r in reports)
        {
            if (!baseline.TryGetValue(r.File, out var prev))
            {
                newFiles++;
                continue;
            }

            var delta = r.StartingPriority - prev;
            if (delta < -0.005) improved++;
            else if (delta > 0.005) degraded++;
        }

        var removedFiles = baseline.Keys.Count(k => reports.All(r => r.File != k));

        return (improved, degraded, newFiles, removedFiles);
    }

    private static void RenderBaselineSummary(List<FileRiskReport> reports, Dictionary<string, double> baseline)
    {
        var (improved, degraded, newFiles, removed) = ComputeBaselineStats(reports, baseline);
        AnsiConsole.MarkupLine($"vs baseline: {improved} improved, {degraded} degraded, {newFiles} new, {removed} removed.");
    }

    private void ExportResults(List<FileRiskReport> reports, string outputPath, Dictionary<string, double>? baseline)
    {
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        var content = extension switch
        {
            ".json" => ExportJson(reports, baseline),
            ".csv" => ExportCsv(reports, baseline),
            ".html" or ".htm" => ExportHtml(reports, baseline),
            _ => throw new ArgumentException($"Unsupported output format: {extension}. Use .json, .csv, or .html.")
        };

        File.WriteAllText(outputPath, content);
        AnsiConsole.MarkupLine($"Results exported to [bold]{outputPath.EscapeMarkup()}[/]");
    }

    internal static string ExportJson(List<FileRiskReport> reports, Dictionary<string, double>? baseline = null)
    {
        var exportData = reports.Select(r =>
        {
            double? delta = baseline != null
                ? (baseline.TryGetValue(r.File, out var prev) ? r.StartingPriority - prev : null)
                : null;

            return new
            {
                file = r.File,
                commits = r.Commits,
                weightedChurn = r.WeightedChurn,
                coverageRate = r.CoverageRate,
                cyclomaticComplexity = r.CyclomaticComplexity,
                riskScore = r.RiskScore,
                riskLevel = r.RiskLevel,
                infrastructureCalls = r.InfrastructureCalls,
                directInstantiations = r.DirectInstantiations,
                concreteConstructorParams = r.ConcreteConstructorParams,
                staticCalls = r.StaticCalls,
                asyncSeamCalls = r.AsyncSeamCalls,
                concreteCasts = r.ConcreteCasts,
                isRegistrationFile = r.IsRegistrationFile,
                rawCouplingScore = r.RawCouplingScore,
                couplingNorm = r.CouplingNorm,
                couplingLevel = r.CouplingLevel,
                startingPriority = r.StartingPriority,
                priorityLevel = r.PriorityLevel,
                delta
            };
        });

        return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    internal static string ExportCsv(List<FileRiskReport> reports, Dictionary<string, double>? baseline = null)
    {
        var sb = new StringBuilder();
        var header = "file,commits,weightedChurn,coverageRate,cyclomaticComplexity,riskScore,riskLevel," +
                     "infrastructureCalls,directInstantiations,concreteConstructorParams,staticCalls," +
                     "asyncSeamCalls,concreteCasts,isRegistrationFile," +
                     "rawCouplingScore,couplingNorm,couplingLevel,startingPriority,priorityLevel";
        if (baseline != null)
            header += ",delta";
        sb.AppendLine(header);

        foreach (var r in reports)
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18}",
                EscapeCsvField(r.File),
                r.Commits,
                r.WeightedChurn,
                r.CoverageRate,
                r.CyclomaticComplexity,
                r.RiskScore,
                r.RiskLevel,
                r.InfrastructureCalls,
                r.DirectInstantiations,
                r.ConcreteConstructorParams,
                r.StaticCalls,
                r.AsyncSeamCalls,
                r.ConcreteCasts,
                r.IsRegistrationFile.ToString().ToLowerInvariant(),
                r.RawCouplingScore,
                r.CouplingNorm,
                EscapeCsvField(r.CouplingLevel),
                r.StartingPriority,
                EscapeCsvField(r.PriorityLevel));

            if (baseline != null)
            {
                var delta = baseline.TryGetValue(r.File, out var prev)
                    ? (r.StartingPriority - prev).ToString("F4", CultureInfo.InvariantCulture)
                    : "NEW";
                line += $",{delta}";
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    internal static string ExportHtml(List<FileRiskReport> reports, Dictionary<string, double>? baseline = null)
    {
        var hasBaseline = baseline != null;
        var sb = new StringBuilder();
        sb.AppendLine("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Litmus Report</title>
<style>
  *, *::before, *::after { box-sizing: border-box; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 2rem; background: #f8f9fa; color: #212529; }
  h1 { font-size: 1.5rem; margin-bottom: 0.25rem; }
  .meta { color: #6c757d; margin-bottom: 1.5rem; font-size: 0.875rem; }
  table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
  th { background: #343a40; color: #fff; text-align: left; padding: 0.625rem 0.75rem; font-size: 0.8125rem; cursor: pointer; user-select: none; white-space: nowrap; }
  th:hover { background: #495057; }
  th .arrow { font-size: 0.625rem; margin-left: 0.25rem; }
  td { padding: 0.5rem 0.75rem; border-bottom: 1px solid #e9ecef; font-size: 0.8125rem; }
  tr:hover td { background: #f1f3f5; }
  .num { text-align: right; font-variant-numeric: tabular-nums; }
  .high { color: #dc3545; font-weight: 600; }
  .medium { color: #e67700; font-weight: 600; }
  .low { color: #28a745; }
  .delta-pos { color: #dc3545; }
  .delta-neg { color: #28a745; }
  .delta-new { color: #6c757d; font-style: italic; }
</style>
</head>
<body>
<h1>Litmus Report</h1>
""");
        sb.AppendLine($"<p class=\"meta\">Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC &mdash; {reports.Count} files</p>");
        sb.AppendLine("<table id=\"t\">");
        sb.AppendLine("<thead><tr>");
        var columns = new List<(string header, bool numeric)>
        {
            ("#", true), ("File", false), ("Commits", true), ("Coverage", true),
            ("Complexity", true), ("Coupling", false), ("Risk", false), ("Priority", false)
        };
        if (hasBaseline) columns.Add(("Delta", true));

        foreach (var (header, _) in columns)
            sb.AppendLine($"  <th onclick=\"sortTable(this)\">{HtmlEncode(header)}<span class=\"arrow\"></span></th>");
        sb.AppendLine("</tr></thead>");
        sb.AppendLine("<tbody>");

        for (var i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            var priorityCss = r.PriorityLevel.ToLowerInvariant();
            var riskCss = r.RiskLevel.ToLowerInvariant();
            var depCss = r.CouplingLevel switch
            {
                "Very High" => "high",
                "High" => "medium",
                _ => ""
            };

            sb.AppendLine("<tr>");
            sb.AppendLine($"  <td class=\"num\">{i + 1}</td>");
            sb.AppendLine($"  <td>{HtmlEncode(r.File)}</td>");
            sb.AppendLine($"  <td class=\"num\">{r.Commits}</td>");
            sb.AppendLine($"  <td class=\"num\">{r.CoverageRate * 100:F0}%</td>");
            sb.AppendLine($"  <td class=\"num\">{r.CyclomaticComplexity}</td>");
            sb.AppendLine($"  <td class=\"{depCss}\">{HtmlEncode(r.CouplingLevel)}</td>");
            sb.AppendLine($"  <td class=\"{riskCss}\">{HtmlEncode(r.RiskLevel)}</td>");
            sb.AppendLine($"  <td class=\"{priorityCss}\">{HtmlEncode(r.PriorityLevel)}</td>");

            if (hasBaseline)
            {
                if (!baseline!.TryGetValue(r.File, out var prev))
                {
                    sb.AppendLine("  <td class=\"num delta-new\">NEW</td>");
                }
                else
                {
                    var delta = r.StartingPriority - prev;
                    if (Math.Abs(delta) < 0.005)
                        sb.AppendLine("  <td class=\"num\">&mdash;</td>");
                    else
                    {
                        var sign = delta > 0 ? "+" : "";
                        var css = delta > 0 ? "delta-pos" : "delta-neg";
                        sb.AppendLine($"  <td class=\"num {css}\">{sign}{delta:F2}</td>");
                    }
                }
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");

        // Client-side sorting script
        sb.AppendLine("""
<script>
function sortTable(th) {
  const table = document.getElementById('t');
  const tbody = table.tBodies[0];
  const idx = Array.from(th.parentNode.children).indexOf(th);
  const rows = Array.from(tbody.rows);
  const cur = th.dataset.dir || '';
  // Reset all arrows
  th.parentNode.querySelectorAll('.arrow').forEach(a => a.textContent = '');
  const dir = cur === 'asc' ? 'desc' : 'asc';
  th.dataset.dir = dir;
  th.querySelector('.arrow').textContent = dir === 'asc' ? ' \u25B2' : ' \u25BC';
  rows.sort((a, b) => {
    let av = a.cells[idx].textContent.trim();
    let bv = b.cells[idx].textContent.trim();
    // Try numeric comparison
    const an = parseFloat(av.replace('%',''));
    const bn = parseFloat(bv.replace('%',''));
    if (!isNaN(an) && !isNaN(bn)) return dir === 'asc' ? an - bn : bn - an;
    return dir === 'asc' ? av.localeCompare(bv) : bv.localeCompare(av);
  });
  rows.forEach(r => tbody.appendChild(r));
}
</script>
</body>
</html>
""");

        return sb.ToString();
    }

    private static string HtmlEncode(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }
}
