using Litmus.Abstractions;
using Litmus.Services;
using Litmus.Tests.Helpers;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Litmus.Tests.Services;

public class ComplexityAnalyzerTests
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly ComplexityAnalyzer _sut;

    public ComplexityAnalyzerTests()
    {
        _sut = new ComplexityAnalyzer(_fileSystem);
    }

    [Fact]
    public void CalculateFileComplexity_NoBranches_ReturnsOne()
    {
        var complexity = ComplexityAnalyzer.CalculateFileComplexity(TestFixtures.NoBranchCode);

        complexity.Should().Be(1); // Base complexity of 1 for the single method
    }

    [Fact]
    public void CalculateFileComplexity_TwoIfStatements_ReturnsThree()
    {
        var complexity = ComplexityAnalyzer.CalculateFileComplexity(TestFixtures.SimpleComplexityCode);

        // 1 (base) + 2 (if statements) = 3
        complexity.Should().Be(3);
    }

    [Fact]
    public void CalculateFileComplexity_CountsAllBranchTypes()
    {
        var code = """
            public class Test
            {
                public void Method(int x, object obj)
                {
                    if (x > 0) { }                    // +1
                    for (int i = 0; i < x; i++) { }   // +1
                    foreach (var item in new[] {1}) { } // +1
                    while (x > 0) { x--; }             // +1
                    do { x--; } while (x > 0);         // +1
                    try { } catch (Exception) { }      // +1
                    var a = x > 0 ? 1 : 2;            // +1 (?:)
                    var b = obj ?? "default";           // +1 (??)
                    var c = true && false;              // +1 (&&)
                    var d = true || false;              // +1 (||)
                    switch (x)
                    {
                        case 1: break;                  // +1
                        case 2: break;                  // +1
                    }
                }
            }
            """;

        var complexity = ComplexityAnalyzer.CalculateFileComplexity(code);

        // 1 (base) + 12 branches = 13
        complexity.Should().Be(13);
    }

    [Fact]
    public void CalculateFileComplexity_MultipleMethods_SumsComplexity()
    {
        var code = """
            public class Test
            {
                public void Method1(int x)
                {
                    if (x > 0) { }
                }

                public void Method2(int x)
                {
                    if (x > 0) { }
                    if (x < 0) { }
                }
            }
            """;

        var complexity = ComplexityAnalyzer.CalculateFileComplexity(code);

        // Method1: 1 (base) + 1 (if) = 2
        // Method2: 1 (base) + 2 (if) = 3
        // Total: 5
        complexity.Should().Be(5);
    }

    [Fact]
    public void Analyze_NormalizesComplexity_HighestFileScoresOne()
    {
        var projectDirs = new List<string> { "MyApp" };
        var fullDir = Path.Combine("/repo", "MyApp");
        var simpleFile = Path.Combine("/repo", "MyApp", "Simple.cs");
        var complexFile = Path.Combine("/repo", "MyApp", "Complex.cs");

        _fileSystem.DirectoryExists(fullDir).Returns(true);
        _fileSystem.GetFiles(fullDir, "*.cs", true)
            .Returns([simpleFile, complexFile]);
        _fileSystem.ReadAllText(simpleFile).Returns(TestFixtures.NoBranchCode);
        _fileSystem.ReadAllText(complexFile).Returns(TestFixtures.ComplexCode);

        var result = _sut.Analyze("/repo", projectDirs, []);

        result.FileComplexityNorm.Values.Max().Should().Be(1.0);
    }

    [Fact]
    public void Analyze_SkippedFiles_ExcludedFromNormalization()
    {
        var projectDirs = new List<string> { "MyApp" };
        var fullDir = Path.Combine("/repo", "MyApp");
        var goodFile = Path.Combine("/repo", "MyApp", "Good.cs");
        var badFile = Path.Combine("/repo", "MyApp", "Bad.cs");

        _fileSystem.DirectoryExists(fullDir).Returns(true);
        _fileSystem.GetFiles(fullDir, "*.cs", true)
            .Returns([goodFile, badFile]);
        _fileSystem.ReadAllText(goodFile).Returns(TestFixtures.NoBranchCode);
        _fileSystem.ReadAllText(badFile).Throws(new IOException("encoding issue"));

        var result = _sut.Analyze("/repo", projectDirs, []);

        result.SkippedFiles.Should().Be(1);
        result.FileComplexity.Should().HaveCount(1);
        result.FileComplexityNorm.Should().HaveCount(1);
    }

    [Fact]
    public void Analyze_OnlyAnalyzesFilesUnderProjectDirectories()
    {
        var projectDirs = new List<string> { "backend/MyApp" };
        var fullDir = Path.Combine("/repo", "backend/MyApp");
        var serviceFile = Path.Combine("/repo", "backend/MyApp", "Service.cs");

        _fileSystem.DirectoryExists(fullDir).Returns(true);
        _fileSystem.GetFiles(fullDir, "*.cs", true)
            .Returns([serviceFile]);
        _fileSystem.ReadAllText(serviceFile).Returns(TestFixtures.NoBranchCode);

        var result = _sut.Analyze("/repo", projectDirs, []);

        result.FileComplexity.Should().HaveCount(1);
        _fileSystem.DidNotReceive().GetFiles("/repo", Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public void Analyze_WithProgressCallback_InvokesPerFile()
    {
        var projectDirs = new List<string> { "MyApp" };
        var fullDir = Path.Combine("/repo", "MyApp");
        var file1 = Path.Combine("/repo", "MyApp", "A.cs");
        var file2 = Path.Combine("/repo", "MyApp", "B.cs");

        _fileSystem.DirectoryExists(fullDir).Returns(true);
        _fileSystem.GetFiles(fullDir, "*.cs", true).Returns([file1, file2]);
        _fileSystem.ReadAllText(file1).Returns(TestFixtures.NoBranchCode);
        _fileSystem.ReadAllText(file2).Returns(TestFixtures.NoBranchCode);

        var callCount = 0;
        _sut.Analyze("/repo", projectDirs, [], onFileProcessed: () => callCount++);

        callCount.Should().Be(2);
    }

    [Fact]
    public void CalculateMethodComplexities_SimpleClass_ReturnsMethodNameAndComplexity()
    {
        var result = ComplexityAnalyzer.CalculateMethodComplexities(TestFixtures.SimpleComplexityCode);

        result.Should().ContainSingle()
            .Which.Should().Be(("Calculate", 3));
    }

    [Fact]
    public void CalculateMethodComplexities_ComplexClass_ReturnsMultipleMethods()
    {
        var result = ComplexityAnalyzer.CalculateMethodComplexities(TestFixtures.ComplexCode);

        result.Should().HaveCount(2);
        result.Should().Contain(m => m.Name == "Method1");
        result.Should().Contain(m => m.Name == "Method2");

        var method1 = result.First(m => m.Name == "Method1");
        var method2 = result.First(m => m.Name == "Method2");
        method1.Complexity.Should().BeGreaterThan(1);
        method2.Complexity.Should().BeGreaterThan(method1.Complexity);
    }

    [Fact]
    public void CalculateMethodComplexities_NoBranchCode_ReturnsBaseComplexity()
    {
        var result = ComplexityAnalyzer.CalculateMethodComplexities(TestFixtures.NoBranchCode);

        result.Should().ContainSingle()
            .Which.Should().Be(("Add", 1));
    }

    [Fact]
    public void CalculateMethodComplexities_Constructor_IncludesConstructor()
    {
        var code = """
            public class Service
            {
                public Service(int x)
                {
                    if (x > 0) { }
                }

                public void DoWork() { }
            }
            """;

        var result = ComplexityAnalyzer.CalculateMethodComplexities(code);

        result.Should().HaveCount(2);
        result.Should().Contain(("Service", 2));
        result.Should().Contain(("DoWork", 1));
    }

    [Fact]
    public void CalculateMethodComplexities_ExcludesPropertyAccessors()
    {
        var code = """
            public class Model
            {
                private int _value;
                public int Value
                {
                    get { if (_value > 0) return _value; return 0; }
                    set { _value = value; }
                }

                public void DoWork() { }
            }
            """;

        var result = ComplexityAnalyzer.CalculateMethodComplexities(code);

        result.Should().ContainSingle()
            .Which.Name.Should().Be("DoWork");
    }

    [Fact]
    public void Analyze_PopulatesMethodComplexity()
    {
        var projectDirs = new List<string> { "MyApp" };
        var fullDir = Path.Combine("/repo", "MyApp");
        var file = Path.Combine("/repo", "MyApp", "Calculator.cs");

        _fileSystem.DirectoryExists(fullDir).Returns(true);
        _fileSystem.GetFiles(fullDir, "*.cs", true).Returns([file]);
        _fileSystem.ReadAllText(file).Returns(TestFixtures.SimpleComplexityCode);

        var result = _sut.Analyze("/repo", projectDirs, []);

        result.MethodComplexity.Should().ContainKey("MyApp/Calculator.cs");
        result.MethodComplexity["MyApp/Calculator.cs"].Should().ContainSingle()
            .Which.Name.Should().Be("Calculate");
    }
}
