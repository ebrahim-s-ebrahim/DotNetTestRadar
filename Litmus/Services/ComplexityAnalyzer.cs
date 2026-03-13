using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Litmus.Abstractions;

namespace Litmus.Services;

public record ComplexityBreakdown
{
    public int Conditionals { get; init; }  // if, ternary (?:)
    public int Loops { get; init; }         // for, foreach, while, do
    public int Catches { get; init; }       // catch clauses
    public int Switches { get; init; }      // case labels, when clauses
    public int LogicalOps { get; init; }    // &&, ||, ??
}

public class ComplexityResult
{
    public Dictionary<string, int> FileComplexity { get; set; } = new();
    public Dictionary<string, double> FileComplexityNorm { get; set; } = new();
    public Dictionary<string, List<(string Name, int Complexity)>> MethodComplexity { get; set; } = new();
    public Dictionary<string, ComplexityBreakdown> FileComplexityBreakdown { get; set; } = new();
    public int SkippedFiles { get; set; }
}

public class ComplexityAnalyzer
{
    private readonly IFileSystem _fileSystem;

    public ComplexityAnalyzer(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public ComplexityResult Analyze(string gitRoot, List<string> projectDirectories, List<string> excludePatterns,
        Action? onFileProcessed = null)
    {
        var result = new ComplexityResult();

        foreach (var projectDir in projectDirectories)
        {
            var fullDir = Path.Combine(gitRoot, projectDir);
            if (!_fileSystem.DirectoryExists(fullDir))
                continue;

            var files = _fileSystem.GetFiles(fullDir, "*.cs", recursive: true);
            foreach (var file in files)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(gitRoot, file).Replace('\\', '/');
                    if (FileFilterHelper.MatchesAnyPattern(relativePath, excludePatterns))
                    {
                        onFileProcessed?.Invoke();
                        continue;
                    }

                    var content = _fileSystem.ReadAllText(file);
                    var complexity = CalculateFileComplexity(content);
                    result.FileComplexity[relativePath] = complexity;

                    var breakdown = CalculateFileBreakdown(content);
                    result.FileComplexityBreakdown[relativePath] = breakdown;

                    var methodDetails = CalculateMethodComplexities(content);
                    if (methodDetails.Count > 0)
                        result.MethodComplexity[relativePath] = methodDetails;
                }
                catch
                {
                    result.SkippedFiles++;
                }

                onFileProcessed?.Invoke();
            }
        }

        // Normalize
        var maxComplexity = result.FileComplexity.Values.DefaultIfEmpty(0).Max();
        foreach (var (file, complexity) in result.FileComplexity)
        {
            result.FileComplexityNorm[file] = maxComplexity > 0
                ? (double)complexity / maxComplexity
                : 0.0;
        }

        return result;
    }

    public static List<(string Name, int Complexity)> CalculateMethodComplexities(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();
        var methods = new List<(string Name, int Complexity)>();

        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            var name = method switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                ConstructorDeclarationSyntax c => c.Identifier.Text,
                DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
                OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
                ConversionOperatorDeclarationSyntax co => "operator " + co.Type,
                _ => null
            };

            if (name == null) continue;

            var complexity = CalculateMethodComplexity(method);
            methods.Add((name, complexity));
        }

        return methods;
    }

    public static int CalculateFileComplexity(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();
        var totalComplexity = 0;

        var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
        foreach (var method in methods)
        {
            totalComplexity += CalculateMethodComplexity(method);
        }

        // Also count local functions and property accessors
        var accessors = root.DescendantNodes().OfType<AccessorDeclarationSyntax>()
            .Where(a => a.Body != null || a.ExpressionBody != null);
        foreach (var accessor in accessors)
        {
            totalComplexity += CalculateNodeComplexity(accessor);
        }

        return totalComplexity;
    }

    private static int CalculateMethodComplexity(BaseMethodDeclarationSyntax method)
    {
        var complexity = 1; // Base complexity
        complexity += CalculateNodeComplexity(method);
        return complexity;
    }

    private static int CalculateNodeComplexity(SyntaxNode node)
    {
        var complexity = 0;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant)
            {
                case IfStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case CasePatternSwitchLabelSyntax:
                case CaseSwitchLabelSyntax:
                case WhenClauseSyntax:
                case ConditionalExpressionSyntax:
                    complexity++;
                    break;
            }

            if (descendant is BinaryExpressionSyntax binary)
            {
                if (binary.OperatorToken.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
                    binary.OperatorToken.IsKind(SyntaxKind.BarBarToken) ||
                    binary.OperatorToken.IsKind(SyntaxKind.QuestionQuestionToken))
                {
                    complexity++;
                }
            }
        }

        return complexity;
    }

    public static ComplexityBreakdown CalculateFileBreakdown(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();
        return CalculateNodeBreakdown(root);
    }

    private static ComplexityBreakdown CalculateNodeBreakdown(SyntaxNode node)
    {
        int conditionals = 0, loops = 0, catches = 0, switches = 0, logicalOps = 0;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant)
            {
                case IfStatementSyntax:
                case ConditionalExpressionSyntax:
                    conditionals++;
                    break;
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                    loops++;
                    break;
                case CatchClauseSyntax:
                    catches++;
                    break;
                case CasePatternSwitchLabelSyntax:
                case CaseSwitchLabelSyntax:
                case WhenClauseSyntax:
                    switches++;
                    break;
            }

            if (descendant is BinaryExpressionSyntax binary)
            {
                if (binary.OperatorToken.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
                    binary.OperatorToken.IsKind(SyntaxKind.BarBarToken) ||
                    binary.OperatorToken.IsKind(SyntaxKind.QuestionQuestionToken))
                {
                    logicalOps++;
                }
            }
        }

        return new ComplexityBreakdown
        {
            Conditionals = conditionals,
            Loops = loops,
            Catches = catches,
            Switches = switches,
            LogicalOps = logicalOps
        };
    }
}
