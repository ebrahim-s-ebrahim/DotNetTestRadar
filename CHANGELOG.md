# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `--detailed` option for `scan` and `analyze` commands: expand top-ranked files with per-method coverage and complexity via Roslyn and Cobertura XML method-level parsing
- `--no-coverage` option for `scan` command: skip test execution and coverage collection to rank files by churn, complexity, and testability only — for codebases with zero tests
- `--coverage-tool` option for `scan` command: choose between `coverlet` (default) and `dotnet-coverage` as the coverage collector
- `--timeout` option for `scan` command: configurable time limit (default: 10 minutes) that kills the process tree if exceeded
- Live output streaming during `dotnet test`: verbose mode shows per-test results, default mode shows a spinner with the latest output line
- Troubleshooting section in README for coverlet hang issues
- `--verbose` flag showing detailed intermediate scores per file (churn norm, coverage, complexity norm, all 6 dependency signal counts)
- `--quiet` flag suppressing all output except errors and file export
- Signal 5: async seam call detection (`await httpClient.GetAsync()`, `SaveChangesAsync`, etc.) with ×1.5 weight
- Signal 6: concrete downcast detection (`(ConcreteType)expr`, `expr as ConcreteType`) with ×1.0 weight
- DI registration file heuristic: `Program.cs`, `Startup.cs`, and files with `AddScoped`/`AddSingleton`/`AddTransient` calls get zeroed dependency scores
- Progress bars during analysis using `AnsiConsole.Progress()` for complexity and dependency analyzers
- `--format json|csv|html|table` option for structured stdout output, enabling piping to tools like `jq`
- HTML report output: self-contained `.html` file with sortable table, color-coded risk/priority levels, and baseline delta support — via `--output report.html` or `--format html`
- `--baseline <path.json>` option for comparing against a previous analysis run (Delta column, summary line)
- Integration tests exercising the full `analyze` pipeline with a real temp git repo
- Pipeline parallelism: churn, complexity, and dependency analyzers now run concurrently via `Task.WhenAll`
- Self-analysis dogfooding step in CI workflow — runs `dotnet-litmus analyze` against itself on every push
- This CHANGELOG file

### Fixed

- Pipe buffer deadlock in `ProcessRunner` when `dotnet test` produces large stdout/stderr output (read both streams asynchronously)
- `scan` command no longer hangs indefinitely when coverlet data collector outlives the `dotnet test` process (uses non-draining `WaitForExit` overload)

### Changed

- `scan` error message when no coverage files are generated now suggests `--no-coverage` for codebases without tests
- Pinned all dependency versions to exact resolved versions (no more wildcard `*` ranges)

## [0.1.0] - 2026-02-27

### Added

- `analyze` command: cross-references git churn, code coverage, cyclomatic complexity, and dependency entanglement to produce Risk Score and Starting Priority
- `scan` command: runs `dotnet test`, collects coverage automatically, merges multi-project results, then runs the full analysis pipeline
- Phase 2 dependency/seam analysis: detects unseamed infrastructure calls, direct instantiations, concrete constructor params, and static calls on non-utility types
- `--solution`, `--coverage`, `--since`, `--top`, `--exclude`, `--output`, `--no-color` CLI options
- `--tests-dir` option for `scan` command
- JSON and CSV file export via `--output`
- Color-coded Spectre.Console table output with per-cell styling for priority, risk, and dependency levels
- Default exclusion patterns for auto-generated files (Designer.cs, Migrations, etc.)
- Support for both `.sln` and `.slnx` solution formats
- MinVer-based semantic versioning from git tags
- NuGet global tool packaging

### Fixed

- Test projects are now filtered out from solution parsing results
- File exclusion patterns applied consistently across all analyzers
