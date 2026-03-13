using Litmus.Models;

namespace Litmus.Services;

public class RiskScorer
{
    public List<FileRiskReport> Score(
        ChurnResult churn,
        CoverageResult coverage,
        ComplexityResult complexity,
        DependencyResult dependency,
        string solutionDirectory,
        string gitRoot)
    {
        var allFiles = new HashSet<string>();
        foreach (var f in churn.Files.Keys) allFiles.Add(f);

        var solutionRelPath = Path.GetRelativePath(gitRoot, solutionDirectory).Replace('\\', '/');

        foreach (var f in complexity.FileComplexity.Keys)
        {
            // Convert from git-root-relative to solution-relative
            var rel = f;
            if (solutionRelPath != "." && rel.StartsWith(solutionRelPath + "/", StringComparison.OrdinalIgnoreCase))
                rel = rel[(solutionRelPath.Length + 1)..];
            allFiles.Add(rel);
        }

        var reports = new List<FileRiskReport>();

        foreach (var file in allFiles)
        {
            var churnData = churn.Files.GetValueOrDefault(file);
            var churnNorm = churnData?.ChurnNorm ?? 0.0;
            var commits = churnData?.Commits ?? 0;
            var weightedChurn = churnData?.WeightedChurn ?? 0;

            var coverageRate = CoverageParser.GetCoverageForFile(coverage, file);

            // Build git-root-relative path for complexity and dependency lookups
            var gitRootRelPath = file;
            if (solutionRelPath != ".")
                gitRootRelPath = solutionRelPath + "/" + file;

            var complexityNorm = complexity.FileComplexityNorm.GetValueOrDefault(gitRootRelPath, 0.0);
            var rawComplexity = complexity.FileComplexity.GetValueOrDefault(gitRootRelPath, 0);

            // Phase 1: Risk score (round to avoid floating-point boundary misclassification)
            var riskScore = Math.Round(churnNorm * (1 - coverageRate) * (1 + complexityNorm), 4);
            var riskLevel = riskScore switch
            {
                >= 0.6 => "High",
                >= 0.2 => "Medium",
                _ => "Low"
            };

            // Phase 2: Coupling score and starting priority
            var depData = dependency.Files.GetValueOrDefault(gitRootRelPath);
            var couplingNorm = depData?.CouplingNorm ?? 0.0;

            var startingPriority = Math.Round(riskScore * (1 - couplingNorm), 4);
            var couplingLevel = couplingNorm switch
            {
                >= 0.75 => "Very High",
                >= 0.50 => "High",
                >= 0.25 => "Medium",
                _ => "Low"
            };
            var priorityLevel = startingPriority switch
            {
                >= 0.6 => "High",
                >= 0.2 => "Medium",
                _ => "Low"
            };

            reports.Add(new FileRiskReport
            {
                File = file,
                Commits = commits,
                WeightedChurn = weightedChurn,
                ChurnNorm = churnNorm,
                CoverageRate = coverageRate,
                CyclomaticComplexity = rawComplexity,
                ComplexityNorm = complexityNorm,
                RiskScore = riskScore,
                RiskLevel = riskLevel,
                InfrastructureCalls = depData?.InfrastructureCalls ?? 0,
                DirectInstantiations = depData?.DirectInstantiations ?? 0,
                ConcreteConstructorParams = depData?.ConcreteConstructorParams ?? 0,
                StaticCalls = depData?.StaticCalls ?? 0,
                AsyncSeamCalls = depData?.AsyncSeamCalls ?? 0,
                ConcreteCasts = depData?.ConcreteCasts ?? 0,
                IsRegistrationFile = depData?.IsRegistrationFile ?? false,
                RawCouplingScore = depData?.RawCouplingScore ?? 0.0,
                CouplingNorm = couplingNorm,
                CouplingLevel = couplingLevel,
                StartingPriority = startingPriority,
                PriorityLevel = priorityLevel,
                ComplexityBreakdown = complexity.FileComplexityBreakdown.GetValueOrDefault(gitRootRelPath)
            });
        }

        // Sort by StartingPriority — the primary actionable output of the tool
        return reports.OrderByDescending(r => r.StartingPriority).ToList();
    }
}
