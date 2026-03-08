using Litmus.Abstractions;
using Litmus.Services;
using Litmus.Tests.Helpers;
using FluentAssertions;
using NSubstitute;

namespace Litmus.Tests.Services;

public class CoverageParserTests
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly CoverageParser _sut;

    public CoverageParserTests()
    {
        _sut = new CoverageParser(_fileSystem);
    }

    [Fact]
    public void Parse_ValidCobertura_ReturnsCoveragePerFile()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.ValidCoberturaXml);

        var result = _sut.Parse("coverage.xml");

        result.FileCoverage.Should().HaveCount(3);
        result.FileCoverage.Should().ContainKey("MyApp/Services/UserService.cs");
    }

    [Fact]
    public void GetCoverageForFile_FileNotInCoverage_ReturnsZero()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.ValidCoberturaXml);

        var result = _sut.Parse("coverage.xml");
        var coverage = CoverageParser.GetCoverageForFile(result, "MyApp/Services/NotCovered.cs");

        coverage.Should().Be(0.0);
    }

    [Fact]
    public void GetCoverageForFile_DifferentPathSeparators_StillMatches()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.ValidCoberturaXml);

        var result = _sut.Parse("coverage.xml");
        // Coverage XML has forward slashes; query with backslashes
        var coverage = CoverageParser.GetCoverageForFile(result, "MyApp\\Services\\UserService.cs");

        coverage.Should().BeApproximately(0.23, 0.01);
    }

    [Fact]
    public void Parse_MalformedXml_ThrowsClearException()
    {
        _fileSystem.ReadAllText("bad.xml").Returns("not valid xml <><>");

        var act = () => _sut.Parse("bad.xml");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid XML*");
    }

    [Fact]
    public void Parse_FullyCoveredFile_ReturnsOne()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.ValidCoberturaXml);

        var result = _sut.Parse("coverage.xml");
        var coverage = CoverageParser.GetCoverageForFile(result, "MyApp/Models/User.cs");

        coverage.Should().Be(1.0);
    }

    [Fact]
    public void Parse_ZeroCoverageFile_ReturnsZero()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.0">
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Uncovered" filename="MyApp/Uncovered.cs" line-rate="0">
                      <lines/>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        _fileSystem.ReadAllText("coverage.xml").Returns(xml);

        var result = _sut.Parse("coverage.xml");
        var coverage = CoverageParser.GetCoverageForFile(result, "MyApp/Uncovered.cs");

        coverage.Should().Be(0.0);
    }

    // -------------------------------------------------------------------------
    // Multi-class per file — coverlet generates multiple <class> entries for
    // files with compiler-generated types (lambdas, async state machines).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MultipleClassesPerFile_AggregatesLineData()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.CoberturaWithMultipleClassesPerFile);

        var result = _sut.Parse("coverage.xml");

        // UserService.cs: 10 lines total (5 from main class + 5 from <>c), 6 hit
        result.FileCoverage["MyApp/Services/UserService.cs"].Should().BeApproximately(0.6, 0.01,
            "coverage should aggregate lines across all class entries for the same file");
    }

    [Fact]
    public void Parse_MultipleClassesPerFile_SingleClassFileUnaffected()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.CoberturaWithMultipleClassesPerFile);

        var result = _sut.Parse("coverage.xml");

        // OrderService.cs has only one class entry — should match its line data
        result.FileCoverage["MyApp/Services/OrderService.cs"].Should().Be(0.5);
    }

    [Fact]
    public void Parse_MultipleClassesPerFile_DeduplicatesOverlappingLines()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.0">
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Svc" filename="Svc.cs" line-rate="0.5">
                      <lines>
                        <line number="1" hits="1"/>
                        <line number="2" hits="0"/>
                      </lines>
                    </class>
                    <class name="MyApp.Svc+Nested" filename="Svc.cs" line-rate="1">
                      <lines>
                        <line number="2" hits="1"/>
                        <line number="3" hits="1"/>
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        _fileSystem.ReadAllText("coverage.xml").Returns(xml);

        var result = _sut.Parse("coverage.xml");

        // Lines: 1(hit), 2(hit — max of 0 and 1), 3(hit) → 3/3 = 1.0
        result.FileCoverage["Svc.cs"].Should().Be(1.0,
            "overlapping line numbers should take the max hits, so line 2 counts as hit");
    }

    [Fact]
    public void Parse_ClassWithNoLineElements_FallsBackToLineRate()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.0">
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Empty" filename="Empty.cs" line-rate="0.75">
                      <lines/>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        _fileSystem.ReadAllText("coverage.xml").Returns(xml);

        var result = _sut.Parse("coverage.xml");

        result.FileCoverage["Empty.cs"].Should().Be(0.75,
            "when no <line> elements exist, should fall back to line-rate attribute");
    }

    // -------------------------------------------------------------------------
    // Merge — used by scan command when multiple test projects each produce a
    // coverage.cobertura.xml
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_EmptyInput_ReturnsEmptyResult()
    {
        var merged = CoverageParser.Merge([]);
        merged.FileCoverage.Should().BeEmpty();
    }

    [Fact]
    public void Merge_SingleResult_ReturnsItUnchanged()
    {
        var single = new CoverageResult
        {
            FileCoverage = new Dictionary<string, double>
            {
                ["Foo.cs"] = 0.75,
                ["Bar.cs"] = 0.0
            }
        };

        var merged = CoverageParser.Merge([single]);

        merged.FileCoverage.Should().BeEquivalentTo(single.FileCoverage);
    }

    [Fact]
    public void Merge_NonOverlappingResults_CombinesAll()
    {
        var a = new CoverageResult { FileCoverage = new() { ["A.cs"] = 0.5 } };
        var b = new CoverageResult { FileCoverage = new() { ["B.cs"] = 0.8 } };

        var merged = CoverageParser.Merge([a, b]);

        merged.FileCoverage.Should().HaveCount(2);
        merged.FileCoverage["A.cs"].Should().Be(0.5);
        merged.FileCoverage["B.cs"].Should().Be(0.8);
    }

    [Fact]
    public void Merge_OverlappingFiles_TakesHigherCoverage()
    {
        // TestProject1 covers Service.cs at 30%
        var proj1 = new CoverageResult { FileCoverage = new() { ["Service.cs"] = 0.30 } };
        // TestProject2 covers Service.cs at 80% — higher wins
        var proj2 = new CoverageResult { FileCoverage = new() { ["Service.cs"] = 0.80 } };

        var merged = CoverageParser.Merge([proj1, proj2]);

        merged.FileCoverage["Service.cs"].Should().Be(0.80,
            "the highest coverage across all test projects should win");
    }

    [Fact]
    public void Merge_OverlappingFiles_LowerCoverageDoesNotOverride()
    {
        var high = new CoverageResult { FileCoverage = new() { ["Service.cs"] = 0.90 } };
        var low  = new CoverageResult { FileCoverage = new() { ["Service.cs"] = 0.10 } };

        var merged = CoverageParser.Merge([high, low]);

        merged.FileCoverage["Service.cs"].Should().Be(0.90);
    }

    [Fact]
    public void Merge_MultipleResults_CombinesAndDeduplicatesCorrectly()
    {
        var proj1 = new CoverageResult
        {
            FileCoverage = new()
            {
                ["Services/OrderService.cs"] = 0.20,
                ["Models/Order.cs"] = 1.0
            }
        };
        var proj2 = new CoverageResult
        {
            FileCoverage = new()
            {
                ["Services/OrderService.cs"] = 0.85,  // higher → wins
                ["Controllers/OrderController.cs"] = 0.55
            }
        };

        var merged = CoverageParser.Merge([proj1, proj2]);

        merged.FileCoverage.Should().HaveCount(3);
        merged.FileCoverage["Services/OrderService.cs"].Should().Be(0.85);
        merged.FileCoverage["Models/Order.cs"].Should().Be(1.0);
        merged.FileCoverage["Controllers/OrderController.cs"].Should().Be(0.55);
    }

    [Fact]
    public void Parse_CoberturaWithMethods_ExtractsMethodCoverage()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.CoberturaWithMethodElements);

        var result = _sut.Parse("coverage.xml");

        result.MethodCoverage.Should().ContainKey("MyApp/Services/OrderService.cs");
        var methods = result.MethodCoverage["MyApp/Services/OrderService.cs"];

        methods.Should().Contain(m => m.Name == "ProcessOrder");
        methods.Should().Contain(m => m.Name == "ValidateInput");

        // Compiler-generated <ProcessOrder>b__0 and .ctor should be excluded
        methods.Should().NotContain(m => m.Name.Contains("<"));
        methods.Should().NotContain(m => m.Name.StartsWith("."));

        var processOrder = methods.First(m => m.Name == "ProcessOrder");
        processOrder.CoverageRate.Should().Be(0.5); // 2 of 4 lines hit

        var validateInput = methods.First(m => m.Name == "ValidateInput");
        validateInput.CoverageRate.Should().Be(0.0); // 0 of 3 lines hit
    }

    [Fact]
    public void Parse_CoberturaWithoutMethods_MethodCoverageEmpty()
    {
        _fileSystem.ReadAllText("coverage.xml").Returns(TestFixtures.ValidCoberturaXml);

        var result = _sut.Parse("coverage.xml");

        result.MethodCoverage.Should().BeEmpty();
    }

    [Fact]
    public void GetMethodCoverageForFile_SuffixMatching_Works()
    {
        var coverage = new CoverageResult
        {
            MethodCoverage =
            {
                ["MyApp/Services/OrderService.cs"] = [("ProcessOrder", 0.5)]
            }
        };

        var methods = CoverageParser.GetMethodCoverageForFile(coverage, "Services/OrderService.cs");

        methods.Should().ContainSingle().Which.Name.Should().Be("ProcessOrder");
    }

    [Fact]
    public void GetMethodCoverageForFile_NoMatch_ReturnsEmpty()
    {
        var coverage = new CoverageResult();

        var methods = CoverageParser.GetMethodCoverageForFile(coverage, "NoSuchFile.cs");

        methods.Should().BeEmpty();
    }

    [Fact]
    public void Merge_MethodCoverage_KeepsLargerSet()
    {
        var proj1 = new CoverageResult
        {
            MethodCoverage =
            {
                ["Service.cs"] = [("MethodA", 0.5)]
            }
        };
        var proj2 = new CoverageResult
        {
            MethodCoverage =
            {
                ["Service.cs"] = [("MethodA", 0.8), ("MethodB", 1.0)]
            }
        };

        var merged = CoverageParser.Merge([proj1, proj2]);

        merged.MethodCoverage["Service.cs"].Should().HaveCount(2);
    }
}
