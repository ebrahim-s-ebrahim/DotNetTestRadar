using System.Globalization;
using System.Xml.Linq;
using Litmus.Abstractions;

namespace Litmus.Services;

public class CoverageResult
{
    public Dictionary<string, double> FileCoverage { get; set; } = new();
    public Dictionary<string, List<(string Name, double CoverageRate)>> MethodCoverage { get; set; } = new();
}

public class CoverageParser
{
    private readonly IFileSystem _fileSystem;

    public CoverageParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public CoverageResult Parse(string coveragePath)
    {
        var content = _fileSystem.ReadAllText(coveragePath);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"The coverage file contains invalid XML: {ex.Message}", ex);
        }

        var result = new CoverageResult();

        // Cobertura format: <coverage> -> <packages> -> <package> -> <classes> -> <class filename="...">
        // Coverlet generates multiple <class> entries per file (main class + compiler-generated
        // types like <>c, <>d__N). We group by filename and aggregate <line> elements to compute
        // an accurate per-file coverage rate.
        var classes = doc.Descendants("class");

        var fileGroups = new Dictionary<string, List<XElement>>();
        foreach (var cls in classes)
        {
            var filename = cls.Attribute("filename")?.Value;
            if (filename == null)
                continue;

            var normalizedPath = filename.Replace('\\', '/');
            if (!fileGroups.TryGetValue(normalizedPath, out var list))
            {
                list = new List<XElement>();
                fileGroups[normalizedPath] = list;
            }
            list.Add(cls);
        }

        foreach (var (filePath, classList) in fileGroups)
        {
            // Collect all <line> elements across every class in this file,
            // deduplicating by line number (take max hits per line).
            var lineHits = new Dictionary<int, int>();
            foreach (var cls in classList)
            {
                foreach (var line in cls.Descendants("line"))
                {
                    var numStr = line.Attribute("number")?.Value;
                    var hitsStr = line.Attribute("hits")?.Value;
                    if (numStr == null || hitsStr == null)
                        continue;
                    if (!int.TryParse(numStr, out var lineNum) || !int.TryParse(hitsStr, out var hits))
                        continue;

                    if (!lineHits.TryGetValue(lineNum, out var existing) || hits > existing)
                        lineHits[lineNum] = hits;
                }
            }

            double lineRate;
            if (lineHits.Count > 0)
            {
                lineRate = (double)lineHits.Values.Count(h => h > 0) / lineHits.Count;
            }
            else
            {
                // No <line> elements — fall back to line-rate attribute from last class
                var lastLineRateStr = classList[^1].Attribute("line-rate")?.Value;
                lineRate = lastLineRateStr != null &&
                           double.TryParse(lastLineRateStr, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0.0;
            }

            result.FileCoverage[filePath] = lineRate;
        }

        // Method-level coverage: parse <method> elements inside each <class>
        foreach (var (filePath, classList) in fileGroups)
        {
            var methodCoverages = new List<(string Name, double CoverageRate)>();

            foreach (var cls in classList)
            {
                var methodElements = cls.Elements("methods")?.Elements("method");
                if (methodElements == null) continue;

                foreach (var method in methodElements)
                {
                    var methodName = method.Attribute("name")?.Value;
                    if (methodName == null) continue;

                    // Skip compiler-generated methods
                    if (methodName.StartsWith("<") || methodName.StartsWith("."))
                        continue;

                    var methodLineHits = new Dictionary<int, int>();
                    foreach (var line in method.Descendants("line"))
                    {
                        var numStr = line.Attribute("number")?.Value;
                        var hitsStr = line.Attribute("hits")?.Value;
                        if (numStr == null || hitsStr == null) continue;
                        if (!int.TryParse(numStr, out var lineNum) || !int.TryParse(hitsStr, out var hits))
                            continue;

                        if (!methodLineHits.TryGetValue(lineNum, out var existing) || hits > existing)
                            methodLineHits[lineNum] = hits;
                    }

                    if (methodLineHits.Count > 0)
                    {
                        var rate = (double)methodLineHits.Values.Count(h => h > 0) / methodLineHits.Count;
                        methodCoverages.Add((methodName, rate));
                    }
                }
            }

            if (methodCoverages.Count > 0)
                result.MethodCoverage[filePath] = methodCoverages;
        }

        return result;
    }

    /// <summary>
    /// Merges multiple coverage results into one by taking the highest coverage rate
    /// seen for each file across all results. Used when multiple test projects each
    /// produce their own coverage.cobertura.xml.
    /// </summary>
    public static CoverageResult Merge(IEnumerable<CoverageResult> results)
    {
        var merged = new CoverageResult();
        foreach (var result in results)
        {
            foreach (var (file, rate) in result.FileCoverage)
            {
                if (!merged.FileCoverage.TryGetValue(file, out var existing) || rate > existing)
                    merged.FileCoverage[file] = rate;
            }

            foreach (var (file, methods) in result.MethodCoverage)
            {
                if (!merged.MethodCoverage.TryGetValue(file, out var existing) || methods.Count > existing.Count)
                    merged.MethodCoverage[file] = methods;
            }
        }
        return merged;
    }

    public static List<(string Name, double CoverageRate)> GetMethodCoverageForFile(
        CoverageResult coverage, string fileRelativePath)
    {
        var normalizedTarget = fileRelativePath.Replace('\\', '/');

        if (coverage.MethodCoverage.TryGetValue(normalizedTarget, out var methods))
            return methods;

        foreach (var (path, methodList) in coverage.MethodCoverage)
        {
            if (path.EndsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedTarget.EndsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                return methodList;
            }
        }

        return [];
    }

    public static double GetCoverageForFile(CoverageResult coverage, string fileRelativePath)
    {
        var normalizedTarget = fileRelativePath.Replace('\\', '/');

        // Try exact match first
        if (coverage.FileCoverage.TryGetValue(normalizedTarget, out var rate))
            return rate;

        // Try suffix matching (coverage paths may have different prefixes)
        foreach (var (path, coverageRate) in coverage.FileCoverage)
        {
            if (path.EndsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                normalizedTarget.EndsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                return coverageRate;
            }
        }

        return 0.0;
    }
}
