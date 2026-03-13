using Litmus.Services;
using FluentAssertions;

namespace Litmus.Tests.Services;

public class RiskScorerTests
{
    private readonly RiskScorer _sut = new();

    private static ChurnResult MakeChurn(string file, int weightedChurn, double churnNorm, int commits = 1)
    {
        return new ChurnResult
        {
            Files = new Dictionary<string, FileChurnData>
            {
                [file] = new FileChurnData
                {
                    WeightedChurn = weightedChurn,
                    ChurnNorm = churnNorm,
                    Commits = commits
                }
            }
        };
    }

    private static CoverageResult MakeCoverage(string file, double rate)
    {
        return new CoverageResult
        {
            FileCoverage = new Dictionary<string, double>
            {
                [file] = rate
            }
        };
    }

    private static ComplexityResult MakeComplexity(string gitRootRelFile, int complexity, double norm)
    {
        return new ComplexityResult
        {
            FileComplexity = new Dictionary<string, int> { [gitRootRelFile] = complexity },
            FileComplexityNorm = new Dictionary<string, double> { [gitRootRelFile] = norm }
        };
    }

    private static DependencyResult MakeDependency(string gitRootRelFile, double rawScore, double norm)
    {
        return new DependencyResult
        {
            Files = new Dictionary<string, FileDependencyData>
            {
                [gitRootRelFile] = new FileDependencyData
                {
                    RawCouplingScore = rawScore,
                    CouplingNorm = norm
                }
            }
        };
    }

    private static DependencyResult EmptyDependency() => new();

    // -------------------------------------------------------------------------
    // Phase 1 — Risk score (unchanged behaviour)
    // -------------------------------------------------------------------------

    [Fact]
    public void Score_ZeroChurn_RiskScoreIsZero()
    {
        var churn = new ChurnResult();
        var coverage = MakeCoverage("MyApp/File.cs", 0.0);
        var complexity = MakeComplexity("MyApp/File.cs", 10, 1.0);

        var reports = _sut.Score(churn, coverage, complexity, EmptyDependency(), "/repo", "/repo");

        reports.Should().AllSatisfy(r => r.RiskScore.Should().Be(0));
    }

    [Fact]
    public void Score_FullCoverage_RiskScoreIsZero()
    {
        var churn = MakeChurn("MyApp/File.cs", 100, 1.0);
        var coverage = MakeCoverage("MyApp/File.cs", 1.0);
        var complexity = MakeComplexity("MyApp/File.cs", 50, 1.0);

        var reports = _sut.Score(churn, coverage, complexity, EmptyDependency(), "/repo", "/repo");

        var file = reports.First(r => r.File == "MyApp/File.cs");
        file.RiskScore.Should().Be(0);
    }

    [Fact]
    public void Score_HighChurnLowCoverageHighComplexity_ScoresHigherThanWithZeroComplexity()
    {
        var churn1 = MakeChurn("MyApp/FileA.cs", 100, 1.0);
        var coverage1 = MakeCoverage("MyApp/FileA.cs", 0.1);
        var complexity1 = MakeComplexity("MyApp/FileA.cs", 50, 1.0);
        var reports1 = _sut.Score(churn1, coverage1, complexity1, EmptyDependency(), "/repo", "/repo");
        var scoreWithComplexity = reports1.First(r => r.File == "MyApp/FileA.cs").RiskScore;

        var churn2 = MakeChurn("MyApp/FileB.cs", 100, 1.0);
        var coverage2 = MakeCoverage("MyApp/FileB.cs", 0.1);
        var complexity2 = MakeComplexity("MyApp/FileB.cs", 0, 0.0);
        var reports2 = _sut.Score(churn2, coverage2, complexity2, EmptyDependency(), "/repo", "/repo");
        var scoreWithoutComplexity = reports2.First(r => r.File == "MyApp/FileB.cs").RiskScore;

        scoreWithComplexity.Should().BeGreaterThan(scoreWithoutComplexity);
    }

    [Theory]
    [InlineData(1.0, 0.0, 1.0, "High")]   // RiskScore = 1.0 * 1.0 * 2.0 = 2.0
    [InlineData(0.6, 0.0, 0.0, "High")]   // RiskScore = 0.6 * 1.0 * 1.0 = 0.6
    [InlineData(0.3, 0.0, 0.0, "Medium")] // RiskScore = 0.3 * 1.0 * 1.0 = 0.3
    [InlineData(0.2, 0.0, 0.0, "Medium")] // RiskScore = 0.2 * 1.0 * 1.0 = 0.2
    [InlineData(0.1, 0.0, 0.0, "Low")]    // RiskScore = 0.1 * 1.0 * 1.0 = 0.1
    public void Score_RiskLevelClassifiesCorrectlyAtBoundaries(
        double churnNorm, double coverageRate, double complexityNorm, string expectedLevel)
    {
        var churn = MakeChurn("File.cs", 100, churnNorm);
        var coverage = MakeCoverage("File.cs", coverageRate);
        var complexity = MakeComplexity("File.cs", 10, complexityNorm);

        var reports = _sut.Score(churn, coverage, complexity, EmptyDependency(), "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.RiskLevel.Should().Be(expectedLevel);
    }

    [Fact]
    public void Score_NeverExceedsTwo()
    {
        var churn = MakeChurn("File.cs", 100, 1.0);
        var coverage = MakeCoverage("File.cs", 0.0);
        var complexity = MakeComplexity("File.cs", 50, 1.0);

        var reports = _sut.Score(churn, coverage, complexity, EmptyDependency(), "/repo", "/repo");

        reports.Should().AllSatisfy(r => r.RiskScore.Should().BeLessThanOrEqualTo(2.0));
    }

    // -------------------------------------------------------------------------
    // Phase 2 — Starting priority
    // -------------------------------------------------------------------------

    [Fact]
    public void Score_NoDependencies_StartingPriorityEqualsRiskScore()
    {
        var churn = MakeChurn("File.cs", 100, 1.0);
        var coverage = MakeCoverage("File.cs", 0.0);
        var complexity = MakeComplexity("File.cs", 50, 1.0);
        var dependency = MakeDependency("File.cs", 0, 0.0);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.StartingPriority.Should().Be(file.RiskScore,
            "when CouplingNorm = 0, StartingPriority = RiskScore × (1 - 0) = RiskScore");
    }

    [Fact]
    public void Score_MaxDependencies_StartingPriorityIsZero()
    {
        var churn = MakeChurn("File.cs", 100, 1.0);
        var coverage = MakeCoverage("File.cs", 0.0);
        var complexity = MakeComplexity("File.cs", 50, 1.0);
        var dependency = MakeDependency("File.cs", 99, 1.0);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.StartingPriority.Should().Be(0,
            "when CouplingNorm = 1, StartingPriority = RiskScore × (1 - 1) = 0");
    }

    [Fact]
    public void Score_PartialDependency_StartingPriorityDiscountedCorrectly()
    {
        // RiskScore = 1.0 × 1.0 × 1.0 = 1.0, CouplingNorm = 0.6 → SP = 0.4
        var churn = MakeChurn("File.cs", 100, 1.0);
        var coverage = MakeCoverage("File.cs", 0.0);
        var complexity = MakeComplexity("File.cs", 10, 0.0);
        var dependency = MakeDependency("File.cs", 10, 0.6);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.StartingPriority.Should().BeApproximately(0.4, 0.0001);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0, 0.0, "High")]    // SP = 1.0 × (1-0) = 1.0 ≥ 0.6
    [InlineData(0.6, 0.0, 0.0, 0.0, "High")]    // SP = 0.6 ≥ 0.6
    [InlineData(0.5, 0.0, 0.0, 0.0, "Medium")]  // SP = 0.5 ≥ 0.2
    [InlineData(0.1, 0.0, 0.0, 0.0, "Low")]     // SP = 0.1 < 0.2
    [InlineData(1.0, 0.0, 0.0, 0.8, "Medium")]   // SP = 1.0 × (1-0.8) = 0.2 → >= 0.2 is Medium boundary
    public void Score_PriorityLevelClassifiesCorrectlyAtBoundaries(
        double churnNorm, double coverageRate, double complexityNorm, double depNorm, string expectedPriorityLevel)
    {
        var churn = MakeChurn("File.cs", 100, churnNorm);
        var coverage = MakeCoverage("File.cs", coverageRate);
        var complexity = MakeComplexity("File.cs", 10, complexityNorm);
        var dependency = MakeDependency("File.cs", 10, depNorm);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.PriorityLevel.Should().Be(expectedPriorityLevel);
    }

    [Theory]
    [InlineData(0.0, "Low")]
    [InlineData(0.24, "Low")]
    [InlineData(0.25, "Medium")]
    [InlineData(0.74, "High")]
    [InlineData(0.75, "Very High")]
    [InlineData(1.0, "Very High")]
    public void Score_CouplingLevelClassifiesCorrectly(double depNorm, string expectedDepLevel)
    {
        var churn = MakeChurn("File.cs", 100, 1.0);
        var coverage = MakeCoverage("File.cs", 0.0);
        var complexity = MakeComplexity("File.cs", 0, 0.0);
        var dependency = MakeDependency("File.cs", 10, depNorm);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "File.cs");
        file.CouplingLevel.Should().Be(expectedDepLevel);
    }

    [Fact]
    public void Score_ReportsSortedByStartingPriorityDescending()
    {
        // File A: high risk, no dependency → SP stays high
        // File B: high risk, max dependency → SP = 0
        var churn = new ChurnResult
        {
            Files = new Dictionary<string, FileChurnData>
            {
                ["FileA.cs"] = new FileChurnData { ChurnNorm = 1.0, WeightedChurn = 100, Commits = 10 },
                ["FileB.cs"] = new FileChurnData { ChurnNorm = 1.0, WeightedChurn = 100, Commits = 10 }
            }
        };
        var coverage = new CoverageResult
        {
            FileCoverage = new Dictionary<string, double>
            {
                ["FileA.cs"] = 0.0,
                ["FileB.cs"] = 0.0
            }
        };
        var complexity = new ComplexityResult
        {
            FileComplexity = new Dictionary<string, int> { ["FileA.cs"] = 0, ["FileB.cs"] = 0 },
            FileComplexityNorm = new Dictionary<string, double> { ["FileA.cs"] = 0.0, ["FileB.cs"] = 0.0 }
        };
        var dependency = new DependencyResult
        {
            Files = new Dictionary<string, FileDependencyData>
            {
                ["FileA.cs"] = new FileDependencyData { CouplingNorm = 0.0 },   // fully seamed
                ["FileB.cs"] = new FileDependencyData { CouplingNorm = 1.0 }    // maximally entangled
            }
        };

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        reports[0].File.Should().Be("FileA.cs", "FileA has higher StartingPriority");
        reports[1].File.Should().Be("FileB.cs", "FileB is maximally entangled → SP = 0");
    }

    [Fact]
    public void Score_HighRiskLowPriority_IdentifiesSeamIntroductionCandidate()
    {
        // This is the key Phase 2 signal: dangerous but entangled
        var churn = MakeChurn("Legacy.cs", 100, 1.0);
        var coverage = MakeCoverage("Legacy.cs", 0.0);
        var complexity = MakeComplexity("Legacy.cs", 50, 1.0);
        var dependency = MakeDependency("Legacy.cs", 99, 1.0);

        var reports = _sut.Score(churn, coverage, complexity, dependency, "/repo", "/repo");

        var file = reports.First(r => r.File == "Legacy.cs");
        file.RiskLevel.Should().Be("High", "this file is genuinely dangerous");
        file.PriorityLevel.Should().Be("Low", "but impossible to test today due to entanglement");
    }
}
