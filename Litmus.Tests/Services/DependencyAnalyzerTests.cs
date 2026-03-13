using Litmus.Abstractions;
using Litmus.Services;
using Litmus.Tests.Helpers;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Litmus.Tests.Services;

public class DependencyAnalyzerTests
{
    // -------------------------------------------------------------------------
    // Signal 1 — Unseamed infrastructure calls (weight 2.0)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_DateTimeNow_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var t = DateTime.Now; }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.StaticCalls.Should().Be(0, "DateTime is excluded from Signal 4 to avoid double counting");
    }

    [Fact]
    public void AnalyzeFile_FileReadAllText_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var s = File.ReadAllText("path"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.StaticCalls.Should().Be(0, "File is excluded from Signal 4");
    }

    [Fact]
    public void AnalyzeFile_EnvironmentGetVariable_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var v = Environment.GetEnvironmentVariable("KEY"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_GuidNewGuid_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var id = Guid.NewGuid(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.StaticCalls.Should().Be(0, "Guid.NewGuid is Signal 1, not Signal 4");
    }

    [Fact]
    public void AnalyzeFile_NewHttpClient_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var c = new HttpClient(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.DirectInstantiations.Should().Be(0, "HttpClient must not be double-counted in Signal 2");
    }

    [Fact]
    public void AnalyzeFile_NewSqlConnection_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var conn = new SqlConnection("cs"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.DirectInstantiations.Should().Be(0);
    }

    [Fact]
    public void AnalyzeFile_NewDbContextSubclass_CountsAsInfrastructureCall()
    {
        var code = """
            public class C {
                public void M() { var ctx = new AppDbContext(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.DirectInstantiations.Should().Be(0);
    }

    [Fact]
    public void AnalyzeFile_MultipleInfrastructureCalls_CountsAll()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeWithInfrastructureCalls);
        // DateTime.Now, File.ReadAllText, new HttpClient() = 3
        result.InfrastructureCalls.Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // Signal 2 — Direct instantiation inside methods (weight 1.5)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_NewConcreteType_CountsAsDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { var r = new UserRepository(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_NewListGeneric_ExcludedFromDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { var l = new List<string>(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(0, "List<T> is a safe collection");
    }

    [Fact]
    public void AnalyzeFile_NewDictionaryGeneric_ExcludedFromDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { var d = new Dictionary<string, int>(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(0, "Dictionary<K,V> is a safe collection");
    }

    [Fact]
    public void AnalyzeFile_NewExceptionSuffix_ExcludedFromDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { throw new ArgumentException("msg"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(0, "ArgumentException ends with Exception");
    }

    [Fact]
    public void AnalyzeFile_NewDtoSuffix_ExcludedFromDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { var dto = new UserDto(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(0, "UserDto ends with Dto");
    }

    [Fact]
    public void AnalyzeFile_NewOptionsSuffix_ExcludedFromDirectInstantiation()
    {
        var code = """
            public class C {
                public void M() { var opts = new RetryOptions(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.DirectInstantiations.Should().Be(0, "RetryOptions ends with Options");
    }

    [Fact]
    public void AnalyzeFile_MixedInstantiations_OnlyConcreteCounted()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeWithDirectInstantiations);
        // UserRepository and EmailService = 2; List<int> and UserDto excluded
        result.DirectInstantiations.Should().Be(2);
        result.InfrastructureCalls.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Signal 3 — Concrete constructor parameters (weight 0.5)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_ConcreteConstructorParam_Counted()
    {
        var code = """
            public class C {
                public C(UserRepository repo) { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteConstructorParams.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_InterfaceParamByConvention_NotCounted()
    {
        var code = """
            public class C {
                public C(IUserRepository repo) { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteConstructorParams.Should().Be(0, "IUserRepository follows ITypeName convention");
    }

    [Fact]
    public void AnalyzeFile_PrimitiveConstructorParams_NotCounted()
    {
        var code = """
            public class C {
                public C(string name, int count, bool enabled) { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteConstructorParams.Should().Be(0, "string, int, bool are primitives");
    }

    [Fact]
    public void AnalyzeFile_CancellationTokenParam_NotCounted()
    {
        var code = """
            public class C {
                public C(CancellationToken ct) { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteConstructorParams.Should().Be(0, "CancellationToken is a known safe type");
    }

    [Fact]
    public void AnalyzeFile_ILoggerGenericParam_NotCounted()
    {
        var code = """
            public class C {
                public C(ILogger<C> logger) { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteConstructorParams.Should().Be(0, "ILogger<T> follows ITypeName convention");
    }

    [Fact]
    public void AnalyzeFile_MixedConstructorParams_OnlyConcreteCounted()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeWithConcreteConstructorParams);
        // UserRepository = 1 concrete; ILogger<Service> and string excluded
        result.ConcreteConstructorParams.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Signal 4 — Static calls on non-utility types (weight 1.0)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_CustomStaticCall_CountsAsStaticCall()
    {
        var code = """
            public class C {
                public void M() { LegacyHelper.Transform(null); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.StaticCalls.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_TwoCustomStaticCalls_CountsBoth()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeWithStaticCalls);
        // MyHelper.Calculate = 1; Math.Abs and Convert.ToInt32 excluded
        result.StaticCalls.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_MathAbsCall_NotCountedAsStaticCall()
    {
        var code = """
            public class C {
                public void M() { var x = Math.Abs(-1); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.StaticCalls.Should().Be(0, "Math is a safe utility type");
    }

    [Fact]
    public void AnalyzeFile_ConvertCall_NotCountedAsStaticCall()
    {
        var code = """
            public class C {
                public void M() { var x = Convert.ToInt32("5"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.StaticCalls.Should().Be(0, "Convert is a safe utility type");
    }

    [Fact]
    public void AnalyzeFile_EnumerableLinqCall_NotCountedAsStaticCall()
    {
        var code = """
            public class C {
                public void M() { var s = Enumerable.Empty<int>(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.StaticCalls.Should().Be(0, "Enumerable is a safe utility type");
    }

    // -------------------------------------------------------------------------
    // Boundary: code outside method bodies must not be counted
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_InfrastructureCallInFieldInitializer_NotCounted()
    {
        var code = """
            public class C {
                private static readonly DateTime _start = DateTime.Now;
                public void M() { }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(0,
            "Field initializers are not inside executable code — no seam exists but cost is at construction, not invocation");
    }

    [Fact]
    public void AnalyzeFile_EmptyClass_AllSignalsZero()
    {
        var result = DependencyAnalyzer.AnalyzeFile("public class Empty {}");
        result.InfrastructureCalls.Should().Be(0);
        result.DirectInstantiations.Should().Be(0);
        result.ConcreteConstructorParams.Should().Be(0);
        result.StaticCalls.Should().Be(0);
        result.RawCouplingScore.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Raw score formula validation
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_RawScoreCalculatedCorrectly()
    {
        // 1 infra call (×2.0) + 1 direct instantiation (×1.5) = 3.5
        var code = """
            public class C {
                public void M()
                {
                    var now = DateTime.Now;
                    var repo = new UserRepository();
                }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.InfrastructureCalls.Should().Be(1);
        result.DirectInstantiations.Should().Be(1);
        result.RawCouplingScore.Should().Be(3.5);
    }

    [Fact]
    public void AnalyzeFile_FullySeamed_ScoresZero()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeFullySeamed);
        result.RawCouplingScore.Should().Be(0,
            "All dependencies are injected via interfaces — perfect seaming");
    }

    [Fact]
    public void AnalyzeFile_MaximallyEntangled_ScoresHigh()
    {
        var result = DependencyAnalyzer.AnalyzeFile(TestFixtures.CodeMaximallyEntangled);
        result.RawCouplingScore.Should().BeGreaterThan(5.0,
            "Multiple infraCalls, instantiations, concrete params, and static calls");
    }

    // -------------------------------------------------------------------------
    // Analyze method — file system integration + normalization
    // -------------------------------------------------------------------------

    [Fact]
    public void Analyze_MaxFileScoredOne_OtherFilesProportional()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true)
            .Returns(["/repo/proj/Entangled.cs", "/repo/proj/Seamed.cs"]);
        fs.ReadAllText("/repo/proj/Entangled.cs").Returns(TestFixtures.CodeMaximallyEntangled);
        fs.ReadAllText("/repo/proj/Seamed.cs").Returns(TestFixtures.CodeFullySeamed);

        var analyzer = new DependencyAnalyzer(fs);
        var result = analyzer.Analyze("/repo", ["proj"], []);

        var entangled = result.Files["proj/Entangled.cs"];
        var seamed = result.Files["proj/Seamed.cs"];

        entangled.CouplingNorm.Should().Be(1.0, "highest raw score normalizes to 1.0");
        seamed.CouplingNorm.Should().Be(0.0, "zero raw score normalizes to 0.0");
    }

    [Fact]
    public void Analyze_AllFilesZeroScore_AllNormsZero()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true)
            .Returns(["/repo/proj/A.cs", "/repo/proj/B.cs"]);
        fs.ReadAllText(Arg.Any<string>()).Returns(TestFixtures.CodeFullySeamed);

        var analyzer = new DependencyAnalyzer(fs);
        var result = analyzer.Analyze("/repo", ["proj"], []);

        result.Files.Values.Should().AllSatisfy(f => f.CouplingNorm.Should().Be(0.0));
    }

    [Fact]
    public void Analyze_UnreadableFile_IncrementsSkippedCount()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true).Returns(["/repo/proj/Bad.cs"]);
        fs.ReadAllText("/repo/proj/Bad.cs").Throws(new IOException("disk error"));

        var analyzer = new DependencyAnalyzer(fs);
        var result = analyzer.Analyze("/repo", ["proj"], []);

        result.SkippedFiles.Should().Be(1);
        result.Files.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_PathsAreGitRootRelativeWithForwardSlashes()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true)
            .Returns(["/repo/src/MyApp/Services/UserService.cs"]);
        fs.ReadAllText(Arg.Any<string>()).Returns(TestFixtures.NoBranchCode);

        var analyzer = new DependencyAnalyzer(fs);
        var result = analyzer.Analyze("/repo", ["src/MyApp"], []);

        result.Files.Keys.Should().ContainSingle()
            .Which.Should().Be("src/MyApp/Services/UserService.cs");
    }

    [Fact]
    public void Analyze_WithProgressCallback_InvokesPerFile()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true)
            .Returns(["/repo/proj/A.cs", "/repo/proj/B.cs", "/repo/proj/C.cs"]);
        fs.ReadAllText(Arg.Any<string>()).Returns(TestFixtures.NoBranchCode);

        var analyzer = new DependencyAnalyzer(fs);
        var callCount = 0;
        analyzer.Analyze("/repo", ["proj"], [], onFileProcessed: () => callCount++);

        callCount.Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // Signal 5 — Async seam calls (weight 1.5)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_AwaitHttpClientGetAsync_CountsAsAsyncSeamCall()
    {
        var code = """
            public class C {
                private HttpClient _client;
                public async Task M() { var r = await _client.GetAsync("/api"); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.AsyncSeamCalls.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_AwaitSaveChangesAsync_CountsAsAsyncSeamCall()
    {
        var code = """
            public class C {
                private DbContext _db;
                public async Task M() { await _db.SaveChangesAsync(); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.AsyncSeamCalls.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_AwaitNonIoMethod_NotCountedAsAsyncSeam()
    {
        var code = """
            public class C {
                public async Task M() { await Task.Delay(100); }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.AsyncSeamCalls.Should().Be(0);
    }

    [Fact]
    public void AnalyzeFile_MultipleAsyncCalls_CountsEach()
    {
        var code = """
            public class C {
                private HttpClient _client;
                public async Task M() {
                    await _client.GetAsync("/a");
                    await _client.PostAsync("/b", null);
                }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.AsyncSeamCalls.Should().Be(2);
    }

    // -------------------------------------------------------------------------
    // Signal 6 — Concrete downcasts (weight 1.0)
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeFile_CastToConcreteType_CountsAsConcreteCast()
    {
        var code = """
            public class C {
                public void M(object o) { var x = (UserRepository)o; }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteCasts.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_AsCastToConcreteType_CountsAsConcreteCast()
    {
        var code = """
            public class C {
                public void M(object o) { var x = o as UserRepository; }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteCasts.Should().Be(1);
    }

    [Fact]
    public void AnalyzeFile_CastToInterface_NotCountedAsConcreteCast()
    {
        var code = """
            public class C {
                public void M(object o) { var x = (IUserRepository)o; }
            }
            """;
        var result = DependencyAnalyzer.AnalyzeFile(code);
        result.ConcreteCasts.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // DI registration file detection
    // -------------------------------------------------------------------------

    [Fact]
    public void Analyze_RegistrationFile_ZerosDependencyScore()
    {
        var registrationCode = """
            public static class ServiceRegistration {
                public static void Register(IServiceCollection services) {
                    services.AddScoped<IOrderService, OrderService>();
                    services.AddSingleton<ICache, RedisCache>();
                }
            }
            """;
        var fs = Substitute.For<IFileSystem>();
        fs.DirectoryExists(Arg.Any<string>()).Returns(true);
        fs.GetFiles(Arg.Any<string>(), "*.cs", true).Returns(["/repo/proj/Registration.cs"]);
        fs.ReadAllText(Arg.Any<string>()).Returns(registrationCode);

        var analyzer = new DependencyAnalyzer(fs);
        var result = analyzer.Analyze("/repo", ["proj"], []);

        var file = result.Files.Values.Single();
        file.IsRegistrationFile.Should().BeTrue();
        file.RawCouplingScore.Should().Be(0);
        file.CouplingNorm.Should().Be(0);
    }
}
