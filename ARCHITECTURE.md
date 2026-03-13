# Architecture & Technical Deep Dive

This document covers the internal design of Litmus: how each component works, why specific decisions were made, and the tradeoffs involved.

## Project Structure

```
Litmus.slnx
├── Litmus/                   # Main CLI tool
│   ├── Abstractions/                  # Interfaces for I/O boundaries
│   │   ├── IFileSystem.cs
│   │   ├── IProcessRunner.cs
│   │   ├── FileSystemWrapper.cs
│   │   └── ProcessRunner.cs
│   ├── Commands/
│   │   ├── AnalyzeCommand.cs          # CLI definition + orchestration
│   │   └── ScanCommand.cs             # One-step test + analyze
│   ├── Models/
│   │   ├── AnalysisOptions.cs         # Parsed CLI options
│   │   └── FileRiskReport.cs          # Per-file output model
│   ├── Output/
│   │   └── ReportRenderer.cs          # Table, JSON, CSV, HTML output
│   ├── Services/
│   │   ├── SolutionParser.cs          # .sln / .slnx parsing
│   │   ├── GitChurnAnalyzer.cs        # Git log --numstat analysis
│   │   ├── CoverageParser.cs          # Cobertura XML parsing
│   │   ├── ComplexityAnalyzer.cs      # Roslyn-based cyclomatic complexity
│   │   ├── DependencyAnalyzer.cs     # Roslyn-based coupling/seam detection (Phase 2)
│   │   ├── FileFilterHelper.cs       # Glob pattern matching
│   │   └── RiskScorer.cs              # Final risk + priority formula
│   └── Program.cs                     # Entry point
└── Litmus.Tests/             # Tests (xUnit)
    ├── Helpers/
    │   └── TestFixtures.cs            # Shared test data
    ├── Integration/
    │   ├── IntegrationTestFixture.cs  # Real temp git repo setup
    │   └── AnalyzeCommandIntegrationTests.cs
    └── Services/
        ├── SolutionParserTests.cs
        ├── GitChurnAnalyzerTests.cs
        ├── CoverageParserTests.cs
        ├── ComplexityAnalyzerTests.cs
        ├── DependencyAnalyzerTests.cs # 6-signal seam detection tests
        ├── FileFilterHelperTests.cs
        ├── RiskScorerTests.cs
        └── ReportRendererTests.cs
```

## Analysis Pipeline

The tool executes a 5-step pipeline, each step producing a typed result consumed by the next:

```
Solution Path ─► SolutionParser ─► SolutionParseResult
                                       │
                      ┌────────────────┼────────────────┐
                      ▼                ▼                ▼
              GitChurnAnalyzer  ComplexityAnalyzer  DependencyAnalyzer
                      │                │                │
                      ▼                ▼                ▼      (parallel via Task.WhenAll)
                 ChurnResult    ComplexityResult   DependencyResult
                      │                │                │
                      └────────────────┼────────────────┘
                                       ▼
                          RiskScorer (+CoverageResult)
                                       │
                                       ▼
                              List<FileRiskReport>
                                       │
                                       ▼
                           ReportRenderer (+baseline?)
```

Steps 1-3 (churn, complexity, dependency) run in parallel using `Task.Run` + `Task.WhenAll`. Each analyzer creates its own result object with no shared mutable state, so parallelism is safe. Coverage is parsed before `RunAnalysis` is called and passed in directly.

---

## Component Details

### 1. SolutionParser

**File:** `Services/SolutionParser.cs`

**Purpose:** Discover which project directories belong to the solution, and locate the git repository root.

**How it works:**

- Detects format from extension (`.sln` vs `.slnx`)
- For `.sln`: uses a compiled `[GeneratedRegex]` to extract `Project("...")` entries, filtering to `.csproj` paths only
- For `.slnx`: parses XML via `System.Xml.Linq`, reads `<Project Path="..." />` elements from the `<Solution>` root
- Runs `git rev-parse --show-toplevel` to find the git root
- Converts csproj paths to project directories, expressed as git-root-relative paths with forward slashes

**Design decision -- regex vs MSBuild for .sln:**
Classic `.sln` files use a proprietary text format, not XML. Using MSBuild's `SolutionFile` API would add a heavy dependency. A regex captures the `Project(...)` lines reliably for the data we need (just the csproj path). For `.slnx`, the XML format is clean and `XDocument` is the natural choice.

**Tradeoff:** The regex won't handle pathological `.sln` files with unusual quoting. In practice, these files are machine-generated and follow a consistent format.

### 2. GitChurnAnalyzer

**File:** `Services/GitChurnAnalyzer.cs`

**Purpose:** Measure how frequently each `.cs` file changes, weighted by lines modified.

**How it works:**

- Runs `git log --since=<date> --numstat --pretty=format:"" -- <pathspecs>`
- Pathspecs scope the query to `<projectDir>/**/*.cs` for each project directory
- Parses the tab-separated `added\tdeleted\tfilepath` lines
- Applies a **noise floor**: commits touching <= 2 lines in a file are discarded (whitespace fixes, using changes)
- Computes `WeightedChurn = sum(added + deleted)` across all qualifying commits
- Normalizes: `ChurnNorm = WeightedChurn / max(WeightedChurn)`, producing values in [0, 1]

**Design decision -- shelling out to git vs LibGit2Sharp:**
LibGit2Sharp is a managed wrapper around libgit2. It would avoid process spawning, but it:
- Adds a large native dependency (~5 MB)
- Lags behind git features
- Makes the tool harder to package as a global tool (native libs per platform)

Shelling out to `git` is simpler, universally available, and produces the exact same data. The process overhead is negligible since we make a single `git log` call.

**Design decision -- weighted churn vs commit count:**
Pure commit count treats a 1-line typo fix the same as a 500-line refactor. Weighting by `added + deleted` better captures "how much work is happening in this file." The noise floor (<=2 lines) filters trivial changes like import additions.

**Glob matching:**
Exclusion patterns use a custom glob-to-regex converter. Both `*` and `**` map to `.*` (match any characters including path separators). This is intentional -- patterns like `*Migrations/*.cs` need to match `MyApp/Migrations/Init.cs` where the leading segment contains a path separator.

### 3. CoverageParser

**File:** `Services/CoverageParser.cs`

**Purpose:** Extract per-file line coverage rates from a Cobertura XML report.

**How it works:**

- Parses the XML with `System.Xml.Linq`
- Iterates `<class>` elements, reading `filename` and `line-rate` attributes
- Normalizes path separators to `/`
- For duplicate class entries (partial classes, multiple classes per file), the last entry wins

**File matching (`GetCoverageForFile`):**
Coverage reports use absolute paths or paths relative to the build root, which rarely match solution-relative paths exactly. The matcher uses suffix matching:

```csharp
path.EndsWith(target) || target.EndsWith(path)
```

This handles cases like:
- Coverage says `/home/ci/src/MyApp/Service.cs`
- Churn says `MyApp/Service.cs`

**Tradeoff:** Suffix matching could produce false positives if two files share the same name at different depths (e.g., `A/Utils.cs` and `B/Utils.cs`). In practice, .NET projects rarely have this collision, and exact-match is tried first.

### 4. ComplexityAnalyzer

**File:** `Services/ComplexityAnalyzer.cs`

**Purpose:** Compute cyclomatic complexity for each `.cs` file using Roslyn.

**How it works:**

- Parses each file with `CSharpSyntaxTree.ParseText()` (syntax-only, no semantic model needed)
- Walks the AST to find all `BaseMethodDeclarationSyntax` nodes (methods, constructors, operators)
- Also counts `AccessorDeclarationSyntax` with bodies (property getters/setters with logic)
- Each method starts with a base complexity of **1**
- Adds **+1** for each branching construct:

| Construct | AST Node |
|---|---|
| `if` | `IfStatementSyntax` |
| `for` | `ForStatementSyntax` |
| `foreach` | `ForEachStatementSyntax` |
| `while` | `WhileStatementSyntax` |
| `do..while` | `DoStatementSyntax` |
| `catch` | `CatchClauseSyntax` |
| `case` | `CaseSwitchLabelSyntax`, `CasePatternSwitchLabelSyntax` |
| `when` | `WhenClauseSyntax` |
| `? :` | `ConditionalExpressionSyntax` |
| `&&` | `BinaryExpressionSyntax` (AmpersandAmpersandToken) |
| `\|\|` | `BinaryExpressionSyntax` (BarBarToken) |
| `??` | `BinaryExpressionSyntax` (QuestionQuestionToken) |

- File complexity = sum of all method complexities
- Normalization: `ComplexityNorm = Complexity / max(Complexity)` across all files

**Design decision -- Roslyn syntax tree vs regex:**
A regex-based approach (counting `if`, `for`, etc. as strings) is fragile -- it matches keywords inside strings, comments, and identifiers. Roslyn's syntax tree gives exact node types with zero ambiguity. The cost is a heavier dependency (~15 MB), but it's already a standard .NET package.

**Design decision -- syntax-only parsing:**
Using `CSharpSyntaxTree.ParseText()` instead of building a full `Compilation` means we don't need project references, NuGet packages, or a semantic model. This makes it fast (milliseconds per file) and dependency-free at analysis time. The tradeoff is we can't resolve types or detect dead code, but cyclomatic complexity only needs syntax.

**Error handling:**
Files that fail to read (encoding issues, locked files) are silently skipped and counted in `SkippedFiles`. This avoids aborting the entire analysis over one unreadable file.

### 5. RiskScorer

**File:** `Services/RiskScorer.cs`

**Purpose:** Combine the three signals into a single risk score per file.

**Formula:**

```
RiskScore = ChurnNorm × (1 - CoverageRate) × (1 + ComplexityNorm)
```

**Why this formula:**

| Factor | Effect |
|---|---|
| `ChurnNorm` | Files that never change get score 0, regardless of coverage or complexity. No churn = no risk of regression. |
| `(1 - CoverageRate)` | 100% covered files get score 0 -- if every line is tested, churn is safely caught. 0% coverage = full penalty. |
| `(1 + ComplexityNorm)` | Complexity acts as a **multiplier** (1x to 2x). A complex file with high churn and low coverage is riskier than a simple one. The `+1` ensures complexity never zeroes out the score -- it only amplifies. |

**Score range:** [0, 2.0]. The maximum occurs when `ChurnNorm = 1.0`, `CoverageRate = 0.0`, `ComplexityNorm = 1.0`.

**Risk levels:**

| Level | Threshold | Rationale |
|---|---|---|
| High | >= 0.6 | Roughly the top 30% of a typical distribution |
| Medium | >= 0.2 | Meaningful combined signal |
| Low | < 0.2 | Below the noise threshold |

**Path reconciliation:** Churn data uses solution-relative paths, complexity uses git-root-relative paths. The scorer converts between them using `Path.GetRelativePath`. Coverage uses suffix matching, so it's inherently flexible.

### 6. ReportRenderer

**File:** `Output/ReportRenderer.cs`

**Purpose:** Present results in a terminal table with optional file export.

**Terminal output:** Uses `Spectre.Console` for a bordered table with:
- Red rows for **High** risk
- Yellow rows for **Medium** risk
- Default color for **Low** risk
- Respects `--no-color` for CI environments

**Export formats:**
- **JSON:** Serialized with `System.Text.Json`, camelCase naming, indented
- **CSV:** Manual StringBuilder with proper field escaping (commas, quotes, newlines)
- **HTML:** Self-contained `.html` file with inline CSS/JS, sortable table columns, color-coded risk/priority levels, and baseline delta support

**Design decision -- Spectre.Console vs raw Console.WriteLine:**
Spectre provides cross-platform ANSI color support, table rendering with borders and alignment, and automatic terminal width handling. The alternative would be manual padding and escape codes, which is error-prone across terminals.

---

## Abstractions & Testability

All I/O operations are behind two interfaces:

### IFileSystem

```csharp
public interface IFileSystem
{
    string ReadAllText(string path);
    IEnumerable<string> GetFiles(string directory, string pattern, bool recursive);
    bool FileExists(string path);
    bool DirectoryExists(string path);
}
```

### IProcessRunner

```csharp
public interface IProcessRunner
{
    string Run(string executable, string arguments, string workingDirectory);
    int RunWithLiveOutput(string executable, string arguments, string workingDirectory,
        Action<string>? onOutput = null, int timeoutMs = 0);
}
```

`Run` captures all output and returns stdout as a string. `RunWithLiveOutput` streams output line-by-line via a callback and supports a timeout that kills the entire process tree — used by `scan` for `dotnet test` where real-time feedback and hang protection are needed.

These are injected via constructor parameters, not a DI container. The production implementations (`FileSystemWrapper`, `ProcessRunner`) are simple pass-throughs to `System.IO` and `System.Diagnostics.Process`.

**Why no DI container:** The tool has a flat dependency graph with only two abstractions. A container adds startup overhead and ceremony for no real benefit. Manual injection in `Program.cs` makes the wiring explicit and traceable.

**Test impact:** Unit tests use `NSubstitute` to mock these interfaces, making them fully deterministic with no disk or process dependencies.

---

## Path Handling Strategy

Path management is the trickiest part of the codebase, because three different systems use different path conventions:

| System | Path Style | Example |
|---|---|---|
| Git | Forward slashes, relative to repo root | `src/MyApp/Service.cs` |
| Cobertura | OS-dependent, often absolute | `/home/ci/src/MyApp/Service.cs` |
| .NET / Windows | Backslashes | `src\MyApp\Service.cs` |

**Strategy:**
1. All internal storage uses **forward slashes**
2. `Path.Combine` is used for filesystem operations (OS-correct separators)
3. `Replace('\\', '/')` normalizes before comparison
4. Coverage matching uses bidirectional suffix comparison
5. Tests use `Path.Combine` so they work on both Windows and Linux

---

## Tech Stack Rationale

| Dependency | Version | Why |
|---|---|---|
| **System.CommandLine** | 2.0.3 | The official .NET CLI framework. Stable release (not the pre-release API). |
| **Spectre.Console** | 0.54.0 | Rich terminal tables with zero effort. Cross-platform ANSI support. |
| **Microsoft.CodeAnalysis.CSharp** | 4.14.0 | Roslyn syntax trees for complexity and dependency analysis. No semantic model needed. |
| **xUnit** | 2.9.3 | Standard .NET test framework. |
| **FluentAssertions** | 8.8.0 | Readable assertions with good error messages. |
| **NSubstitute** | 5.3.0 | Clean mocking syntax without lambda ceremony. |

**What's NOT included and why:**

| Omitted | Reason |
|---|---|
| LibGit2Sharp | Adds native dependencies, complicates global tool packaging |
| DI container | Overkill for 2 interfaces |
| Roslyn `Compilation` / semantic model | Unnecessary for cyclomatic complexity; would require resolving all project references |
| `Microsoft.Build` / MSBuild API | Heavy dependency just to parse solution files |
| Configuration files (appsettings.json) | CLI options cover all use cases; no ambient config needed |

---

## System.CommandLine 2.0.3 API

The stable 2.0.3 release has a significantly different API from the widely-documented pre-release versions. Key patterns used:

```csharp
// Option definition
var opt = new Option<string>("--name") { Description = "...", Required = true };
var optWithDefault = new Option<int>("--top") { DefaultValueFactory = _ => 20 };

// Command with collection initializer
var command = new Command("analyze", "description") { opt1, opt2 };

// Action handler (replaces SetHandler)
command.SetAction(parseResult =>
{
    var value = parseResult.GetValue(opt);
    // ...
});

// Invocation (replaces InvokeAsync)
return rootCommand.Parse(args).Invoke();
```

This differs from the pre-release API (`AddOption`, `SetHandler` with delegates, `InvokeAsync`, `IsRequired`).

---

## Test Coverage

258 tests across 9 test classes:

| Test Class | Count | What's Tested |
|---|---|---|
| `SolutionParserTests` | 9 | .sln/.slnx parsing, folder filtering, path normalization, error cases |
| `GitChurnAnalyzerTests` | 10 | Numstat parsing, noise floor, normalization, exclusion globs, binary files, path handling |
| `CoverageParserTests` | 12 | Cobertura XML parsing, suffix matching, separator handling, malformed XML, multi-file merge |
| `ComplexityAnalyzerTests` | 8 | All branch types, multi-method, normalization, skipped files, project scoping, progress callback |
| `DependencyAnalyzerTests` | 39 | 6-signal coupling detection (infra, new, ctor params, static, async seam, casts), DI registration, normalization |
| `FileFilterHelperTests` | 9 | Glob pattern matching, default exclusions |
| `RiskScorerTests` | 10 | Zero cases, boundary classification, complexity amplification, score cap |
| `ReportRendererTests` | 41 | Baseline stats, JSON/CSV/HTML export, --format stdout, --verbose, --quiet, --explain annotations, complexity breakdown, footer legend, grouped/flat output |
| `AnalyzeCommandIntegrationTests` | 11 | End-to-end pipeline with real git repo, --format json/csv, --baseline, progress |

Unit tests mock `IFileSystem` and `IProcessRunner`, making them fast and deterministic. Integration tests create real temp git repos with `.slnx`, `.cs` files, git history, and Cobertura XML.

---

## Known Limitations

1. **No incremental analysis** -- Every run re-analyzes the full git history within the `--since` window. For very large repos, this could be slow. However, analyzers run in parallel to reduce wall-clock time.

2. **Single coverage file** -- The `analyze` command takes one Cobertura XML file. If you have multiple (e.g., per test project), merge them first with ReportGenerator, or use the `scan` command which merges automatically.

3. **C# only** -- Complexity analysis uses Roslyn's C# parser. F# and VB files are excluded from complexity scoring (though churn and coverage still apply).

4. **No caching** -- Results aren't cached between runs. A future version could cache git churn data keyed by HEAD commit.

5. **Glob matching is simple** -- The `*` pattern matches any characters including path separators, which differs from strict POSIX glob semantics where `*` doesn't cross directories. This was a deliberate choice to make patterns like `*Migrations/*.cs` match nested paths intuitively.
