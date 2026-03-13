using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Litmus.Abstractions;

namespace Litmus.Services;

public class FileDependencyData
{
    public int InfrastructureCalls { get; set; }
    public int DirectInstantiations { get; set; }
    public int ConcreteConstructorParams { get; set; }
    public int StaticCalls { get; set; }
    public int AsyncSeamCalls { get; set; }
    public int ConcreteCasts { get; set; }
    public bool IsRegistrationFile { get; set; }
    public double RawCouplingScore { get; set; }
    public double CouplingNorm { get; set; }
}

public class DependencyResult
{
    public Dictionary<string, FileDependencyData> Files { get; set; } = new();
    public int SkippedFiles { get; set; }
}

public class DependencyAnalyzer
{
    private readonly IFileSystem _fileSystem;

    // Signal 1: Infrastructure static type names (member access patterns like DateTime.Now, File.ReadAllText)
    private static readonly HashSet<string> InfrastructureStaticTypes = new(StringComparer.Ordinal)
    {
        "DateTime", "DateTimeOffset", "File", "Directory", "Environment", "Guid", "Console"
    };

    // Signal 1: Infrastructure types created via new (HttpClient, SqlConnection, *DbContext)
    private static readonly HashSet<string> InfrastructureNewTypes = new(StringComparer.Ordinal)
    {
        "HttpClient", "SqlConnection", "SqlCommand", "SqlDataAdapter",
        "TcpClient", "UdpClient", "TcpListener"
    };

    // Signal 2: Type name suffixes that are safe to instantiate — DTOs, value objects, exceptions
    private static readonly string[] SafeNewSuffixes =
    [
        "Exception", "EventArgs", "Attribute", "Options", "Settings",
        "Dto", "ViewModel", "Request", "Response", "Command", "Query",
        "Builder", "Model", "Entity", "Result", "Event", "Message"
    ];

    // Signal 2: Exact type names that are safe to instantiate — collections, string helpers, primitives
    private static readonly HashSet<string> SafeNewExact = new(StringComparer.Ordinal)
    {
        "List", "Dictionary", "HashSet", "Queue", "Stack", "LinkedList",
        "SortedList", "SortedSet", "ConcurrentDictionary", "ConcurrentQueue",
        "ConcurrentBag", "ConcurrentStack", "Collection", "ObservableCollection",
        "ReadOnlyCollection", "ReadOnlyDictionary",
        "StringBuilder", "MemoryStream", "StringWriter", "StringReader",
        "CancellationTokenSource", "TaskCompletionSource",
        "Stopwatch", "Random", "Timer"
    };

    // Signal 4: Known safe static utility types — excluded from static call counting
    private static readonly HashSet<string> SafeStaticTypes = new(StringComparer.Ordinal)
    {
        "Math", "Convert", "Encoding", "Enumerable", "Queryable",
        "Task", "ValueTask", "Activator", "BitConverter", "Buffer",
        "GC", "Monitor", "Interlocked", "Volatile",
        "String", "Char", "Int32", "Int64", "Double", "Decimal", "Boolean",
        "Byte", "Enum", "Array", "Tuple", "KeyValuePair",
        "EqualityComparer", "Comparer", "StringComparer",
        "Regex", "Path",
        // Also include Signal 1 types to avoid double counting
        "DateTime", "DateTimeOffset", "File", "Directory", "Environment", "Guid", "Console",
        // Collections as static factory (e.g. List.Empty, Array.Empty)
        "List", "Dictionary", "HashSet", "ImmutableArray", "ImmutableList", "ImmutableDictionary",
        // Diagnostics
        "Debug", "Trace",
        // Serialization
        "JsonSerializer",
        // LINQ / expressions
        "Expression"
    };

    // Signal 3: Primitive C# type aliases
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "double", "float", "decimal", "long", "short",
        "byte", "char", "uint", "ulong", "ushort", "sbyte", "object", "void",
        "dynamic", "nint", "nuint"
    };

    // Signal 3: Known safe framework types that are not DI dependencies
    private static readonly HashSet<string> SafeConstructorParamTypes = new(StringComparer.Ordinal)
    {
        "DateTime", "DateTimeOffset", "TimeSpan", "TimeOnly", "DateOnly",
        "Guid", "Uri", "Version", "CancellationToken",
        "ILogger", "IConfiguration", "IOptions",
        "String", "Boolean", "Int32", "Int64", "Double", "Decimal"
    };

    // Signal 5: Known async I/O method names (await receiver.MethodAsync())
    private static readonly HashSet<string> AsyncIoMethodNames = new(StringComparer.Ordinal)
    {
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "SendAsync", "PatchAsync",
        "ExecuteAsync", "SaveChangesAsync",
        "ReadAsync", "WriteAsync", "FlushAsync", "CopyToAsync",
        "ReadLineAsync", "WriteLineAsync",
        "ExecuteNonQueryAsync", "ExecuteReaderAsync", "ExecuteScalarAsync",
        "OpenAsync", "CloseAsync"
    };

    // DI registration method names
    private static readonly HashSet<string> DiRegistrationMethods = new(StringComparer.Ordinal)
    {
        "AddScoped", "AddSingleton", "AddTransient",
        "AddHostedService", "AddDbContext", "AddHttpClient"
    };

    public DependencyAnalyzer(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public DependencyResult Analyze(string gitRoot, List<string> projectDirectories, List<string> excludePatterns,
        Action? onFileProcessed = null)
    {
        var result = new DependencyResult();

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
                    var data = AnalyzeFile(content);

                    // DI registration file heuristic: files that configure DI containers
                    // have expected high coupling — zero their score
                    var fileName = Path.GetFileName(file);
                    if (fileName is "Program.cs" or "Startup.cs" || ContainsDiRegistrations(content))
                    {
                        data.IsRegistrationFile = true;
                        data.RawCouplingScore = 0;
                    }

                    result.Files[relativePath] = data;
                }
                catch
                {
                    result.SkippedFiles++;
                }

                onFileProcessed?.Invoke();
            }
        }

        // Normalize: divide each raw score by the maximum across non-registration files
        var maxScore = result.Files.Values
            .Where(f => !f.IsRegistrationFile)
            .Select(f => f.RawCouplingScore)
            .DefaultIfEmpty(0).Max();
        foreach (var data in result.Files.Values)
        {
            data.CouplingNorm = data.IsRegistrationFile ? 0.0
                : maxScore > 0 ? data.RawCouplingScore / maxScore : 0.0;
        }

        return result;
    }

    public static FileDependencyData AnalyzeFile(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        var infraCalls = 0;
        var directInstantiations = 0;
        var staticCalls = 0;
        var asyncSeamCalls = 0;
        var concreteCasts = 0;

        foreach (var node in root.DescendantNodes())
        {
            if (!IsInsideExecutableCode(node))
                continue;

            switch (node)
            {
                // Signal 1: Member access on infrastructure static types
                // Catches: DateTime.Now, File.ReadAllText, Environment.GetEnvironmentVariable, Guid.NewGuid, etc.
                case MemberAccessExpressionSyntax memberAccess
                    when memberAccess.Expression is IdentifierNameSyntax id
                    && InfrastructureStaticTypes.Contains(id.Identifier.Text):
                    infraCalls++;
                    break;

                // Signal 1 & 2: Object creation expressions
                // HttpClient, SqlConnection, *DbContext → Signal 1
                // Other concrete types → Signal 2
                case ObjectCreationExpressionSyntax creation:
                {
                    var typeName = GetSimpleTypeName(creation.Type);
                    if (typeName is null) break;

                    if (IsInfrastructureNewType(typeName))
                        infraCalls++;
                    else if (IsDirectInstantiationTarget(typeName))
                        directInstantiations++;
                    break;
                }

                // Signal 4: Static method calls on non-utility PascalCase types
                // Catches: LegacyHelper.Transform(), CacheManager.Invalidate()
                // Excludes: Math.Abs(), Convert.ToInt32(), DateTime.X (already Signal 1)
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax mac
                    && mac.Expression is IdentifierNameSyntax typeId
                    && typeId.Identifier.Text.Length > 0
                    && char.IsUpper(typeId.Identifier.Text[0])
                    && !InfrastructureStaticTypes.Contains(typeId.Identifier.Text)
                    && !SafeStaticTypes.Contains(typeId.Identifier.Text):
                    staticCalls++;
                    break;

                // Signal 5: Async seam calls — await receiver.MethodAsync(...)
                // Catches: await _httpClient.GetAsync(), await _dbContext.SaveChangesAsync()
                case AwaitExpressionSyntax awaitExpr
                    when awaitExpr.Expression is InvocationExpressionSyntax awaitInvoke
                    && awaitInvoke.Expression is MemberAccessExpressionSyntax awaitMac
                    && awaitMac.Name is IdentifierNameSyntax methodName
                    && AsyncIoMethodNames.Contains(methodName.Identifier.Text):
                    asyncSeamCalls++;
                    break;

                // Signal 6: Concrete downcasts — (ConcreteType)expr
                // Detects code that defeats interface abstractions
                case CastExpressionSyntax cast
                    when IsConcreteTypeName(cast.Type):
                    concreteCasts++;
                    break;

                // Signal 6: As-casts — expr as ConcreteType
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } binary
                    when IsConcreteTypeName(binary.Right as TypeSyntax):
                    concreteCasts++;
                    break;
            }
        }

        // Signal 3: Concrete constructor parameters (not restricted to executable code —
        // constructor parameters are always at declaration level)
        var concreteParams = CountConcreteConstructorParams(root);

        var rawScore = (infraCalls * 2.0) + (directInstantiations * 1.5) + (concreteParams * 0.5)
                     + (staticCalls * 1.0) + (asyncSeamCalls * 1.5) + (concreteCasts * 1.0);

        return new FileDependencyData
        {
            InfrastructureCalls = infraCalls,
            DirectInstantiations = directInstantiations,
            ConcreteConstructorParams = concreteParams,
            StaticCalls = staticCalls,
            AsyncSeamCalls = asyncSeamCalls,
            ConcreteCasts = concreteCasts,
            RawCouplingScore = rawScore
        };
    }

    private static int CountConcreteConstructorParams(Microsoft.CodeAnalysis.SyntaxNode root)
    {
        var count = 0;
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var param in ctor.ParameterList.Parameters)
            {
                if (param.Type != null && IsConcreteParameterType(param.Type))
                    count++;
            }
        }
        return count;
    }

    private static bool IsConcreteParameterType(Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax type)
    {
        var baseName = GetSimpleTypeName(type);
        if (baseName is null) return false;

        // C# primitive aliases (string, int, bool, etc.)
        if (PrimitiveTypes.Contains(baseName)) return false;

        // ITypeName convention: I followed by an uppercase letter = interface
        if (baseName.Length >= 2 && baseName[0] == 'I' && char.IsUpper(baseName[1])) return false;

        // Known safe framework types that are value-like or standard abstractions
        if (SafeConstructorParamTypes.Contains(baseName)) return false;

        return true;
    }

    private static bool IsInfrastructureNewType(string typeName)
    {
        if (InfrastructureNewTypes.Contains(typeName)) return true;
        if (typeName.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ContainsDiRegistrations(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax mac => mac.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            if (methodName != null && DiRegistrationMethods.Contains(methodName))
                return true;
        }
        return false;
    }

    private static bool IsConcreteTypeName(TypeSyntax? type)
    {
        if (type is null) return false;
        var name = GetSimpleTypeName(type);
        if (name is null) return false;
        if (name.Length == 0 || !char.IsUpper(name[0])) return false;
        if (PrimitiveTypes.Contains(name)) return false;
        if (name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1])) return false;
        if (SafeConstructorParamTypes.Contains(name)) return false;
        if (SafeNewExact.Contains(name)) return false;
        foreach (var suffix in SafeNewSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsDirectInstantiationTarget(string typeName)
    {
        // Only PascalCase types represent concrete dependencies
        if (typeName.Length == 0 || !char.IsUpper(typeName[0])) return false;

        // Safe exact names: collections, string helpers, timers, etc.
        if (SafeNewExact.Contains(typeName)) return false;

        // Safe suffixes: DTOs, value objects, exceptions, options, etc.
        foreach (var suffix in SafeNewSuffixes)
        {
            if (typeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string? GetSimpleTypeName(Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax gen => gen.Identifier.Text,
            QualifiedNameSyntax qual => GetSimpleTypeName(qual.Right),
            NullableTypeSyntax nullable => GetSimpleTypeName(nullable.ElementType),
            _ => null
        };
    }

    /// <summary>
    /// Returns true if <paramref name="node"/> is a descendant of a method, constructor,
    /// property accessor, or anonymous function body — i.e., inside executable code.
    /// Field initializers, attribute arguments, and other class-level declarations return false.
    /// </summary>
    private static bool IsInsideExecutableCode(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is BaseMethodDeclarationSyntax
                || current is AccessorDeclarationSyntax
                || current is AnonymousFunctionExpressionSyntax)
                return true;

            // Hit a type or namespace boundary without finding a method — stop
            if (current is TypeDeclarationSyntax
                || current is EnumDeclarationSyntax
                || current is NamespaceDeclarationSyntax
                || current is FileScopedNamespaceDeclarationSyntax
                || current is CompilationUnitSyntax)
                return false;

            current = current.Parent;
        }
        return false;
    }
}
