namespace Litmus.Tests.Helpers;

public static class TestFixtures
{
    public static string ValidCoberturaXml => """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0.5" version="1.0" timestamp="1234567890">
          <packages>
            <package name="MyApp">
              <classes>
                <class name="MyApp.Services.UserService" filename="MyApp/Services/UserService.cs" line-rate="0.23">
                  <lines>
                    <line number="1" hits="1"/>
                    <line number="2" hits="1"/>
                    <line number="3" hits="1"/>
                    <line number="4" hits="0"/>
                    <line number="5" hits="0"/>
                    <line number="6" hits="0"/>
                    <line number="7" hits="0"/>
                    <line number="8" hits="0"/>
                    <line number="9" hits="0"/>
                    <line number="10" hits="0"/>
                    <line number="11" hits="0"/>
                    <line number="12" hits="0"/>
                    <line number="13" hits="0"/>
                  </lines>
                </class>
                <class name="MyApp.Services.OrderService" filename="MyApp/Services/OrderService.cs" line-rate="0.85">
                  <lines>
                    <line number="1" hits="1"/>
                    <line number="2" hits="1"/>
                    <line number="3" hits="1"/>
                    <line number="4" hits="1"/>
                    <line number="5" hits="1"/>
                    <line number="6" hits="1"/>
                    <line number="7" hits="1"/>
                    <line number="8" hits="1"/>
                    <line number="9" hits="1"/>
                    <line number="10" hits="1"/>
                    <line number="11" hits="1"/>
                    <line number="12" hits="1"/>
                    <line number="13" hits="1"/>
                    <line number="14" hits="1"/>
                    <line number="15" hits="1"/>
                    <line number="16" hits="1"/>
                    <line number="17" hits="1"/>
                    <line number="18" hits="0"/>
                    <line number="19" hits="0"/>
                    <line number="20" hits="0"/>
                  </lines>
                </class>
                <class name="MyApp.Models.User" filename="MyApp/Models/User.cs" line-rate="1">
                  <lines>
                    <line number="1" hits="1"/>
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    /// <summary>
    /// Cobertura XML where a single file has multiple class entries —
    /// the main class plus a compiler-generated lambda closure class.
    /// Coverlet produces this layout for files containing lambdas/async.
    /// </summary>
    public static string CoberturaWithMultipleClassesPerFile => """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.6" branch-rate="0" version="1.0" timestamp="1234567890">
          <packages>
            <package name="MyApp">
              <classes>
                <class name="MyApp.Services.UserService" filename="MyApp/Services/UserService.cs" line-rate="1.0">
                  <lines>
                    <line number="1" hits="1"/>
                    <line number="2" hits="1"/>
                    <line number="3" hits="1"/>
                    <line number="10" hits="1"/>
                    <line number="11" hits="1"/>
                  </lines>
                </class>
                <class name="MyApp.Services.UserService+&lt;&gt;c" filename="MyApp/Services/UserService.cs" line-rate="0.2">
                  <lines>
                    <line number="20" hits="1"/>
                    <line number="21" hits="0"/>
                    <line number="22" hits="0"/>
                    <line number="23" hits="0"/>
                    <line number="24" hits="0"/>
                  </lines>
                </class>
                <class name="MyApp.Services.OrderService" filename="MyApp/Services/OrderService.cs" line-rate="0.5">
                  <lines>
                    <line number="1" hits="1"/>
                    <line number="2" hits="0"/>
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    public static string ValidSlnContent => """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "backend\MyApp\MyApp.csproj", "{12345678-1234-1234-1234-123456789012}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp.Tests", "backend\MyApp.Tests\MyApp.Tests.csproj", "{12345678-1234-1234-1234-123456789013}"
        EndProject
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{12345678-1234-1234-1234-123456789014}"
        EndProject
        Global
        EndGlobal
        """;

    public static string ValidSlnxContent => """
        <Solution>
          <Project Path="backend/MyApp/MyApp.csproj" />
          <Project Path="backend/MyApp.Tests/MyApp.Tests.csproj" />
          <Folder Name="Solution Items">
            <File Path="README.md" />
          </Folder>
        </Solution>
        """;

    public static string GitNumstatOutput => """
        10	5	backend/MyApp/Services/UserService.cs
        20	10	backend/MyApp/Services/OrderService.cs
        1	1	backend/MyApp/Models/User.cs

        30	15	backend/MyApp/Services/UserService.cs
        5	3	backend/MyApp/Services/OrderService.cs
        100	50	backend/MyApp/Controllers/HomeController.cs

        8	4	backend/MyApp/Services/UserService.cs
        """;

    public static string SimpleComplexityCode => """
        using System;

        namespace MyApp;

        public class Calculator
        {
            public int Calculate(int x, int y)
            {
                if (x > 0)
                {
                    return x + y;
                }
                if (y > 0)
                {
                    return y;
                }
                return 0;
            }
        }
        """;

    public static string SlnWithSingleProject => """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SingleProject", "src\SingleProject\SingleProject.csproj", "{12345678-1234-1234-1234-123456789012}"
        EndProject
        Global
        EndGlobal
        """;

    public static string SlnxWithSingleProject => """
        <Solution>
          <Project Path="src/SingleProject/SingleProject.csproj" />
        </Solution>
        """;

    public static string EmptySlnContent => """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Global
        EndGlobal
        """;

    public static string GitNumstatWithSpaces => """
        10	5	backend/MyApp/Services/My Service.cs
        """;

    public static string GitNumstatWithBinaryFiles => """
        10	5	backend/MyApp/Services/UserService.cs
        -	-	backend/MyApp/Resources/logo.png
        20	10	backend/MyApp/Services/OrderService.cs
        """;

    public static string NoBranchCode => """
        using System;

        namespace MyApp;

        public class Simple
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    // Phase 2 — Dependency analysis fixtures

    public static string CodeWithInfrastructureCalls => """
        public class Service
        {
            public void DoWork()
            {
                var now = DateTime.Now;
                var content = File.ReadAllText("path");
                var client = new HttpClient();
            }
        }
        """;

    public static string CodeWithDirectInstantiations => """
        public class Service
        {
            public void DoWork()
            {
                var repo = new UserRepository();
                var svc = new EmailService();
                var list = new List<int>();
                var dto = new UserDto();
            }
        }
        """;

    public static string CodeWithConcreteConstructorParams => """
        public class Service
        {
            private readonly UserRepository _repo;
            private readonly ILogger<Service> _logger;

            public Service(UserRepository repo, ILogger<Service> logger, string name)
            {
                _repo = repo;
                _logger = logger;
            }
        }
        """;

    public static string CodeWithStaticCalls => """
        public class Service
        {
            public void DoWork()
            {
                var result = MyHelper.Calculate(5);
                var abs = Math.Abs(-1);
                var converted = Convert.ToInt32("5");
            }
        }
        """;

    public static string CodeFullySeamed => """
        public class OrderService
        {
            private readonly IOrderRepository _repo;
            private readonly IDateTimeProvider _clock;

            public OrderService(IOrderRepository repo, IDateTimeProvider clock)
            {
                _repo = repo;
                _clock = clock;
            }

            public void Process()
            {
                var now = _clock.UtcNow;
                var orders = _repo.GetAll();
            }
        }
        """;

    public static string CodeMaximallyEntangled => """
        public class LegacySync
        {
            private readonly SqlConnection _conn;

            public LegacySync(SqlConnection conn, UserRepository repo)
            {
                _conn = conn;
            }

            public void Sync()
            {
                var now = DateTime.Now;
                var env = Environment.GetEnvironmentVariable("KEY");
                var client = new HttpClient();
                var parser = new XmlParser();
                var id = Guid.NewGuid();
                LegacyHelper.Transform(null);
                CacheManager.Invalidate();
            }
        }
        """;

    public static string CoberturaWithMethodElements => """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0" version="1.0" timestamp="1234567890">
          <packages>
            <package name="MyApp">
              <classes>
                <class name="MyApp.Services.OrderService" filename="MyApp/Services/OrderService.cs" line-rate="0.5">
                  <methods>
                    <method name="ProcessOrder" signature="(Order)">
                      <lines>
                        <line number="10" hits="1"/>
                        <line number="11" hits="1"/>
                        <line number="12" hits="0"/>
                        <line number="13" hits="0"/>
                      </lines>
                    </method>
                    <method name="ValidateInput" signature="(string)">
                      <lines>
                        <line number="20" hits="0"/>
                        <line number="21" hits="0"/>
                        <line number="22" hits="0"/>
                      </lines>
                    </method>
                    <method name="&lt;ProcessOrder&gt;b__0" signature="(int)">
                      <lines>
                        <line number="14" hits="1"/>
                      </lines>
                    </method>
                    <method name=".ctor" signature="()">
                      <lines>
                        <line number="5" hits="1"/>
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="5" hits="1"/>
                    <line number="10" hits="1"/>
                    <line number="11" hits="1"/>
                    <line number="12" hits="0"/>
                    <line number="13" hits="0"/>
                    <line number="14" hits="1"/>
                    <line number="20" hits="0"/>
                    <line number="21" hits="0"/>
                    <line number="22" hits="0"/>
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    public static string ComplexCode => """
        using System;

        namespace MyApp;

        public class ComplexClass
        {
            public void Method1(int x)
            {
                if (x > 0)
                {
                    for (int i = 0; i < x; i++)
                    {
                        while (i > 0)
                        {
                            Console.WriteLine(i);
                        }
                    }
                }
            }

            public void Method2(object obj)
            {
                foreach (var item in new[] { 1, 2, 3 })
                {
                    try
                    {
                        Console.WriteLine(item);
                    }
                    catch (Exception)
                    {
                        do
                        {
                            break;
                        } while (true);
                    }
                }

                var result = obj switch
                {
                    int n when n > 0 => "positive",
                    int n => "non-positive",
                    _ => "unknown"
                };

                var val = true && false || true;
                var x = obj ?? "default";
                var y = val ? 1 : 2;
            }
        }
        """;
}
