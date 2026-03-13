using Litmus.Services;

namespace Litmus.Models;

public class FileRiskReport
{
    // Phase 1 — Risk signals
    public required string File { get; set; }
    public int Commits { get; set; }
    public int WeightedChurn { get; set; }
    public double ChurnNorm { get; set; }
    public double CoverageRate { get; set; }
    public int CyclomaticComplexity { get; set; }
    public double ComplexityNorm { get; set; }
    public double RiskScore { get; set; }
    public string RiskLevel { get; set; } = "Low";

    // Phase 2 — Dependency & starting priority
    public int InfrastructureCalls { get; set; }
    public int DirectInstantiations { get; set; }
    public int ConcreteConstructorParams { get; set; }
    public int StaticCalls { get; set; }
    public int AsyncSeamCalls { get; set; }
    public int ConcreteCasts { get; set; }
    public bool IsRegistrationFile { get; set; }
    public double RawCouplingScore { get; set; }
    public double CouplingNorm { get; set; }
    public string CouplingLevel { get; set; } = "Low";  // Low | Medium | High | Very High
    public double StartingPriority { get; set; }
    public string PriorityLevel { get; set; } = "Low";    // Low | Medium | High
    public ComplexityBreakdown? ComplexityBreakdown { get; set; }
}
