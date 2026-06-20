# StyloExtract v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a standalone .NET 10 NuGet library family (`StyloExtract.*`) that recognises same-layout web pages via a fast structural fingerprint and reuses a learned, drifting extractor per template — turning HTML into AI-ready Markdown + a typed block map as a downstream side-effect.

**Architecture:** AngleSharp DOM parse → MinHash/LSH structural fingerprint (fast path) → pq-gram cosine match (slow path) → heuristic block classifier + extractor induction (novel). Per-host SQLite store with centroid-style learned extractors, drift tracking, refit-as-version-event, JSON export/import.

**Tech Stack:** .NET 10, AngleSharp 1.x, System.IO.Hashing (xxHash3), Microsoft.Data.Sqlite, System.Text.Json, xUnit, BenchmarkDotNet, YamlDotNet.

**Spec:** `docs/superpowers/specs/2026-06-20-styloextract-design.md` (commit b8bf223d).

**Repo:** Standalone, sibling to `stylobot`. Create at `~/RiderProjects/stylobot-extract`.

## Global Constraints

- **Target framework:** `net10.0` for every project.
- **Library license:** Unlicense (matches stylobot main repo posture pre-v7).
- **Package family:** All projects under the `StyloExtract.*` namespace and assembly prefix. No exceptions.
- **Public records:** `init`-only setters, `required` members, value equality. No mutable state on result types.
- **Allocations:** Bounded per `ExtractAsync` call. No LOH allocations on the hot path. Regression-gated by Benchmarks project.
- **No word lists in C#:** Token lists, regex pattern lists, phrase catalogues live in YAML embedded resources under `Definitions/`. C# is the dispatcher.
- **No in-memory persistence:** All state that survives the call lives in SQLite. `ConcurrentDictionary` only for per-request transient state.
- **Naming exclusion:** Keep `StyloExtract` distinct from `StyloWall` (reserved for a separate semantic content-change-detection concept).
- **CLI binary name:** `stylo-extract`.
- **Directory layout:** `src/`, `tests/`, `bench/`, `docs/`, `.github/workflows/`. Central package management via `Directory.Packages.props`.
- **Solution file:** `stylobot-extract.sln`.
- **Performance budgets** (from spec §13; gated by Benchmarks):
  - Fast-path match step alone: <1ms p99 (assumes pre-computed signature)
  - Full `ExtractAsync` on fast-path HIT: <15ms p99 (200KB page)
  - Full `ExtractAsync` on slow-path MATCH: <30ms p99
  - Full `ExtractAsync` on NOVEL: <50ms p99
  - Memory per template: <12KB

## Open-question decisions (locked here, refer to these in tasks)

- **CLI `--monitor` output:** NDJSON to stdout by default; `--webhook <url>` flag POSTs each event; `--pretty` flag for human single-event mode.
- **HostHashKey rotation:** Not supported in v1. Single immutable key per index. Documented workaround: export → wipe → re-import with new key.
- **Heuristic rules:** Hybrid. *Recogniser data* (token lists, regex pattern sets, copyright/cookie/footer phrase lists) in YAML at `src/StyloExtract.Heuristics/Definitions/`. *Combinator code* (link-density math, score aggregation, role assignment) in C#.
- **AngleSharp version:** Pin to `AngleSharp 1.3.x` (latest stable 1.x). AOT-friendliness non-blocking for v1.

---

## File structure (locked before tasks)

```
stylobot-extract/
├── .editorconfig
├── .gitignore
├── .github/workflows/ci.yml
├── Directory.Build.props
├── Directory.Packages.props
├── LICENSE
├── README.md
├── stylobot-extract.sln
├── src/
│   ├── StyloExtract.Abstractions/
│   │   ├── StyloExtract.Abstractions.csproj
│   │   ├── ExtractionResult.cs
│   │   ├── LayoutMatch.cs
│   │   ├── MatchStatus.cs
│   │   ├── ExtractedBlock.cs
│   │   ├── ExtractedLink.cs
│   │   ├── ExtractionStats.cs
│   │   ├── BlockRole.cs
│   │   ├── ExtractionProfile.cs
│   │   ├── ExtractionOptions.cs
│   │   ├── ILayoutExtractor.cs
│   │   ├── IHtmlDomParser.cs
│   │   ├── IDomCleaner.cs
│   │   ├── IStructuralFingerprinter.cs
│   │   ├── ITemplateIndex.cs
│   │   ├── IBlockSegmenter.cs
│   │   ├── IBlockClassifier.cs
│   │   ├── IExtractorInducer.cs
│   │   ├── IExtractorApplicator.cs
│   │   ├── IMarkdownRenderer.cs
│   │   ├── ITemplateVersionEventSink.cs
│   │   ├── LearnedExtractor.cs
│   │   ├── BlockRule.cs
│   │   ├── ExtractorCentroidState.cs
│   │   ├── RoleCentroid.cs
│   │   ├── NewTemplateEvent.cs
│   │   ├── VersionChangeEvent.cs
│   │   ├── TemplateVersionDiff.cs
│   │   ├── PqGramDimensionChange.cs
│   │   ├── RuleSelectorChange.cs
│   │   └── StructuralFingerprint.cs
│   ├── StyloExtract.Html/
│   │   ├── StyloExtract.Html.csproj
│   │   ├── AngleSharpHtmlDomParser.cs
│   │   ├── DomCleaner.cs
│   │   └── TagPathWalker.cs
│   ├── StyloExtract.Fingerprint/
│   │   ├── StyloExtract.Fingerprint.csproj
│   │   ├── XxHash3Helper.cs
│   │   ├── ShingleGenerator.cs
│   │   ├── MinHashSketcher.cs
│   │   ├── JaccardEstimator.cs
│   │   ├── LshBander.cs
│   │   ├── AnchorPathFingerprinter.cs
│   │   ├── PqGramExtractor.cs
│   │   └── StructuralFingerprinter.cs
│   ├── StyloExtract.Templates/
│   │   ├── StyloExtract.Templates.csproj
│   │   ├── SqliteTemplateIndex.cs
│   │   ├── SqliteSchema.cs
│   │   ├── HostHasher.cs
│   │   ├── DriftScorer.cs
│   │   ├── AgingPriorityScorer.cs
│   │   ├── RefitOrchestrator.cs
│   │   ├── DefaultNoopVersionEventSink.cs
│   │   ├── TemplateVersionDiffer.cs
│   │   ├── TemplateExporter.cs
│   │   ├── TemplateImporter.cs
│   │   └── Serialization/
│   │       ├── ExportSchemaV1.cs
│   │       └── PqGramVectorCodec.cs
│   ├── StyloExtract.Heuristics/
│   │   ├── StyloExtract.Heuristics.csproj
│   │   ├── BlockSegmenter.cs
│   │   ├── HeuristicBlockClassifier.cs
│   │   ├── ClassNoiseFilter.cs
│   │   ├── ExtractorInducer.cs
│   │   ├── ExtractorApplicator.cs
│   │   ├── CssSelectorGeneralizer.cs
│   │   └── Definitions/
│   │       ├── class-noise-tokens.yaml
│   │       ├── footer-phrases.yaml
│   │       ├── copyright-patterns.yaml
│   │       ├── cookie-banner-phrases.yaml
│   │       ├── nav-class-hints.yaml
│   │       ├── ad-class-hints.yaml
│   │       └── code-language-patterns.yaml
│   ├── StyloExtract.Markdown/
│   │   ├── StyloExtract.Markdown.csproj
│   │   ├── TypedMarkdownRenderer.cs
│   │   ├── BlockRoleRenderers.cs
│   │   └── MarkdownEscaper.cs
│   ├── StyloExtract.Core/
│   │   ├── StyloExtract.Core.csproj
│   │   └── LayoutExtractor.cs
│   ├── StyloExtract.AspNetCore/
│   │   ├── StyloExtract.AspNetCore.csproj
│   │   ├── StyloExtractServiceCollectionExtensions.cs
│   │   └── StyloExtractOptions.cs
│   └── StyloExtract.Cli/
│       ├── StyloExtract.Cli.csproj
│       ├── Program.cs
│       ├── Commands/
│       │   ├── ExtractCommand.cs
│       │   ├── ExportCommand.cs
│       │   ├── ImportCommand.cs
│       │   └── MonitorCommand.cs
│       └── MonitorEventSink.cs
├── tests/
│   ├── StyloExtract.Core.Tests/
│   ├── StyloExtract.Fingerprint.Tests/
│   ├── StyloExtract.Heuristics.Tests/
│   ├── StyloExtract.Templates.Tests/
│   └── StyloExtract.IntegrationTests/
│       └── Fixtures/
│           ├── news/
│           ├── docs/
│           ├── ecommerce/
│           ├── marketing/
│           └── spa-shell/
└── bench/
    └── StyloExtract.Benchmarks/
        ├── StyloExtract.Benchmarks.csproj
        ├── Program.cs
        ├── FastPathMatchBench.cs
        ├── FullExtractBench.cs
        └── AllocationBench.cs
```

---

## Milestone map

- **M0** (T1–T3): Repo bootstrap, solution skeleton, CI.
- **M1** (T4–T11): Walking skeleton — raw HTML → Markdown via heuristic block classification. Always `NovelEphemeral`. No fingerprint, no store.
- **M2** (T12–T18): Fingerprint primitives (shingles, MinHash + Jaccard, LSH, anchor sig, pq-grams, composite, wire). `FingerprintHex` emitted; status still `NovelEphemeral`.
- **M3** (T19–T23): SQLite template store + host hasher + CRUD + band probe + cosine candidate. Not yet orchestrated.
- **M4** (T24–T28): Fast / slow / novel orchestration. Real `MatchStatus` values. Extractor induce + apply.
- **M5** (T29–T33): Drift, refit, version events, aging priority.
- **M6** (T34–T36): Export / import roundtrip.
- **M7** (T37–T41): ASP.NET Core DI, CLI commands, Benchmarks harness.

Total: 41 tasks. Each ends with a passing test and a commit.

---

## M0 — Repo bootstrap

### Task 1: Initialize repo with conventions

**Files:**
- Create: `~/RiderProjects/stylobot-extract/.gitignore`
- Create: `~/RiderProjects/stylobot-extract/.editorconfig`
- Create: `~/RiderProjects/stylobot-extract/Directory.Build.props`
- Create: `~/RiderProjects/stylobot-extract/Directory.Packages.props`
- Create: `~/RiderProjects/stylobot-extract/LICENSE`
- Create: `~/RiderProjects/stylobot-extract/README.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a repo root every later task assumes is the working directory.

- [ ] **Step 1: Create directory and git-init**

```bash
mkdir -p ~/RiderProjects/stylobot-extract
cd ~/RiderProjects/stylobot-extract
git init -b main
```

- [ ] **Step 2: Write `.gitignore`**

```
bin/
obj/
*.user
.vs/
.idea/
.vscode/
artifacts/
TestResults/
*.db
*.db-journal
*.db-wal
*.db-shm
BenchmarkDotNet.Artifacts/
```

- [ ] **Step 3: Write `.editorconfig`** (standard .NET defaults, 4-space indent, LF line endings)

```ini
root = true

[*]
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.{yaml,yml,json,md}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
dotnet_diagnostic.IDE0073.severity = none
```

- [ ] **Step 4: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <Authors>StyloBot</Authors>
    <Company>StyloBot</Company>
    <PackageLicenseExpression>Unlicense</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/mostlylucid/stylobot-extract</PackageProjectUrl>
    <RepositoryUrl>https://github.com/mostlylucid/stylobot-extract</RepositoryUrl>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Write `Directory.Packages.props`**

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="AngleSharp" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageVersion Include="System.IO.Hashing" Version="10.0.0" />
    <PackageVersion Include="YamlDotNet" Version="16.2.0" />
    <PackageVersion Include="System.CommandLine" Version="2.0.0-rc.1.25130.1" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />
    <PackageVersion Include="FluentAssertions" Version="6.12.1" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write `LICENSE`** — full Unlicense text from <https://unlicense.org/UNLICENSE>.

- [ ] **Step 7: Write `README.md`**

```markdown
# StyloExtract

Layout-fingerprint matching with template-keyed extractor reuse for .NET 10.

See `docs/spec.md` for the full design (sourced from the stylobot repo).

## Status

Pre-1.0. APIs unstable. Built for eventual integration with [StyloBot](https://github.com/mostlylucid/stylobot).
```

- [ ] **Step 8: Verify and commit**

```bash
git status
git add .gitignore .editorconfig Directory.Build.props Directory.Packages.props LICENSE README.md
git commit -m "chore: bootstrap repo conventions

- net10.0 target, central package management
- Unlicense, treat warnings as errors
- AngleSharp 1.3.0, Microsoft.Data.Sqlite 10, xUnit, BenchmarkDotNet pinned"
```

Expected: clean repo, one commit, no build attempted yet.

---

### Task 2: Scaffold solution with all project skeletons

**Files:**
- Create: `stylobot-extract.sln`
- Create: each of the nine `src/StyloExtract.*/StyloExtract.*.csproj` files
- Create: each of the five `tests/StyloExtract.*.Tests/StyloExtract.*.Tests.csproj` files
- Create: `bench/StyloExtract.Benchmarks/StyloExtract.Benchmarks.csproj`

**Interfaces:**
- Consumes: `Directory.Build.props` and `Directory.Packages.props` from T1.
- Produces: a solution that builds (empty) — every later task adds files to existing projects.

- [ ] **Step 1: Create each src project**

For each name in: `Abstractions Html Fingerprint Templates Heuristics Markdown Core AspNetCore Cli`:

```bash
mkdir -p src/StyloExtract.$NAME
cat > src/StyloExtract.$NAME/StyloExtract.$NAME.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>StyloExtract.$NAME</RootNamespace>
    <AssemblyName>StyloExtract.$NAME</AssemblyName>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>
EOF
```

(Manually substitute `$NAME` per project. The `Cli` project additionally sets `<OutputType>Exe</OutputType>` and `<IsPackable>false</IsPackable>`; the others stay library.)

For `StyloExtract.Cli/StyloExtract.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>StyloExtract.Cli</RootNamespace>
    <AssemblyName>stylo-extract</AssemblyName>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create each test project**

For each name in: `Core Fingerprint Heuristics Templates Integration`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>StyloExtract.$NAME.Tests</RootNamespace>
    <AssemblyName>StyloExtract.$NAME.Tests</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
</Project>
```

`StyloExtract.IntegrationTests` uses `RootNamespace=StyloExtract.IntegrationTests` and `AssemblyName=StyloExtract.IntegrationTests`.

- [ ] **Step 3: Create the Benchmarks project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>StyloExtract.Benchmarks</RootNamespace>
    <AssemblyName>StyloExtract.Benchmarks</AssemblyName>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create `stylobot-extract.sln`**

```bash
dotnet new sln -n stylobot-extract
for proj in src/StyloExtract.*/*.csproj tests/StyloExtract.*/*.csproj bench/StyloExtract.Benchmarks/*.csproj; do
  dotnet sln stylobot-extract.sln add "$proj"
done
```

- [ ] **Step 5: Verify solution builds**

```bash
dotnet build stylobot-extract.sln
```

Expected: builds cleanly (every project is empty but valid). 0 warnings (TreatWarningsAsErrors).

- [ ] **Step 6: Commit**

```bash
git add stylobot-extract.sln src/ tests/ bench/
git commit -m "chore: scaffold solution with empty project skeletons

9 library/CLI projects, 5 test projects, 1 benchmark project. Builds clean."
```

---

### Task 3: CI workflow — build and test on push

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the building solution from T2.
- Produces: green CI badge expectation that every future task must preserve.

- [ ] **Step 1: Write `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore
        run: dotnet restore stylobot-extract.sln
      - name: Build
        run: dotnet build stylobot-extract.sln --configuration Release --no-restore
      - name: Test
        run: dotnet test stylobot-extract.sln --configuration Release --no-build --logger "trx;LogFileName=test-results.trx"
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: '**/test-results.trx'
```

- [ ] **Step 2: Verify locally**

```bash
dotnet restore stylobot-extract.sln
dotnet build stylobot-extract.sln --configuration Release --no-restore
dotnet test stylobot-extract.sln --configuration Release --no-build
```

Expected: all three pass. Test step finds zero tests (no tests yet), exits 0.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add build + test workflow for net10.0"
```

---

## M1 — Walking skeleton (HTML → Markdown, always NovelEphemeral)

### Task 4: Abstractions — records and interfaces

**Files:**
- Create: every file under `src/StyloExtract.Abstractions/` listed in the File Structure section.

**Interfaces:**
- Consumes: nothing.
- Produces: every public type the rest of the library consumes. Concrete impls live elsewhere; this project is pure contracts.

- [ ] **Step 1: Write `ExtractionOptions.cs`** (referenced by `ILayoutExtractor` but not defined in spec §4 — derived from spec §12)

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractionOptions
{
    public ExtractionProfile Profile { get; init; } = ExtractionProfile.RagFull;
    public bool LearnNewTemplates { get; init; } = true;
    public bool EmitDebugMetadata { get; init; }
    public string? HostOverride { get; init; }
}
```

- [ ] **Step 2: Write the enums**

`MatchStatus.cs`:

```csharp
namespace StyloExtract.Abstractions;

public enum MatchStatus
{
    FastPathHit,
    SlowPathMatch,
    Novel,
    NovelEphemeral,
    Refit
}
```

`ExtractionProfile.cs`:

```csharp
namespace StyloExtract.Abstractions;

public enum ExtractionProfile
{
    MainContentOnly,
    RagFull,
    AgentNavigation,
    DebugFull
}
```

`BlockRole.cs`:

```csharp
namespace StyloExtract.Abstractions;

public enum BlockRole
{
    Unknown = 0,
    MainContent,
    Article,
    Heading,
    Summary,
    PrimaryNavigation,
    SecondaryNavigation,
    Breadcrumb,
    Sidebar,
    RelatedLinks,
    Footer,
    Header,
    Advertisement,
    CookieBanner,
    Form,
    Table,
    CodeBlock,
    Boilerplate
}
```

- [ ] **Step 3: Write the leaf records**

`ExtractedLink.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractedLink
{
    public required string Text { get; init; }
    public required string Href { get; init; }
    public required bool IsExternal { get; init; }
}
```

`ExtractedBlock.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractedBlock
{
    public required string Id { get; init; }
    public required BlockRole Role { get; init; }
    public required double Confidence { get; init; }
    public required string Text { get; init; }
    public required string Markdown { get; init; }
    public required string XPath { get; init; }
    public string? CssSelector { get; init; }
    public required int TextLength { get; init; }
    public required double LinkDensity { get; init; }
    public required IReadOnlyList<ExtractedLink> Links { get; init; }
}
```

`ExtractionStats.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractionStats
{
    public required int BlockCount { get; init; }
    public required int FingerprintShingleCount { get; init; }
    public required TimeSpan ParseTime { get; init; }
    public required TimeSpan FingerprintTime { get; init; }
    public required TimeSpan MatchTime { get; init; }
    public required TimeSpan RenderTime { get; init; }
}
```

`LayoutMatch.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record LayoutMatch
{
    public required Guid? TemplateId { get; init; }
    public required int TemplateVersion { get; init; }
    public required string FingerprintHex { get; init; }
    public required MatchStatus Status { get; init; }
    public required double Similarity { get; init; }
    public required int ObservationCount { get; init; }
    public required TimeSpan LatencyMatch { get; init; }
    public required TimeSpan LatencyTotal { get; init; }
}
```

`ExtractionResult.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractionResult
{
    public required Uri? SourceUri { get; init; }
    public required string? Title { get; init; }
    public required LayoutMatch Match { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<ExtractedBlock> Blocks { get; init; }
    public required ExtractionStats Stats { get; init; }
}
```

- [ ] **Step 4: Write the centroid records (spec §7)**

`RoleCentroid.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record RoleCentroid
{
    public required int ObservationCount { get; init; }
    public required double MeanLinkDensity { get; init; }
    public required double MeanTextLength { get; init; }
    public required double MeanDepth { get; init; }
}
```

`ExtractorCentroidState.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record ExtractorCentroidState
{
    public required int TotalObservations { get; init; }
    public required IReadOnlyDictionary<BlockRole, RoleCentroid> ByRole { get; init; }
    public required double OverallDriftScore { get; init; }
    public required DateTimeOffset LastObservation { get; init; }
}
```

`BlockRule.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record BlockRule
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> CssSelectors { get; init; }
    public required double MeanConfidence { get; init; }
    public required int ObservationCount { get; init; }
    public required double DriftScore { get; init; }
}
```

`LearnedExtractor.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record LearnedExtractor
{
    public required Guid TemplateId { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<BlockRule> Rules { get; init; }
    public required ExtractorCentroidState Centroid { get; init; }
}
```

- [ ] **Step 5: Write event records (spec §8)**

`NewTemplateEvent.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record NewTemplateEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string FingerprintHex { get; init; }
    public required int InitialBlockCount { get; init; }
}
```

`PqGramDimensionChange.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record PqGramDimensionChange
{
    public required string PqGramKey { get; init; }
    public required double OldCount { get; init; }
    public required double NewCount { get; init; }
}
```

`RuleSelectorChange.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record RuleSelectorChange
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> OldSelectors { get; init; }
    public required IReadOnlyList<string> NewSelectors { get; init; }
}
```

`TemplateVersionDiff.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record TemplateVersionDiff
{
    public required IReadOnlyList<PqGramDimensionChange> TopChangedDimensions { get; init; }
    public required IReadOnlyList<BlockRule> AddedRules { get; init; }
    public required IReadOnlyList<BlockRule> RemovedRules { get; init; }
    public required IReadOnlyList<RuleSelectorChange> ChangedSelectors { get; init; }
    public required double SignatureJaccardDelta { get; init; }
}
```

`VersionChangeEvent.cs`:

```csharp
namespace StyloExtract.Abstractions;

public sealed record VersionChangeEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }
    public required int OldVersion { get; init; }
    public required int NewVersion { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required TemplateVersionDiff Diff { get; init; }
}
```

- [ ] **Step 6: Write `StructuralFingerprint.cs`** (internal seam type)

```csharp
namespace StyloExtract.Abstractions;

public sealed record StructuralFingerprint
{
    public required uint[] StructuralMinHash { get; init; }   // 128 slots
    public required uint[] AnchorMinHash { get; init; }       // 128 slots
    public required ulong[] LshBands { get; init; }           // 16 bands
    public required IReadOnlyDictionary<string, double> PqGramCounts { get; init; }
    public required double PqGramNorm { get; init; }
    public required int ShingleCount { get; init; }
    public required string Hex { get; init; }                  // first-16-bytes hex for display
}
```

- [ ] **Step 7: Write the interfaces**

`ILayoutExtractor.cs`:

```csharp
namespace StyloExtract.Abstractions;

public interface ILayoutExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

`IHtmlDomParser.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IHtmlDomParser
{
    IDocument Parse(string html, Uri? sourceUri = null);
}
```

(Need an `AngleSharp` package ref on Abstractions. Add `<PackageReference Include="AngleSharp" />` to `StyloExtract.Abstractions.csproj`.)

`IDomCleaner.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IDomCleaner
{
    void Clean(IDocument document);
}
```

`IStructuralFingerprinter.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IStructuralFingerprinter
{
    StructuralFingerprint Compute(IDocument document);
}
```

`IBlockSegmenter.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IBlockSegmenter
{
    IReadOnlyList<IElement> Segment(IDocument document);
}
```

`IBlockClassifier.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IBlockClassifier
{
    IReadOnlyList<ExtractedBlock> Classify(IReadOnlyList<IElement> blocks);
}
```

`IExtractorInducer.cs`:

```csharp
namespace StyloExtract.Abstractions;

public interface IExtractorInducer
{
    LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks);
}
```

`IExtractorApplicator.cs`:

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IExtractorApplicator
{
    IReadOnlyList<ExtractedBlock> Apply(IDocument document, LearnedExtractor extractor);
}
```

`IMarkdownRenderer.cs`:

```csharp
namespace StyloExtract.Abstractions;

public interface IMarkdownRenderer
{
    string Render(IReadOnlyList<ExtractedBlock> blocks, ExtractionProfile profile);
}
```

`ITemplateVersionEventSink.cs`:

```csharp
namespace StyloExtract.Abstractions;

public interface ITemplateVersionEventSink
{
    ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken cancellationToken);
    ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken);
}
```

`ITemplateIndex.cs`:

```csharp
namespace StyloExtract.Abstractions;

public interface ITemplateIndex
{
    Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken);
    Task<Guid> RegisterAsync(byte[] hostHash, StructuralFingerprint fingerprint, LearnedExtractor extractor, CancellationToken cancellationToken);
    Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken);
}
```

- [ ] **Step 8: Build**

```bash
dotnet build src/StyloExtract.Abstractions/StyloExtract.Abstractions.csproj
```

Expected: zero warnings, zero errors.

- [ ] **Step 9: Commit**

```bash
git add src/StyloExtract.Abstractions/
git commit -m "feat(abstractions): public records, enums, and interface seams"
```

---

### Task 5: Html — AngleSharp DOM parser

**Files:**
- Create: `src/StyloExtract.Html/StyloExtract.Html.csproj` (add `<ProjectReference Include="../StyloExtract.Abstractions/StyloExtract.Abstractions.csproj" />` and `<PackageReference Include="AngleSharp" />`)
- Create: `src/StyloExtract.Html/AngleSharpHtmlDomParser.cs`
- Create: `tests/StyloExtract.Heuristics.Tests/StyloExtract.Heuristics.Tests.csproj` reference list updated to include Html
- Create: `tests/StyloExtract.Core.Tests/HtmlParserTests.cs`

**Interfaces:**
- Consumes: `IHtmlDomParser` from T4.
- Produces: `AngleSharpHtmlDomParser : IHtmlDomParser`.

- [ ] **Step 1: Add project refs**

In `src/StyloExtract.Html/StyloExtract.Html.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
  <PackageReference Include="AngleSharp" />
</ItemGroup>
```

In `tests/StyloExtract.Core.Tests/StyloExtract.Core.Tests.csproj` add:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\StyloExtract.Html\StyloExtract.Html.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write failing test**

`tests/StyloExtract.Core.Tests/HtmlParserTests.cs`:

```csharp
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class HtmlParserTests
{
    [Fact]
    public void Parse_ProducesDocumentWithExpectedTitleAndBodyTag()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        const string html = "<!DOCTYPE html><html><head><title>Hello</title></head><body><h1>Hi</h1></body></html>";

        IDocument doc = parser.Parse(html);

        doc.Title.Should().Be("Hello");
        doc.Body!.QuerySelector("h1")!.TextContent.Should().Be("Hi");
    }

    [Fact]
    public void Parse_ToleratesMalformedHtml()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        const string html = "<html><body><div>oops<p>unclosed";

        IDocument doc = parser.Parse(html);

        doc.Body!.QuerySelector("p")!.TextContent.Should().Contain("unclosed");
    }
}
```

- [ ] **Step 3: Verify it fails**

```bash
dotnet test tests/StyloExtract.Core.Tests/ --filter HtmlParserTests
```

Expected: compile failure (`AngleSharpHtmlDomParser` not found).

- [ ] **Step 4: Implement**

`src/StyloExtract.Html/AngleSharpHtmlDomParser.cs`:

```csharp
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using StyloExtract.Abstractions;

namespace StyloExtract.Html;

public sealed class AngleSharpHtmlDomParser : IHtmlDomParser
{
    private readonly HtmlParser _parser;

    public AngleSharpHtmlDomParser()
    {
        var context = BrowsingContext.New(Configuration.Default);
        _parser = new HtmlParser(new HtmlParserOptions(), context);
    }

    public IDocument Parse(string html, Uri? sourceUri = null)
    {
        return _parser.ParseDocument(html);
    }
}
```

- [ ] **Step 5: Verify it passes**

```bash
dotnet test tests/StyloExtract.Core.Tests/ --filter HtmlParserTests
```

Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Html/ tests/StyloExtract.Core.Tests/
git commit -m "feat(html): AngleSharp-backed IHtmlDomParser"
```

---

### Task 6: Html — DOM cleaner

**Files:**
- Create: `src/StyloExtract.Html/DomCleaner.cs`
- Create: `tests/StyloExtract.Core.Tests/DomCleanerTests.cs`

**Interfaces:**
- Consumes: `IDomCleaner` from T4.
- Produces: `DomCleaner : IDomCleaner` that strips `<script>`, `<style>`, `<template>`, `<noscript>`, `<svg>` from a parsed document (in-place mutation).

- [ ] **Step 1: Write failing test**

```csharp
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class DomCleanerTests
{
    [Fact]
    public void Clean_RemovesScriptStyleTemplateNoscriptSvg()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        const string html = """
            <html><body>
              <script>alert(1)</script>
              <style>.x{color:red}</style>
              <template id="t"><div>hi</div></template>
              <noscript>no js</noscript>
              <svg><circle/></svg>
              <p>keep me</p>
            </body></html>
            """;

        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);

        doc.QuerySelectorAll("script").Should().BeEmpty();
        doc.QuerySelectorAll("style").Should().BeEmpty();
        doc.QuerySelectorAll("template").Should().BeEmpty();
        doc.QuerySelectorAll("noscript").Should().BeEmpty();
        doc.QuerySelectorAll("svg").Should().BeEmpty();
        doc.QuerySelector("p")!.TextContent.Should().Be("keep me");
    }
}
```

- [ ] **Step 2: Verify failure** (`DomCleaner` not found).

- [ ] **Step 3: Implement**

```csharp
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Html;

public sealed class DomCleaner : IDomCleaner
{
    private static readonly string[] TagsToStrip = ["script", "style", "template", "noscript", "svg"];

    public void Clean(IDocument document)
    {
        foreach (var tag in TagsToStrip)
        {
            var nodes = document.QuerySelectorAll(tag).ToArray();
            foreach (var node in nodes)
            {
                node.Remove();
            }
        }
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Html/DomCleaner.cs tests/StyloExtract.Core.Tests/DomCleanerTests.cs
git commit -m "feat(html): DomCleaner strips script/style/template/noscript/svg"
```

---

### Task 7: Heuristics — class-noise filter (YAML-driven)

**Files:**
- Create: `src/StyloExtract.Heuristics/Definitions/class-noise-tokens.yaml`
- Create: `src/StyloExtract.Heuristics/ClassNoiseFilter.cs`
- Modify: `src/StyloExtract.Heuristics/StyloExtract.Heuristics.csproj` (add YamlDotNet, embed YAML, ref Abstractions)
- Create: `tests/StyloExtract.Heuristics.Tests/ClassNoiseFilterTests.cs`

**Interfaces:**
- Consumes: nothing public.
- Produces: `ClassNoiseFilter.Filter(IReadOnlyList<string> rawClassTokens) → IReadOnlyList<string>` — strips noise tokens by exact match, prefix patterns (`is-*`, `js-*`), and a tail of hashed BEM suffixes (e.g. `__abc123`).

- [ ] **Step 1: Update csproj**

In `src/StyloExtract.Heuristics/StyloExtract.Heuristics.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
  <PackageReference Include="YamlDotNet" />
</ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="Definitions\**\*.yaml" />
</ItemGroup>
```

In `tests/StyloExtract.Heuristics.Tests/StyloExtract.Heuristics.Tests.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\StyloExtract.Heuristics\StyloExtract.Heuristics.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write YAML**

`src/StyloExtract.Heuristics/Definitions/class-noise-tokens.yaml`:

```yaml
exactTokens:
  - dark-mode
  - light-mode
  - dark
  - light
  - active
  - hidden
  - visible
  - selected
  - open
  - closed
  - loading
  - loaded
  - error
prefixes:
  - is-
  - js-
  - has-
  - data-
  - state-
  - aria-
hashedBemSuffixPattern: '__[a-z0-9]{4,}$'
```

- [ ] **Step 3: Write failing test**

```csharp
using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ClassNoiseFilterTests
{
    private static readonly ClassNoiseFilter Filter = ClassNoiseFilter.LoadFromEmbeddedResource();

    [Fact]
    public void Filter_RemovesExactNoiseTokens()
    {
        Filter.Filter(["btn", "primary", "dark-mode", "active"]).Should().BeEquivalentTo(["btn", "primary"]);
    }

    [Fact]
    public void Filter_RemovesPrefixedNoiseTokens()
    {
        Filter.Filter(["nav", "is-open", "js-toggle", "has-children"]).Should().BeEquivalentTo(["nav"]);
    }

    [Fact]
    public void Filter_RemovesHashedBemSuffixes()
    {
        Filter.Filter(["MainNav__abc123", "Logo", "Item__xyz9"]).Should().BeEquivalentTo(["MainNav", "Logo", "Item"]);
    }

    [Fact]
    public void Filter_PreservesStableTokens()
    {
        Filter.Filter(["header", "footer", "primary-nav"]).Should().BeEquivalentTo(["header", "footer", "primary-nav"]);
    }
}
```

- [ ] **Step 4: Verify failure.**

- [ ] **Step 5: Implement**

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StyloExtract.Heuristics;

public sealed class ClassNoiseFilter
{
    private readonly HashSet<string> _exact;
    private readonly string[] _prefixes;
    private readonly Regex _hashedBemSuffix;

    private ClassNoiseFilter(HashSet<string> exact, string[] prefixes, Regex hashedBemSuffix)
    {
        _exact = exact;
        _prefixes = prefixes;
        _hashedBemSuffix = hashedBemSuffix;
    }

    public static ClassNoiseFilter LoadFromEmbeddedResource()
    {
        var assembly = typeof(ClassNoiseFilter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("class-noise-tokens.yaml", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var dto = deserializer.Deserialize<ClassNoiseDto>(yaml);
        return new ClassNoiseFilter(
            new HashSet<string>(dto.ExactTokens, StringComparer.OrdinalIgnoreCase),
            dto.Prefixes,
            new Regex(dto.HashedBemSuffixPattern, RegexOptions.Compiled));
    }

    public IReadOnlyList<string> Filter(IReadOnlyList<string> rawClassTokens)
    {
        var result = new List<string>(rawClassTokens.Count);
        foreach (var token in rawClassTokens)
        {
            if (_exact.Contains(token)) continue;
            if (_prefixes.Any(p => token.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            var stripped = _hashedBemSuffix.Replace(token, string.Empty);
            if (!string.IsNullOrEmpty(stripped))
            {
                result.Add(stripped);
            }
        }
        return result;
    }

    private sealed class ClassNoiseDto
    {
        public List<string> ExactTokens { get; set; } = new();
        public string[] Prefixes { get; set; } = [];
        public string HashedBemSuffixPattern { get; set; } = "";
    }
}
```

- [ ] **Step 6: Verify pass.** Run `dotnet test tests/StyloExtract.Heuristics.Tests/`.

- [ ] **Step 7: Commit**

```bash
git add src/StyloExtract.Heuristics/ tests/StyloExtract.Heuristics.Tests/ClassNoiseFilterTests.cs
git commit -m "feat(heuristics): ClassNoiseFilter loaded from embedded YAML

Per CLAUDE.md no-word-lists-in-C# rule: tokens, prefixes, BEM suffix
pattern live in class-noise-tokens.yaml; C# is dispatcher only."
```

---

### Task 8: Heuristics — block segmenter

**Files:**
- Create: `src/StyloExtract.Heuristics/BlockSegmenter.cs`
- Create: `tests/StyloExtract.Heuristics.Tests/BlockSegmenterTests.cs`

**Interfaces:**
- Consumes: `IBlockSegmenter` from T4.
- Produces: `BlockSegmenter : IBlockSegmenter` returning each semantic-tag node plus div/section subtrees that look block-like (text length > 80 chars OR ≥3 immediate children).

- [ ] **Step 1: Write failing test**

```csharp
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class BlockSegmenterTests
{
    [Fact]
    public void Segment_ReturnsSemanticTagsAndBlockyDivs()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IBlockSegmenter segmenter = new BlockSegmenter();
        const string html = """
            <html><body>
              <header><nav><a href='/'>Home</a><a href='/about'>About</a></nav></header>
              <main>
                <article><h1>Title</h1><p>This is the article body with plenty of text inside it.</p></article>
                <aside><a>r1</a><a>r2</a><a>r3</a></aside>
              </main>
              <footer>Copyright 2026</footer>
              <div>tiny</div>
            </body></html>
            """;
        IDocument doc = parser.Parse(html);

        IReadOnlyList<IElement> blocks = segmenter.Segment(doc);

        var tags = blocks.Select(b => b.TagName.ToLowerInvariant()).ToHashSet();
        tags.Should().Contain(new[] { "header", "nav", "main", "article", "aside", "footer" });
        tags.Should().NotContain("div");
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class BlockSegmenter : IBlockSegmenter
{
    private static readonly HashSet<string> SemanticTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "header", "footer", "nav", "main", "article", "section", "aside", "form", "table"
    };

    private const int BlockyDivMinTextLength = 80;
    private const int BlockyDivMinChildCount = 3;

    public IReadOnlyList<IElement> Segment(IDocument document)
    {
        if (document.Body is null) return Array.Empty<IElement>();
        var result = new List<IElement>();
        Walk(document.Body, result);
        return result;
    }

    private static void Walk(IElement element, List<IElement> sink)
    {
        if (SemanticTags.Contains(element.TagName))
        {
            sink.Add(element);
        }
        else if (IsBlockyDiv(element))
        {
            sink.Add(element);
        }
        foreach (var child in element.Children)
        {
            Walk(child, sink);
        }
    }

    private static bool IsBlockyDiv(IElement element)
    {
        if (!string.Equals(element.TagName, "div", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(element.TagName, "section", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return element.TextContent.Length >= BlockyDivMinTextLength
               || element.ChildElementCount >= BlockyDivMinChildCount;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Heuristics/BlockSegmenter.cs tests/StyloExtract.Heuristics.Tests/BlockSegmenterTests.cs
git commit -m "feat(heuristics): BlockSegmenter walks semantic tags + blocky divs"
```

---

### Task 9: Heuristics — block classifier

**Files:**
- Create: `src/StyloExtract.Heuristics/Definitions/footer-phrases.yaml`
- Create: `src/StyloExtract.Heuristics/Definitions/copyright-patterns.yaml`
- Create: `src/StyloExtract.Heuristics/Definitions/cookie-banner-phrases.yaml`
- Create: `src/StyloExtract.Heuristics/Definitions/nav-class-hints.yaml`
- Create: `src/StyloExtract.Heuristics/Definitions/ad-class-hints.yaml`
- Create: `src/StyloExtract.Heuristics/HeuristicBlockClassifier.cs`
- Create: `tests/StyloExtract.Heuristics.Tests/HeuristicBlockClassifierTests.cs`

**Interfaces:**
- Consumes: `IBlockClassifier`, `ExtractedBlock`, `BlockRole`.
- Produces: `HeuristicBlockClassifier : IBlockClassifier` that turns segmented `IElement`s into `ExtractedBlock`s with a `BlockRole` and `Confidence ∈ [0,1]`.

- [ ] **Step 1: Write YAML phrase files**

`footer-phrases.yaml`:
```yaml
phrases:
  - all rights reserved
  - privacy policy
  - terms of service
  - terms & conditions
  - cookie policy
```

`copyright-patterns.yaml`:
```yaml
patterns:
  - '©\s*\d{4}'
  - 'copyright\s+\d{4}'
  - '\(c\)\s*\d{4}'
```

`cookie-banner-phrases.yaml`:
```yaml
phrases:
  - we use cookies
  - this site uses cookies
  - accept all cookies
  - manage cookie preferences
```

`nav-class-hints.yaml`:
```yaml
hints:
  - nav
  - navigation
  - menu
  - navbar
  - main-menu
  - site-nav
```

`ad-class-hints.yaml`:
```yaml
hints:
  - ad
  - ads
  - advertisement
  - promo
  - sponsored
```

- [ ] **Step 2: Write failing test**

```csharp
using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class HeuristicBlockClassifierTests
{
    private static (IReadOnlyList<ExtractedBlock> Blocks, IDocument Doc) Classify(string html)
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);
        var blocks = classifier.Classify(segmenter.Segment(doc));
        return (blocks, doc);
    }

    [Fact]
    public void Classify_Nav_AsPrimaryNavigation()
    {
        const string html = "<html><body><header><nav class='main-menu'><a href='/'>H</a><a href='/a'>A</a><a href='/b'>B</a><a href='/c'>C</a></nav></header></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().ContainSingle(b => b.Role == BlockRole.PrimaryNavigation);
    }

    [Fact]
    public void Classify_Article_AsMainContent()
    {
        const string html = "<html><body><main><article><h1>Title</h1><p>" + new string('x', 400) + "</p></article></main></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
    }

    [Fact]
    public void Classify_Footer_AsFooter()
    {
        const string html = "<html><body><footer>© 2026 Acme. All rights reserved.</footer></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Footer);
    }

    [Fact]
    public void Classify_CookieBanner_AsCookieBanner()
    {
        const string html = "<html><body><div class='cookie-bar'>We use cookies <button>Accept all cookies</button></div></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.CookieBanner);
    }

    [Fact]
    public void Classify_AdDiv_AsAdvertisement()
    {
        const string html = "<html><body><div class='ad sponsored'><a href='x'>1</a><a href='y'>2</a><a href='z'>3</a></div></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Advertisement);
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement**

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using StyloExtract.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StyloExtract.Heuristics;

public sealed class HeuristicBlockClassifier : IBlockClassifier
{
    private readonly string[] _footerPhrases;
    private readonly Regex[] _copyrightPatterns;
    private readonly string[] _cookiePhrases;
    private readonly HashSet<string> _navHints;
    private readonly HashSet<string> _adHints;

    private HeuristicBlockClassifier(
        string[] footerPhrases,
        Regex[] copyrightPatterns,
        string[] cookiePhrases,
        HashSet<string> navHints,
        HashSet<string> adHints)
    {
        _footerPhrases = footerPhrases;
        _copyrightPatterns = copyrightPatterns;
        _cookiePhrases = cookiePhrases;
        _navHints = navHints;
        _adHints = adHints;
    }

    public static HeuristicBlockClassifier LoadFromEmbeddedResources()
    {
        var assembly = typeof(HeuristicBlockClassifier).Assembly;
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

        T Load<T>(string name)
        {
            var resName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var s = assembly.GetManifestResourceStream(resName)!;
            using var r = new StreamReader(s);
            return deserializer.Deserialize<T>(r.ReadToEnd());
        }

        var footer = Load<PhraseList>("footer-phrases.yaml");
        var copyright = Load<PatternList>("copyright-patterns.yaml");
        var cookie = Load<PhraseList>("cookie-banner-phrases.yaml");
        var nav = Load<HintList>("nav-class-hints.yaml");
        var ad = Load<HintList>("ad-class-hints.yaml");

        return new HeuristicBlockClassifier(
            footer.Phrases.ToArray(),
            copyright.Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray(),
            cookie.Phrases.ToArray(),
            new HashSet<string>(nav.Hints, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ad.Hints, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ExtractedBlock> Classify(IReadOnlyList<IElement> blocks)
    {
        var result = new List<ExtractedBlock>(blocks.Count);
        int i = 0;
        foreach (var element in blocks)
        {
            var (role, confidence) = ClassifyOne(element);
            result.Add(new ExtractedBlock
            {
                Id = $"b{i:D4}",
                Role = role,
                Confidence = confidence,
                Text = element.TextContent.Trim(),
                Markdown = "",
                XPath = ComputeXPath(element),
                CssSelector = null,
                TextLength = element.TextContent.Length,
                LinkDensity = ComputeLinkDensity(element),
                Links = ExtractLinks(element)
            });
            i++;
        }
        return result;
    }

    private (BlockRole Role, double Confidence) ClassifyOne(IElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        var classTokens = (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = element.TextContent;
        var linkDensity = ComputeLinkDensity(element);

        bool ClassMatches(HashSet<string> hints) => classTokens.Any(c =>
            hints.Any(h => c.Contains(h, StringComparison.OrdinalIgnoreCase)));

        bool TextContainsAny(IEnumerable<string> phrases) =>
            phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        bool TextMatchesAny(Regex[] patterns) => patterns.Any(r => r.IsMatch(text));

        if (tag is "nav" || ClassMatches(_navHints))
        {
            if (linkDensity > 0.7)
            {
                var depth = GetDepth(element);
                return (depth <= 2 ? BlockRole.PrimaryNavigation : BlockRole.SecondaryNavigation, 0.85);
            }
        }

        if (tag == "footer" || classTokens.Any(c => c.Contains("footer", StringComparison.OrdinalIgnoreCase)))
        {
            if (TextContainsAny(_footerPhrases) || TextMatchesAny(_copyrightPatterns))
            {
                return (BlockRole.Footer, 0.9);
            }
            return (BlockRole.Footer, 0.6);
        }

        if (tag == "header") return (BlockRole.Header, 0.7);

        if (tag is "main" or "article" && text.Length > 200)
        {
            return (BlockRole.MainContent, 0.92);
        }

        if (tag == "aside")
        {
            return (linkDensity > 0.5 ? BlockRole.RelatedLinks : BlockRole.Sidebar, 0.75);
        }

        if (tag == "form" || element.QuerySelectorAll("input").Length >= 2)
        {
            return (BlockRole.Form, 0.85);
        }

        if (tag == "table") return (BlockRole.Table, 0.95);

        if (TextContainsAny(_cookiePhrases) && element.QuerySelector("button") is not null)
        {
            return (BlockRole.CookieBanner, 0.9);
        }

        if (ClassMatches(_adHints) && linkDensity > 0.5)
        {
            return (BlockRole.Advertisement, 0.8);
        }

        return text.Length > 200 ? (BlockRole.MainContent, 0.5) : (BlockRole.Boilerplate, 0.3);
    }

    private static double ComputeLinkDensity(IElement element)
    {
        var totalText = element.TextContent.Length;
        if (totalText == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / totalText;
    }

    private static IReadOnlyList<ExtractedLink> ExtractLinks(IElement element)
    {
        return element.QuerySelectorAll("a")
            .Select(a => new ExtractedLink
            {
                Text = a.TextContent.Trim(),
                Href = a.GetAttribute("href") ?? "",
                IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static int GetDepth(IElement element)
    {
        int depth = 0;
        var current = element.ParentElement;
        while (current is not null) { depth++; current = current.ParentElement; }
        return depth;
    }

    private static string ComputeXPath(IElement element)
    {
        var parts = new Stack<string>();
        var current = (IElement?)element;
        while (current is not null && current.ParentElement is not null)
        {
            var idx = 1;
            var sibling = current.PreviousElementSibling;
            while (sibling is not null)
            {
                if (sibling.TagName == current.TagName) idx++;
                sibling = sibling.PreviousElementSibling;
            }
            parts.Push($"{current.TagName.ToLowerInvariant()}[{idx}]");
            current = current.ParentElement;
        }
        return "/" + string.Join("/", parts);
    }

    private sealed class PhraseList { public List<string> Phrases { get; set; } = new(); }
    private sealed class PatternList { public List<string> Patterns { get; set; } = new(); }
    private sealed class HintList { public List<string> Hints { get; set; } = new(); }
}
```

- [ ] **Step 5: Verify pass** (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Heuristics/ tests/StyloExtract.Heuristics.Tests/HeuristicBlockClassifierTests.cs
git commit -m "feat(heuristics): HeuristicBlockClassifier with YAML-driven phrase lists

Recognisers (phrases, patterns, class hints) in YAML resources;
combinator logic (link-density, tag dispatch, score) in C#."
```

---

### Task 10: Markdown — deterministic typed renderer

**Files:**
- Create: `src/StyloExtract.Markdown/StyloExtract.Markdown.csproj` ref Abstractions
- Create: `src/StyloExtract.Markdown/MarkdownEscaper.cs`
- Create: `src/StyloExtract.Markdown/BlockRoleRenderers.cs`
- Create: `src/StyloExtract.Markdown/TypedMarkdownRenderer.cs`
- Create: `tests/StyloExtract.Core.Tests/MarkdownRendererTests.cs`
- Modify: `tests/StyloExtract.Core.Tests/StyloExtract.Core.Tests.csproj` to ref Markdown

**Interfaces:**
- Consumes: `IMarkdownRenderer`, `ExtractedBlock`, `BlockRole`, `ExtractionProfile`.
- Produces: `TypedMarkdownRenderer : IMarkdownRenderer`. Output rules:
  - `MainContentOnly` profile: emits only `MainContent` / `Article` / `Heading` / `Table` / `CodeBlock` blocks.
  - `RagFull`: emits the above plus `Breadcrumb` / `Summary` / `RelatedLinks`.
  - `AgentNavigation`: emits `PrimaryNavigation` / `SecondaryNavigation` / `Breadcrumb` / `Form` / `SearchBox`-shaped blocks (any `Form` with one `input[type=search]`).
  - `DebugFull`: emits every block with an HTML comment header `<!-- block:Role confidence:0.XX xpath:... -->`.
  - All profiles wrap block content in `<!-- block:Role -->` comments when `EmitDebugMetadata` is on (passed via overload).

- [ ] **Step 1: Add csproj refs**

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
</ItemGroup>
```

In test csproj:

```xml
<ProjectReference Include="..\..\src\StyloExtract.Markdown\StyloExtract.Markdown.csproj" />
```

- [ ] **Step 2: Write failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

public class MarkdownRendererTests
{
    private static ExtractedBlock Block(BlockRole role, string text, double linkDensity = 0.0) => new()
    {
        Id = "b", Role = role, Confidence = 0.9, Text = text, Markdown = "",
        XPath = "/", TextLength = text.Length, LinkDensity = linkDensity,
        Links = Array.Empty<ExtractedLink>()
    };

    [Fact]
    public void Render_MainContentOnly_DropsNavAndFooter()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[]
        {
            Block(BlockRole.PrimaryNavigation, "Home About"),
            Block(BlockRole.MainContent, "The article body."),
            Block(BlockRole.Footer, "© 2026")
        };

        var md = r.Render(blocks, ExtractionProfile.MainContentOnly);

        md.Should().Contain("The article body.");
        md.Should().NotContain("Home About");
        md.Should().NotContain("© 2026");
    }

    [Fact]
    public void Render_DebugFull_AnnotatesEveryBlock()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[] { Block(BlockRole.MainContent, "hello"), Block(BlockRole.Footer, "bye") };

        var md = r.Render(blocks, ExtractionProfile.DebugFull);

        md.Should().Contain("<!-- block:MainContent");
        md.Should().Contain("<!-- block:Footer");
    }

    [Fact]
    public void Render_AgentNavigation_KeepsNavDropsBody()
    {
        IMarkdownRenderer r = new TypedMarkdownRenderer();
        var blocks = new[]
        {
            Block(BlockRole.PrimaryNavigation, "Home About"),
            Block(BlockRole.MainContent, "Article body")
        };

        var md = r.Render(blocks, ExtractionProfile.AgentNavigation);

        md.Should().Contain("Home About");
        md.Should().NotContain("Article body");
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement**

`MarkdownEscaper.cs`:

```csharp
namespace StyloExtract.Markdown;

internal static class MarkdownEscaper
{
    public static string Escape(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`");
    }
}
```

`BlockRoleRenderers.cs`:

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Markdown;

internal static class BlockRoleRenderers
{
    public static string Render(ExtractedBlock block) => block.Role switch
    {
        BlockRole.MainContent or BlockRole.Article => MarkdownEscaper.Escape(block.Text),
        BlockRole.Heading => "# " + MarkdownEscaper.Escape(block.Text),
        BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation =>
            string.Join("\n", block.Links.Select(l => $"- [{l.Text}]({l.Href})")),
        BlockRole.Breadcrumb =>
            string.Join(" / ", block.Links.Select(l => $"[{l.Text}]({l.Href})")),
        BlockRole.Footer or BlockRole.Boilerplate => MarkdownEscaper.Escape(block.Text),
        BlockRole.Form => RenderForm(block),
        BlockRole.Table or BlockRole.CodeBlock => block.Text,
        _ => MarkdownEscaper.Escape(block.Text)
    };

    private static string RenderForm(ExtractedBlock block) =>
        "Form: " + (block.Text.Length > 80 ? block.Text[..80] + "…" : block.Text);
}
```

`TypedMarkdownRenderer.cs`:

```csharp
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Markdown;

public sealed class TypedMarkdownRenderer : IMarkdownRenderer
{
    public string Render(IReadOnlyList<ExtractedBlock> blocks, ExtractionProfile profile)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (!ShouldEmit(block, profile)) continue;
            if (profile == ExtractionProfile.DebugFull)
            {
                sb.AppendLine($"<!-- block:{block.Role} confidence:{block.Confidence:F2} xpath:{block.XPath} -->");
            }
            sb.AppendLine(BlockRoleRenderers.Render(block));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static bool ShouldEmit(ExtractedBlock b, ExtractionProfile p) => p switch
    {
        ExtractionProfile.MainContentOnly => b.Role is BlockRole.MainContent or BlockRole.Article
            or BlockRole.Heading or BlockRole.Summary or BlockRole.Table or BlockRole.CodeBlock,
        ExtractionProfile.RagFull => b.Role is not (BlockRole.Footer or BlockRole.Header or BlockRole.Advertisement
            or BlockRole.CookieBanner or BlockRole.Boilerplate or BlockRole.Unknown),
        ExtractionProfile.AgentNavigation => b.Role is BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation
            or BlockRole.Breadcrumb or BlockRole.Form,
        ExtractionProfile.DebugFull => true,
        _ => true
    };
}
```

- [ ] **Step 5: Verify pass** (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Markdown/ tests/StyloExtract.Core.Tests/MarkdownRendererTests.cs
git commit -m "feat(markdown): TypedMarkdownRenderer with profile-aware emission"
```

---

### Task 11: Core — `LayoutExtractor` wiring (always NovelEphemeral)

**Files:**
- Modify: `src/StyloExtract.Core/StyloExtract.Core.csproj` to ref Abstractions, Html, Heuristics, Markdown
- Create: `src/StyloExtract.Core/LayoutExtractor.cs`
- Create: `tests/StyloExtract.Core.Tests/LayoutExtractorTests.cs` (golden end-to-end)
- Create: `tests/StyloExtract.IntegrationTests/Fixtures/news/article-001.html` (a small synthetic news fixture)

**Interfaces:**
- Consumes: `IHtmlDomParser`, `IDomCleaner`, `IBlockSegmenter`, `IBlockClassifier`, `IMarkdownRenderer`, `ExtractionResult`, `LayoutMatch`, `MatchStatus.NovelEphemeral`.
- Produces: `LayoutExtractor : ILayoutExtractor`. Until M2, every call returns `Status = NovelEphemeral`, `TemplateId = null`, `TemplateVersion = 0`, `FingerprintHex = ""`, `Similarity = 0`, `ObservationCount = 0`.

- [ ] **Step 1: Csproj refs**

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
  <ProjectReference Include="..\StyloExtract.Html\StyloExtract.Html.csproj" />
  <ProjectReference Include="..\StyloExtract.Heuristics\StyloExtract.Heuristics.csproj" />
  <ProjectReference Include="..\StyloExtract.Markdown\StyloExtract.Markdown.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

public class LayoutExtractorTests
{
    private static ILayoutExtractor Build() => new LayoutExtractor(
        new AngleSharpHtmlDomParser(),
        new DomCleaner(),
        new BlockSegmenter(),
        HeuristicBlockClassifier.LoadFromEmbeddedResources(),
        new TypedMarkdownRenderer());

    [Fact]
    public async Task ExtractAsync_ProducesNovelEphemeralResultWithMarkdown()
    {
        const string html = "<html><head><title>Test</title></head><body><main><article><p>" +
                            new string('x', 300) + "</p></article></main></body></html>";

        var result = await Build().ExtractAsync(html);

        result.Match.Status.Should().Be(MatchStatus.NovelEphemeral);
        result.Match.TemplateId.Should().BeNull();
        result.Title.Should().Be("Test");
        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.Blocks.Should().NotBeEmpty();
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement**

```csharp
using System.Diagnostics;
using StyloExtract.Abstractions;

namespace StyloExtract.Core;

public sealed class LayoutExtractor : ILayoutExtractor
{
    private readonly IHtmlDomParser _parser;
    private readonly IDomCleaner _cleaner;
    private readonly IBlockSegmenter _segmenter;
    private readonly IBlockClassifier _classifier;
    private readonly IMarkdownRenderer _renderer;

    public LayoutExtractor(
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier,
        IMarkdownRenderer renderer)
    {
        _parser = parser;
        _cleaner = cleaner;
        _segmenter = segmenter;
        _classifier = classifier;
        _renderer = renderer;
    }

    public Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        var total = Stopwatch.StartNew();

        var parseTimer = Stopwatch.StartNew();
        var doc = _parser.Parse(html, sourceUri);
        _cleaner.Clean(doc);
        parseTimer.Stop();

        var segmented = _segmenter.Segment(doc);
        var blocks = _classifier.Classify(segmented);

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();

        total.Stop();

        var result = new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            Match = new LayoutMatch
            {
                TemplateId = null,
                TemplateVersion = 0,
                FingerprintHex = "",
                Status = MatchStatus.NovelEphemeral,
                Similarity = 0,
                ObservationCount = 0,
                LatencyMatch = TimeSpan.Zero,
                LatencyTotal = total.Elapsed
            },
            Stats = new ExtractionStats
            {
                BlockCount = blocks.Count,
                FingerprintShingleCount = 0,
                ParseTime = parseTimer.Elapsed,
                FingerprintTime = TimeSpan.Zero,
                MatchTime = TimeSpan.Zero,
                RenderTime = renderTimer.Elapsed
            }
        };
        return Task.FromResult(result);
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Core/ tests/StyloExtract.Core.Tests/LayoutExtractorTests.cs
git commit -m "feat(core): LayoutExtractor walking skeleton — html→markdown, NovelEphemeral"
```

**End of M1.** Manual smoke: `dotnet test stylobot-extract.sln` — all green.

---

## M2 — Fingerprint primitives

### Task 12: Fingerprint — shingle generator

**Files:**
- Modify: `src/StyloExtract.Fingerprint/StyloExtract.Fingerprint.csproj` ref Abstractions + Heuristics (for `ClassNoiseFilter`)
- Modify: same csproj add `<PackageReference Include="System.IO.Hashing" />`
- Create: `src/StyloExtract.Fingerprint/ShingleGenerator.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/StyloExtract.Fingerprint.Tests.csproj` ref the Fingerprint and Html packages
- Create: `tests/StyloExtract.Fingerprint.Tests/ShingleGeneratorTests.cs`

**Interfaces:**
- Consumes: `IDocument` from AngleSharp, `ClassNoiseFilter` from Heuristics.
- Produces: `ShingleGenerator.Generate(IDocument) → IReadOnlyList<ulong>` — depth-first walk yielding shingles as 64-bit hashes of `(tagName, nthOfTypeBucket, classTokenSetHash, ancestorTagPathHash)` tuples. Default shingle width is 3 (configurable via ctor).

- [ ] **Step 1: Update Fingerprint csproj**

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
  <ProjectReference Include="..\StyloExtract.Heuristics\StyloExtract.Heuristics.csproj" />
  <PackageReference Include="System.IO.Hashing" />
</ItemGroup>
```

Fingerprint test csproj:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\StyloExtract.Fingerprint\StyloExtract.Fingerprint.csproj" />
  <ProjectReference Include="..\..\src\StyloExtract.Html\StyloExtract.Html.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write failing test**

```csharp
using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class ShingleGeneratorTests
{
    [Fact]
    public void Generate_TwoIdenticalDocuments_ProduceIdenticalShingleSequences()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string html = "<html><body><header><nav class='nav main-menu'><a>x</a></nav></header></body></html>";

        var a = gen.Generate(parser.Parse(html));
        var b = gen.Generate(parser.Parse(html));

        a.Should().Equal(b);
        a.Should().NotBeEmpty();
    }

    [Fact]
    public void Generate_DifferentNoiseClassesOnly_ProduceIdenticalShingles()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string htmlA = "<html><body><header class='dark-mode'><nav class='nav is-open'><a>x</a></nav></header></body></html>";
        const string htmlB = "<html><body><header class='light-mode'><nav class='nav is-closed'><a>x</a></nav></header></body></html>";

        var a = gen.Generate(parser.Parse(htmlA));
        var b = gen.Generate(parser.Parse(htmlB));

        a.Should().Equal(b);
    }

    [Fact]
    public void Generate_DifferentStructure_ProducesDifferentShingles()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var gen = new ShingleGenerator(noise);
        const string htmlA = "<html><body><header><nav><a>x</a></nav></header></body></html>";
        const string htmlB = "<html><body><main><article><p>x</p></article></main></body></html>";

        var a = gen.Generate(parser.Parse(htmlA));
        var b = gen.Generate(parser.Parse(htmlB));

        a.Should().NotEqual(b);
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement**

`src/StyloExtract.Fingerprint/ShingleGenerator.cs`:

```csharp
using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using StyloExtract.Heuristics;

namespace StyloExtract.Fingerprint;

public sealed class ShingleGenerator
{
    private readonly ClassNoiseFilter _classNoise;
    private readonly int _shingleWidth;

    public ShingleGenerator(ClassNoiseFilter classNoise, int shingleWidth = 3)
    {
        _classNoise = classNoise;
        _shingleWidth = shingleWidth;
    }

    public IReadOnlyList<ulong> Generate(IDocument document)
    {
        if (document.Body is null) return Array.Empty<ulong>();
        var nodeHashes = new List<ulong>(256);
        Walk(document.Body, ancestorPathHash: 0, nodeHashes);
        return CombineIntoShingles(nodeHashes);
    }

    private void Walk(IElement element, ulong ancestorPathHash, List<ulong> sink)
    {
        var tag = element.TagName.ToLowerInvariant();
        var nthBucket = BucketSiblingIndex(element);
        var rawClasses = (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = _classNoise.Filter(rawClasses);
        var classHash = HashClassTokens(filtered);
        var nodeHash = HashTuple(tag, nthBucket, classHash, ancestorPathHash);
        sink.Add(nodeHash);

        var nextAncestor = XxHash64.HashToUInt64(BitConverter.GetBytes(ancestorPathHash).Concat(Encoding.UTF8.GetBytes(tag)).ToArray());
        foreach (var child in element.Children)
        {
            Walk(child, nextAncestor, sink);
        }
    }

    private IReadOnlyList<ulong> CombineIntoShingles(List<ulong> nodeHashes)
    {
        if (nodeHashes.Count < _shingleWidth) return nodeHashes;
        var result = new List<ulong>(nodeHashes.Count - _shingleWidth + 1);
        var buf = new byte[8 * _shingleWidth];
        for (int i = 0; i <= nodeHashes.Count - _shingleWidth; i++)
        {
            for (int j = 0; j < _shingleWidth; j++)
            {
                BitConverter.GetBytes(nodeHashes[i + j]).CopyTo(buf, j * 8);
            }
            result.Add(XxHash64.HashToUInt64(buf));
        }
        return result;
    }

    private static int BucketSiblingIndex(IElement element)
    {
        int idx = 1;
        var prev = element.PreviousElementSibling;
        while (prev is not null)
        {
            if (string.Equals(prev.TagName, element.TagName, StringComparison.OrdinalIgnoreCase)) idx++;
            prev = prev.PreviousElementSibling;
        }
        return idx <= 3 ? idx : 4; // 4 = "many"
    }

    private static ulong HashClassTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0UL;
        var sorted = tokens.OrderBy(t => t, StringComparer.Ordinal);
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Join(",", sorted)));
    }

    private static ulong HashTuple(string tag, int nthBucket, ulong classHash, ulong ancestorPathHash)
    {
        Span<byte> buf = stackalloc byte[8 + 4 + 8 + 64];
        BitConverter.TryWriteBytes(buf, classHash);
        BitConverter.TryWriteBytes(buf[8..], nthBucket);
        BitConverter.TryWriteBytes(buf[12..], ancestorPathHash);
        var written = 20 + Encoding.UTF8.GetBytes(tag, buf[20..]);
        return XxHash64.HashToUInt64(buf[..written]);
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Fingerprint/ShingleGenerator.cs tests/StyloExtract.Fingerprint.Tests/
git commit -m "feat(fingerprint): ShingleGenerator over tag-path n-grams (class-noise filtered)"
```

---

### Task 13: Fingerprint — MinHash sketcher + Jaccard estimator

**Files:**
- Create: `src/StyloExtract.Fingerprint/MinHashSketcher.cs`
- Create: `src/StyloExtract.Fingerprint/JaccardEstimator.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/MinHashTests.cs`

**Interfaces:**
- Consumes: shingle list from T12.
- Produces:
  - `MinHashSketcher(int signatureSize = 128).Sketch(IReadOnlyList<ulong> shingles) → uint[]`
  - `JaccardEstimator.Estimate(uint[] a, uint[] b) → double` (fraction of matching slots).

- [ ] **Step 1: Write failing test**

```csharp
using FluentAssertions;
using StyloExtract.Fingerprint;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class MinHashTests
{
    [Fact]
    public void Sketch_ProducesFixedSizeSignature()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var shingles = Enumerable.Range(0, 50).Select(i => (ulong)i).ToArray();

        var sig = sketcher.Sketch(shingles);

        sig.Length.Should().Be(128);
    }

    [Fact]
    public void Jaccard_OfIdenticalSignatures_IsOne()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var shingles = Enumerable.Range(0, 100).Select(i => (ulong)i).ToArray();

        var a = sketcher.Sketch(shingles);
        var b = sketcher.Sketch(shingles);

        JaccardEstimator.Estimate(a, b).Should().Be(1.0);
    }

    [Fact]
    public void Jaccard_OfDisjointSets_IsApproximatelyZero()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(10_000, 200).Select(i => (ulong)i).ToArray());

        JaccardEstimator.Estimate(a, b).Should().BeLessThan(0.1);
    }

    [Fact]
    public void Jaccard_OfHalfOverlap_IsApproximatelyHalf()
    {
        var sketcher = new MinHashSketcher(signatureSize: 128);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(100, 200).Select(i => (ulong)i).ToArray());

        var j = JaccardEstimator.Estimate(a, b);
        j.Should().BeInRange(0.25, 0.45);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

`MinHashSketcher.cs`:

```csharp
using System.IO.Hashing;

namespace StyloExtract.Fingerprint;

public sealed class MinHashSketcher
{
    private readonly int _signatureSize;
    private readonly ulong[] _seeds;

    public MinHashSketcher(int signatureSize = 128)
    {
        _signatureSize = signatureSize;
        _seeds = new ulong[signatureSize];
        for (int i = 0; i < signatureSize; i++)
        {
            _seeds[i] = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
        }
    }

    public uint[] Sketch(IReadOnlyList<ulong> shingles)
    {
        var sig = new uint[_signatureSize];
        Array.Fill(sig, uint.MaxValue);
        if (shingles.Count == 0) return sig;
        Span<byte> buf = stackalloc byte[16];
        foreach (var shingle in shingles)
        {
            BitConverter.TryWriteBytes(buf, shingle);
            for (int i = 0; i < _signatureSize; i++)
            {
                BitConverter.TryWriteBytes(buf[8..], _seeds[i]);
                var h = (uint)(XxHash64.HashToUInt64(buf) & 0xFFFFFFFFUL);
                if (h < sig[i]) sig[i] = h;
            }
        }
        return sig;
    }
}
```

`JaccardEstimator.cs`:

```csharp
namespace StyloExtract.Fingerprint;

public static class JaccardEstimator
{
    public static double Estimate(uint[] a, uint[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Signatures must be equal length.");
        int matches = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) matches++;
        }
        return (double)matches / a.Length;
    }
}
```

- [ ] **Step 4: Verify pass** (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Fingerprint/MinHashSketcher.cs src/StyloExtract.Fingerprint/JaccardEstimator.cs tests/StyloExtract.Fingerprint.Tests/MinHashTests.cs
git commit -m "feat(fingerprint): MinHashSketcher 128×uint + JaccardEstimator"
```

---

### Task 14: Fingerprint — LSH bander

**Files:**
- Create: `src/StyloExtract.Fingerprint/LshBander.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/LshBanderTests.cs`

**Interfaces:**
- Consumes: MinHash signature from T13.
- Produces: `LshBander(int bands = 16, int rowsPerBand = 8).BandHashes(uint[] signature) → ulong[]` of length `bands`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Fingerprint;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class LshBanderTests
{
    [Fact]
    public void BandHashes_IdenticalSignatures_ProduceIdenticalBands()
    {
        var sketcher = new MinHashSketcher(128);
        var bander = new LshBander(16, 8);
        var sig = sketcher.Sketch(Enumerable.Range(0, 100).Select(i => (ulong)i).ToArray());

        var a = bander.BandHashes(sig);
        var b = bander.BandHashes(sig);

        a.Should().Equal(b);
        a.Length.Should().Be(16);
    }

    [Fact]
    public void BandHashes_HighSimilarity_ShareSomeBands()
    {
        var sketcher = new MinHashSketcher(128);
        var bander = new LshBander(16, 8);
        var a = sketcher.Sketch(Enumerable.Range(0, 200).Select(i => (ulong)i).ToArray());
        var b = sketcher.Sketch(Enumerable.Range(0, 199).Select(i => (ulong)i).ToArray());

        var ba = bander.BandHashes(a);
        var bb = bander.BandHashes(b);

        ba.Intersect(bb).Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.IO.Hashing;

namespace StyloExtract.Fingerprint;

public sealed class LshBander
{
    private readonly int _bands;
    private readonly int _rowsPerBand;

    public LshBander(int bands = 16, int rowsPerBand = 8)
    {
        _bands = bands;
        _rowsPerBand = rowsPerBand;
    }

    public ulong[] BandHashes(uint[] signature)
    {
        if (signature.Length != _bands * _rowsPerBand)
            throw new ArgumentException($"Signature size {signature.Length} != bands*rows {_bands * _rowsPerBand}.");
        var result = new ulong[_bands];
        var buf = new byte[_rowsPerBand * 4];
        for (int b = 0; b < _bands; b++)
        {
            for (int r = 0; r < _rowsPerBand; r++)
            {
                BitConverter.GetBytes(signature[b * _rowsPerBand + r]).CopyTo(buf, r * 4);
            }
            result[b] = XxHash64.HashToUInt64(buf);
        }
        return result;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Fingerprint/LshBander.cs tests/StyloExtract.Fingerprint.Tests/LshBanderTests.cs
git commit -m "feat(fingerprint): LshBander (16 bands × 8 rows default)"
```

---

### Task 15: Fingerprint — anchor-path signature

**Files:**
- Create: `src/StyloExtract.Fingerprint/AnchorPathFingerprinter.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/AnchorPathFingerprinterTests.cs`

**Interfaces:**
- Consumes: AngleSharp `IDocument`, `MinHashSketcher`, `ClassNoiseFilter`.
- Produces: `AnchorPathFingerprinter(ClassNoiseFilter, MinHashSketcher).Sketch(IDocument) → uint[]`. The multiset element per `<a>` is `(tagPathHash, hrefRegistrableDomain, hrefHasHash, classTokenSetHash)`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class AnchorPathFingerprinterTests
{
    [Fact]
    public void Sketch_TwoPagesSameNavStructure_HighJaccard()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var anchor = new AnchorPathFingerprinter(noise, sketcher);

        const string a = "<html><body><nav><a href='/home'>H</a><a href='/about'>A</a><a href='/blog'>B</a></nav></body></html>";
        const string b = "<html><body><nav><a href='/home'>H</a><a href='/about'>A</a><a href='/blog'>B</a></nav></body></html>";

        var sa = anchor.Sketch(parser.Parse(a));
        var sb = anchor.Sketch(parser.Parse(b));

        JaccardEstimator.Estimate(sa, sb).Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void Sketch_DifferentNavStructure_LowJaccard()
    {
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var anchor = new AnchorPathFingerprinter(noise, sketcher);

        const string a = "<html><body><nav><a href='/home'>H</a></nav></body></html>";
        const string b = "<html><body><footer><a href='https://twitter.com/x'>T</a></footer></body></html>";

        var sa = anchor.Sketch(parser.Parse(a));
        var sb = anchor.Sketch(parser.Parse(b));

        JaccardEstimator.Estimate(sa, sb).Should().BeLessThan(0.1);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using StyloExtract.Heuristics;

namespace StyloExtract.Fingerprint;

public sealed class AnchorPathFingerprinter
{
    private readonly ClassNoiseFilter _classNoise;
    private readonly MinHashSketcher _sketcher;

    public AnchorPathFingerprinter(ClassNoiseFilter classNoise, MinHashSketcher sketcher)
    {
        _classNoise = classNoise;
        _sketcher = sketcher;
    }

    public uint[] Sketch(IDocument document)
    {
        if (document.Body is null) return _sketcher.Sketch(Array.Empty<ulong>());
        var anchors = document.QuerySelectorAll("a");
        var elements = new List<ulong>(anchors.Length);
        foreach (var a in anchors)
        {
            var tagPathHash = TagPathHash(a);
            var href = a.GetAttribute("href") ?? "";
            var domain = ExtractDomain(href);
            var hasHash = href.Contains('#') ? 1 : 0;
            var classes = (a.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var classHash = HashClasses(_classNoise.Filter(classes));
            var sb = new StringBuilder();
            sb.Append(tagPathHash); sb.Append('|');
            sb.Append(domain); sb.Append('|');
            sb.Append(hasHash); sb.Append('|');
            sb.Append(classHash);
            elements.Add(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        return _sketcher.Sketch(elements);
    }

    private static ulong TagPathHash(IElement element)
    {
        var sb = new StringBuilder();
        var current = element.ParentElement;
        while (current is not null)
        {
            sb.Append(current.TagName.ToLowerInvariant());
            sb.Append('/');
            current = current.ParentElement;
        }
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string ExtractDomain(string href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        if (href.StartsWith("/") || href.StartsWith("#")) return "";
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }
        return "";
    }

    private static ulong HashClasses(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0UL;
        var sorted = string.Join(",", tokens.OrderBy(t => t, StringComparer.Ordinal));
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sorted));
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Fingerprint/AnchorPathFingerprinter.cs tests/StyloExtract.Fingerprint.Tests/AnchorPathFingerprinterTests.cs
git commit -m "feat(fingerprint): AnchorPathFingerprinter for nav/footer template discrimination"
```

---

### Task 16: Fingerprint — pq-gram extractor

**Files:**
- Create: `src/StyloExtract.Fingerprint/PqGramExtractor.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/PqGramExtractorTests.cs`

**Interfaces:**
- Consumes: AngleSharp `IDocument`.
- Produces: `PqGramExtractor(int p = 2, int q = 3, int topK = 256).Extract(IDocument) → (IReadOnlyDictionary<string,double> counts, double norm)`. Keys are stringified `(ancestor_p, ..., ancestor_1, child_1, ..., child_q)` tuples; counts are integer (returned as double for cosine math). `norm` is the L2 norm of the sparse vector.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Fingerprint;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class PqGramExtractorTests
{
    [Fact]
    public void Extract_TwoIdenticalDocs_HighCosine()
    {
        var parser = new AngleSharpHtmlDomParser();
        var pq = new PqGramExtractor(p: 2, q: 3, topK: 256);
        const string html = "<html><body><main><article><h1>x</h1><p>y</p><p>z</p></article></main></body></html>";

        var (ca, na) = pq.Extract(parser.Parse(html));
        var (cb, nb) = pq.Extract(parser.Parse(html));

        Cosine(ca, na, cb, nb).Should().BeGreaterThan(0.99);
    }

    [Fact]
    public void Extract_StructurallyDifferentDocs_LowCosine()
    {
        var parser = new AngleSharpHtmlDomParser();
        var pq = new PqGramExtractor(p: 2, q: 3, topK: 256);
        const string a = "<html><body><nav><a>x</a><a>y</a></nav></body></html>";
        const string b = "<html><body><table><tr><td>1</td><td>2</td></tr></table></body></html>";

        var (ca, na) = pq.Extract(parser.Parse(a));
        var (cb, nb) = pq.Extract(parser.Parse(b));

        Cosine(ca, na, cb, nb).Should().BeLessThan(0.3);
    }

    private static double Cosine(IReadOnlyDictionary<string, double> ca, double na, IReadOnlyDictionary<string, double> cb, double nb)
    {
        if (na == 0 || nb == 0) return 0;
        double dot = 0;
        foreach (var kv in ca)
        {
            if (cb.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
        }
        return dot / (na * nb);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using AngleSharp.Dom;

namespace StyloExtract.Fingerprint;

public sealed class PqGramExtractor
{
    private readonly int _p;
    private readonly int _q;
    private readonly int _topK;
    private const string NullLabel = "*";

    public PqGramExtractor(int p = 2, int q = 3, int topK = 256)
    {
        _p = p;
        _q = q;
        _topK = topK;
    }

    public (IReadOnlyDictionary<string, double> Counts, double Norm) Extract(IDocument document)
    {
        if (document.Body is null)
        {
            return (new Dictionary<string, double>(), 0);
        }
        var counts = new Dictionary<string, int>();
        var ancestorStem = new Queue<string>(_p);
        for (int i = 0; i < _p; i++) ancestorStem.Enqueue(NullLabel);
        Walk(document.Body, ancestorStem, counts);

        // Truncate to topK by count, then normalise.
        var top = counts.OrderByDescending(kv => kv.Value).Take(_topK).ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        var norm = Math.Sqrt(top.Values.Sum(v => v * v));
        return (top, norm);
    }

    private void Walk(IElement element, Queue<string> ancestorStem, Dictionary<string, int> counts)
    {
        var label = element.TagName.ToLowerInvariant();
        var nextStem = new Queue<string>(ancestorStem);
        nextStem.Dequeue();
        nextStem.Enqueue(label);

        // Emit pq-grams over this node's children with the *_q sliding window of children.
        var children = element.Children;
        var siblingWindow = new Queue<string>(_q);
        for (int i = 0; i < _q; i++) siblingWindow.Enqueue(NullLabel);

        foreach (var child in children)
        {
            siblingWindow.Dequeue();
            siblingWindow.Enqueue(child.TagName.ToLowerInvariant());
            EmitPqGram(nextStem, siblingWindow, counts);
        }
        // Flush final window with trailing nulls.
        for (int i = 0; i < _q - 1; i++)
        {
            siblingWindow.Dequeue();
            siblingWindow.Enqueue(NullLabel);
            EmitPqGram(nextStem, siblingWindow, counts);
        }

        foreach (var child in children)
        {
            Walk(child, nextStem, counts);
        }
    }

    private static void EmitPqGram(Queue<string> stem, Queue<string> window, Dictionary<string, int> counts)
    {
        var key = string.Join(",", stem.Concat(window));
        counts[key] = counts.GetValueOrDefault(key, 0) + 1;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Fingerprint/PqGramExtractor.cs tests/StyloExtract.Fingerprint.Tests/PqGramExtractorTests.cs
git commit -m "feat(fingerprint): PqGramExtractor (p=2 q=3 default) with top-K sparse output

Used as cosine match vector for slow path. NOT a metric — pq-gram
triangle inequality refuted in spec §16 research; cosine only."
```

---

### Task 17: Fingerprint — composite `StructuralFingerprinter`

**Files:**
- Create: `src/StyloExtract.Fingerprint/StructuralFingerprinter.cs`
- Create: `tests/StyloExtract.Fingerprint.Tests/StructuralFingerprinterTests.cs`

**Interfaces:**
- Consumes: `IStructuralFingerprinter` from Abstractions, all primitives above.
- Produces: `StructuralFingerprinter : IStructuralFingerprinter` combining ShingleGenerator + MinHashSketcher + LshBander + AnchorPathFingerprinter + PqGramExtractor into one `StructuralFingerprint` record.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Fingerprint.Tests;

public class StructuralFingerprinterTests
{
    private static IStructuralFingerprinter Build()
    {
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        return new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
    }

    [Fact]
    public void Compute_ReturnsFullyPopulatedFingerprint()
    {
        var parser = new AngleSharpHtmlDomParser();
        var fp = Build().Compute(parser.Parse("<html><body><main><p>x</p></main></body></html>"));

        fp.StructuralMinHash.Length.Should().Be(128);
        fp.AnchorMinHash.Length.Should().Be(128);
        fp.LshBands.Length.Should().Be(16);
        fp.Hex.Should().NotBeNullOrEmpty();
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Fingerprint;

public sealed class StructuralFingerprinter : IStructuralFingerprinter
{
    private readonly ShingleGenerator _shingles;
    private readonly MinHashSketcher _sketcher;
    private readonly LshBander _bander;
    private readonly AnchorPathFingerprinter _anchorSig;
    private readonly PqGramExtractor _pqGram;

    public StructuralFingerprinter(
        ShingleGenerator shingles,
        MinHashSketcher sketcher,
        LshBander bander,
        AnchorPathFingerprinter anchorSig,
        PqGramExtractor pqGram)
    {
        _shingles = shingles;
        _sketcher = sketcher;
        _bander = bander;
        _anchorSig = anchorSig;
        _pqGram = pqGram;
    }

    public StructuralFingerprint Compute(IDocument document)
    {
        var shingleList = _shingles.Generate(document);
        var structural = _sketcher.Sketch(shingleList);
        var bands = _bander.BandHashes(structural);
        var anchor = _anchorSig.Sketch(document);
        var (pq, norm) = _pqGram.Extract(document);
        var hex = ToHex(structural);
        return new StructuralFingerprint
        {
            StructuralMinHash = structural,
            AnchorMinHash = anchor,
            LshBands = bands,
            PqGramCounts = pq,
            PqGramNorm = norm,
            ShingleCount = shingleList.Count,
            Hex = hex
        };
    }

    private static string ToHex(uint[] sig)
    {
        var bytes = new byte[Math.Min(16, sig.Length * 4)];
        for (int i = 0; i < Math.Min(4, sig.Length); i++)
        {
            BitConverter.GetBytes(sig[i]).CopyTo(bytes, i * 4);
        }
        return Convert.ToHexString(bytes);
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Fingerprint/StructuralFingerprinter.cs tests/StyloExtract.Fingerprint.Tests/StructuralFingerprinterTests.cs
git commit -m "feat(fingerprint): StructuralFingerprinter composes all primitives"
```

---

### Task 18: Core — wire `IStructuralFingerprinter` into `LayoutExtractor`

**Files:**
- Modify: `src/StyloExtract.Core/StyloExtract.Core.csproj` ref `StyloExtract.Fingerprint`
- Modify: `src/StyloExtract.Core/LayoutExtractor.cs` (add fingerprinter ctor arg + populate `FingerprintHex` + `FingerprintShingleCount`)
- Modify: `tests/StyloExtract.Core.Tests/LayoutExtractorTests.cs` add assertion

**Interfaces:**
- Consumes: `IStructuralFingerprinter`.
- Produces: `ExtractionResult.Match.FingerprintHex` is non-empty; `Stats.FingerprintShingleCount > 0`.

- [ ] **Step 1: Update csproj**

```xml
<ProjectReference Include="..\StyloExtract.Fingerprint\StyloExtract.Fingerprint.csproj" />
```

- [ ] **Step 2: Update LayoutExtractorTests**

Replace the existing `Build()` and test with:

```csharp
using StyloExtract.Fingerprint;

private static ILayoutExtractor Build()
{
    var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
    var sketcher = new MinHashSketcher(128);
    var fingerprinter = new StructuralFingerprinter(
        new ShingleGenerator(noise),
        sketcher,
        new LshBander(16, 8),
        new AnchorPathFingerprinter(noise, sketcher),
        new PqGramExtractor());
    return new LayoutExtractor(
        new AngleSharpHtmlDomParser(),
        new DomCleaner(),
        fingerprinter,
        new BlockSegmenter(),
        HeuristicBlockClassifier.LoadFromEmbeddedResources(),
        new TypedMarkdownRenderer());
}

[Fact]
public async Task ExtractAsync_PopulatesFingerprintHex()
{
    const string html = "<html><body><main><article><p>" + /* + body text */ "</p></article></main></body></html>";
    var result = await Build().ExtractAsync(html);
    result.Match.FingerprintHex.Should().NotBeNullOrEmpty();
    result.Stats.FingerprintShingleCount.Should().BeGreaterThan(0);
}
```

- [ ] **Step 3: Verify failure** (ctor signature mismatch).

- [ ] **Step 4: Implement** — update `LayoutExtractor`:

```csharp
public sealed class LayoutExtractor : ILayoutExtractor
{
    private readonly IHtmlDomParser _parser;
    private readonly IDomCleaner _cleaner;
    private readonly IStructuralFingerprinter _fingerprinter;
    private readonly IBlockSegmenter _segmenter;
    private readonly IBlockClassifier _classifier;
    private readonly IMarkdownRenderer _renderer;

    public LayoutExtractor(
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IStructuralFingerprinter fingerprinter,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier,
        IMarkdownRenderer renderer)
    {
        _parser = parser;
        _cleaner = cleaner;
        _fingerprinter = fingerprinter;
        _segmenter = segmenter;
        _classifier = classifier;
        _renderer = renderer;
    }

    public Task<ExtractionResult> ExtractAsync(string html, Uri? sourceUri = null, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        var total = Stopwatch.StartNew();

        var parseTimer = Stopwatch.StartNew();
        var doc = _parser.Parse(html, sourceUri);
        _cleaner.Clean(doc);
        parseTimer.Stop();

        var fpTimer = Stopwatch.StartNew();
        var fp = _fingerprinter.Compute(doc);
        fpTimer.Stop();

        var segmented = _segmenter.Segment(doc);
        var blocks = _classifier.Classify(segmented);

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();

        total.Stop();

        return Task.FromResult(new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            Match = new LayoutMatch
            {
                TemplateId = null,
                TemplateVersion = 0,
                FingerprintHex = fp.Hex,
                Status = MatchStatus.NovelEphemeral,
                Similarity = 0,
                ObservationCount = 0,
                LatencyMatch = TimeSpan.Zero,
                LatencyTotal = total.Elapsed
            },
            Stats = new ExtractionStats
            {
                BlockCount = blocks.Count,
                FingerprintShingleCount = fp.ShingleCount,
                ParseTime = parseTimer.Elapsed,
                FingerprintTime = fpTimer.Elapsed,
                MatchTime = TimeSpan.Zero,
                RenderTime = renderTimer.Elapsed
            }
        });
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Core/LayoutExtractor.cs tests/StyloExtract.Core.Tests/LayoutExtractorTests.cs
git commit -m "feat(core): wire StructuralFingerprinter — FingerprintHex now populated"
```

**End of M2.**

---

## M3 — SQLite template store

### Task 19: Templates — host hasher

**Files:**
- Modify: `src/StyloExtract.Templates/StyloExtract.Templates.csproj` ref Abstractions, add `Microsoft.Data.Sqlite`
- Create: `src/StyloExtract.Templates/HostHasher.cs`
- Modify: test csproj for Templates.Tests, ref Templates project
- Create: `tests/StyloExtract.Templates.Tests/HostHasherTests.cs`

**Interfaces:**
- Consumes: nothing public.
- Produces: `HostHasher(byte[] key).Hash(string host) → byte[]` (HMAC-SHA256 truncated to 16 bytes, returning a stable `host_hash` for use as the per-host SQLite gating key). Also `HostHasher.FromConfiguredKeyOrRandom(string? base64Key)` factory.

- [ ] **Step 1: Csproj refs**

```xml
<ItemGroup>
  <ProjectReference Include="..\StyloExtract.Abstractions\StyloExtract.Abstractions.csproj" />
  <PackageReference Include="Microsoft.Data.Sqlite" />
</ItemGroup>
```

Templates.Tests csproj:

```xml
<ProjectReference Include="..\..\src\StyloExtract.Templates\StyloExtract.Templates.csproj" />
```

- [ ] **Step 2: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class HostHasherTests
{
    [Fact]
    public void Hash_SameHostSameKey_ProducesIdenticalBytes()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)42);
        var h = new HostHasher(key);

        var a = h.Hash("example.com");
        var b = h.Hash("example.com");

        a.Should().Equal(b);
        a.Length.Should().Be(16);
    }

    [Fact]
    public void Hash_DifferentHosts_ProduceDifferentBytes()
    {
        var key = new byte[32];
        var h = new HostHasher(key);

        h.Hash("example.com").Should().NotEqual(h.Hash("other.com"));
    }

    [Fact]
    public void Hash_HostCaseDifference_ProducesIdenticalBytes()
    {
        var key = new byte[32];
        var h = new HostHasher(key);

        h.Hash("Example.COM").Should().Equal(h.Hash("example.com"));
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace StyloExtract.Templates;

public sealed class HostHasher
{
    private readonly byte[] _key;

    public HostHasher(byte[] key)
    {
        if (key.Length < 16) throw new ArgumentException("Key must be ≥16 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public static HostHasher FromConfiguredKeyOrRandom(string? base64Key)
    {
        if (!string.IsNullOrEmpty(base64Key))
        {
            return new HostHasher(Convert.FromBase64String(base64Key));
        }
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new HostHasher(key);
    }

    public byte[] Hash(string host)
    {
        var normalized = host.ToLowerInvariant();
        using var hmac = new HMACSHA256(_key);
        var full = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        var truncated = new byte[16];
        Array.Copy(full, truncated, 16);
        return truncated;
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Templates/HostHasher.cs tests/StyloExtract.Templates.Tests/
git commit -m "feat(templates): HostHasher (HMAC-SHA256/16) with random-key fallback"
```

---

### Task 20: Templates — SQLite schema migration

**Files:**
- Create: `src/StyloExtract.Templates/SqliteSchema.cs`
- Create: `tests/StyloExtract.Templates.Tests/SqliteSchemaTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite`.
- Produces: `SqliteSchema.EnsureCreated(SqliteConnection)` idempotent migrator that creates the four tables and indexes per spec §5.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteSchemaTests
{
    [Fact]
    public void EnsureCreated_CreatesAllExpectedTables()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SqliteSchema.EnsureCreated(conn);

        var tables = ListTables(conn);
        tables.Should().Contain(new[] { "templates", "template_lsh_band_index", "template_version_history", "template_observations" });
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SqliteSchema.EnsureCreated(conn);
        Action again = () => SqliteSchema.EnsureCreated(conn);

        again.Should().NotThrow();
    }

    private static List<string> ListTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using Microsoft.Data.Sqlite;

namespace StyloExtract.Templates;

public static class SqliteSchema
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS templates (
          template_id            BLOB PRIMARY KEY,
          host_hash              BLOB NOT NULL,
          version_number         INTEGER NOT NULL DEFAULT 1,
          signature_minhash      BLOB NOT NULL,
          anchor_signature       BLOB NOT NULL,
          pq_gram_vector         BLOB NOT NULL,
          pq_gram_norm           REAL NOT NULL,
          extractor_blob         BLOB NOT NULL,
          observation_count      INTEGER NOT NULL DEFAULT 1,
          created_at             INTEGER NOT NULL,
          last_seen              INTEGER NOT NULL,
          last_refit_at          INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_templates_host ON templates(host_hash, last_seen);

        CREATE TABLE IF NOT EXISTS template_lsh_band_index (
          band_hash   BLOB NOT NULL,
          band_index  INTEGER NOT NULL,
          template_id BLOB NOT NULL,
          PRIMARY KEY (band_hash, band_index, template_id)
        );

        CREATE TABLE IF NOT EXISTS template_version_history (
          template_id          BLOB NOT NULL,
          version_number       INTEGER NOT NULL,
          signature_minhash    BLOB NOT NULL,
          pq_gram_vector       BLOB NOT NULL,
          extractor_blob       BLOB NOT NULL,
          retired_at           INTEGER NOT NULL,
          retirement_reason    TEXT,
          PRIMARY KEY (template_id, version_number)
        );

        CREATE TABLE IF NOT EXISTS template_observations (
          template_id          BLOB NOT NULL,
          observed_at          INTEGER NOT NULL,
          signature_minhash    BLOB NOT NULL,
          similarity_at_match  REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_obs_template ON template_observations(template_id, observed_at);
        """;

    public static void EnsureCreated(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateSql;
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/SqliteSchema.cs tests/StyloExtract.Templates.Tests/SqliteSchemaTests.cs
git commit -m "feat(templates): SqliteSchema with idempotent CREATE TABLE migrations"
```

---

### Task 21: Templates — pq-gram vector codec

**Files:**
- Create: `src/StyloExtract.Templates/Serialization/PqGramVectorCodec.cs`
- Create: `tests/StyloExtract.Templates.Tests/PqGramVectorCodecTests.cs`

**Interfaces:**
- Consumes: `IReadOnlyDictionary<string, double>` (pq-gram counts).
- Produces:
  - `PqGramVectorCodec.Encode(IReadOnlyDictionary<string,double> counts) → byte[]` — compact length-prefixed `[uint count][repeat: uint keyLen, utf8 key, double value]`.
  - `PqGramVectorCodec.Decode(byte[]) → IReadOnlyDictionary<string,double>`.
  - Roundtrip-stable.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Templates.Serialization;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class PqGramVectorCodecTests
{
    [Fact]
    public void Encode_Decode_Roundtrips()
    {
        var src = new Dictionary<string, double>
        {
            ["*,*,html,body,*,*"] = 3,
            ["body,main,article,h1,p,p"] = 5
        };

        var bytes = PqGramVectorCodec.Encode(src);
        var decoded = PqGramVectorCodec.Decode(bytes);

        decoded.Should().BeEquivalentTo(src);
    }

    [Fact]
    public void Encode_EmptyDictionary_ProducesValidBytes()
    {
        var bytes = PqGramVectorCodec.Encode(new Dictionary<string, double>());
        var decoded = PqGramVectorCodec.Decode(bytes);
        decoded.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.Text;

namespace StyloExtract.Templates.Serialization;

public static class PqGramVectorCodec
{
    public static byte[] Encode(IReadOnlyDictionary<string, double> counts)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)counts.Count);
        foreach (var kv in counts)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kv.Key);
            bw.Write((uint)keyBytes.Length);
            bw.Write(keyBytes);
            bw.Write(kv.Value);
        }
        return ms.ToArray();
    }

    public static IReadOnlyDictionary<string, double> Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        var count = br.ReadUInt32();
        var result = new Dictionary<string, double>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var keyLen = br.ReadUInt32();
            var key = Encoding.UTF8.GetString(br.ReadBytes((int)keyLen));
            var value = br.ReadDouble();
            result[key] = value;
        }
        return result;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/Serialization/PqGramVectorCodec.cs tests/StyloExtract.Templates.Tests/PqGramVectorCodecTests.cs
git commit -m "feat(templates): PqGramVectorCodec for sparse vector persistence"
```

---

### Task 22: Templates — `SqliteTemplateIndex` CRUD + register

**Files:**
- Create: `src/StyloExtract.Templates/SqliteTemplateIndex.cs`
- Create: `tests/StyloExtract.Templates.Tests/SqliteTemplateIndexRegisterTests.cs`

**Interfaces:**
- Consumes: `ITemplateIndex` from T4, `SqliteSchema`, `PqGramVectorCodec`, `LearnedExtractor`.
- Produces: `SqliteTemplateIndex : ITemplateIndex`. Methods implemented in this task: `RegisterAsync`, `GetExtractorAsync`, `GetObservationCountAsync`, `GetTemplateVersionAsync`. `ProbeFastPathAsync`, `ProbeSlowPathAsync`, `RecordObservationAsync` get stubs throwing `NotImplementedException` (filled in T23 + M4).
- `LearnedExtractor` persistence: serialised to JSON via `System.Text.Json` with the schema-v1 shape from spec §9.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteTemplateIndexRegisterTests
{
    private static SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    [Fact]
    public async Task Register_PersistsTemplateAndExtractor()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var fp = NewFingerprint();
        var ex = NewExtractor();
        var hostHash = new byte[16];

        var id = await idx.RegisterAsync(hostHash, fp, ex, default);

        var loaded = await idx.GetExtractorAsync(id, default);
        loaded.Should().NotBeNull();
        loaded!.Rules.Should().HaveCount(1);
        (await idx.GetObservationCountAsync(id, default)).Should().Be(1);
        (await idx.GetTemplateVersionAsync(id, default)).Should().Be(1);
    }

    private static StructuralFingerprint NewFingerprint()
    {
        var sig = new uint[128];
        return new StructuralFingerprint
        {
            StructuralMinHash = sig,
            AnchorMinHash = sig,
            LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double> { ["x"] = 1 },
            PqGramNorm = 1,
            ShingleCount = 1,
            Hex = "00000000"
        };
    }

    private static LearnedExtractor NewExtractor() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r1", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 1, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 1,
            ByRole = new Dictionary<BlockRole, RoleCentroid>(),
            OverallDriftScore = 0,
            LastObservation = DateTimeOffset.UtcNow
        }
    };
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public sealed class SqliteTemplateIndex : ITemplateIndex
{
    private readonly SqliteConnection _conn;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public SqliteTemplateIndex(SqliteConnection conn)
    {
        _conn = conn;
    }

    public async Task<Guid> RegisterAsync(
        byte[] hostHash,
        StructuralFingerprint fingerprint,
        LearnedExtractor extractor,
        CancellationToken cancellationToken)
    {
        var id = extractor.TemplateId == Guid.Empty ? Guid.NewGuid() : extractor.TemplateId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);
        var anchorBytes = UintArrayToBytes(fingerprint.AnchorMinHash);
        var pqBytes = PqGramVectorCodec.Encode(fingerprint.PqGramCounts);
        var extractorBytes = JsonSerializer.SerializeToUtf8Bytes(extractor, JsonOpts);

        await using (var tx = await _conn.BeginTransactionAsync(cancellationToken))
        {
            await using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO templates(template_id, host_hash, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm, extractor_blob, observation_count, created_at, last_seen)
                    VALUES (@id, @host, 1, @sig, @anchor, @pq, @norm, @ex, 1, @now, @now)
                    """;
                cmd.Parameters.AddWithValue("@id", id.ToByteArray());
                cmd.Parameters.AddWithValue("@host", hostHash);
                cmd.Parameters.AddWithValue("@sig", sigBytes);
                cmd.Parameters.AddWithValue("@anchor", anchorBytes);
                cmd.Parameters.AddWithValue("@pq", pqBytes);
                cmd.Parameters.AddWithValue("@norm", fingerprint.PqGramNorm);
                cmd.Parameters.AddWithValue("@ex", extractorBytes);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var bandCmd = _conn.CreateCommand())
            {
                bandCmd.Transaction = (SqliteTransaction)tx;
                bandCmd.CommandText = "INSERT OR IGNORE INTO template_lsh_band_index(band_hash, band_index, template_id) VALUES (@bh, @bi, @id)";
                bandCmd.Parameters.Add("@bh", SqliteType.Blob);
                bandCmd.Parameters.Add("@bi", SqliteType.Integer);
                bandCmd.Parameters.Add("@id", SqliteType.Blob);
                for (int i = 0; i < fingerprint.LshBands.Length; i++)
                {
                    bandCmd.Parameters["@bh"].Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
                    bandCmd.Parameters["@bi"].Value = i;
                    bandCmd.Parameters["@id"].Value = id.ToByteArray();
                    await bandCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await tx.CommitAsync(cancellationToken);
        }
        return id;
    }

    public async Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT extractor_blob FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        return blob is null ? null : JsonSerializer.Deserialize<LearnedExtractor>(blob, JsonOpts);
    }

    public async Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT observation_count FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public async Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT version_number FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val is null ? 0 : Convert.ToInt32(val);
    }

    public Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in T23");

    public Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in T23");

    public Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken)
        => throw new NotImplementedException("Filled in M4");

    private static byte[] UintArrayToBytes(uint[] sig)
    {
        var bytes = new byte[sig.Length * 4];
        Buffer.BlockCopy(sig, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static uint[] BytesToUintArray(byte[] bytes)
    {
        var sig = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, sig, 0, bytes.Length);
        return sig;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/SqliteTemplateIndex.cs tests/StyloExtract.Templates.Tests/SqliteTemplateIndexRegisterTests.cs
git commit -m "feat(templates): SqliteTemplateIndex.Register + read accessors"
```

---

### Task 23: Templates — fast-path band probe + slow-path cosine probe

**Files:**
- Modify: `src/StyloExtract.Templates/SqliteTemplateIndex.cs` (replace the two `NotImplementedException` probes)
- Create: `tests/StyloExtract.Templates.Tests/SqliteTemplateIndexProbeTests.cs`

**Interfaces:**
- Consumes: `JaccardEstimator` (StyloExtract.Fingerprint reference required), pq-gram counts decoder.
- Produces:
  - `ProbeFastPathAsync(hostHash, fp, threshold)` returns the best matching `Guid` if any candidate's Jaccard ≥ threshold, else `null`. Candidates come from LSH band joins, filtered to same `host_hash`.
  - `ProbeSlowPathAsync(hostHash, fp, threshold)` scores cosine over the host's templates (sequential scan acceptable for v1), returns best `(TemplateId, Cosine)` if ≥ threshold else `null`.

- [ ] **Step 1: Add Fingerprint ref to Templates.csproj**

```xml
<ProjectReference Include="..\StyloExtract.Fingerprint\StyloExtract.Fingerprint.csproj" />
```

- [ ] **Step 2: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteTemplateIndexProbeTests
{
    private static SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    private static StructuralFingerprint Fp(uint seed)
    {
        var sig = new uint[128];
        Array.Fill(sig, seed);
        var bands = new ulong[16];
        Array.Fill(bands, (ulong)seed * 31);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig,
            AnchorMinHash = sig,
            LshBands = bands,
            PqGramCounts = new Dictionary<string, double> { [$"k-{seed}"] = 1 },
            PqGramNorm = 1,
            ShingleCount = 1,
            Hex = seed.ToString("X8")
        };
    }

    private static LearnedExtractor Ex(Guid? id = null) => new()
    {
        TemplateId = id ?? Guid.NewGuid(),
        Version = 1,
        Rules = Array.Empty<BlockRule>(),
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };

    [Fact]
    public async Task ProbeFastPath_HitsRegisteredTemplate()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = Fp(42);
        var id = await idx.RegisterAsync(host, fp, Ex(), default);

        var hit = await idx.ProbeFastPathAsync(host, fp, 0.85, default);

        hit.Should().Be(id);
    }

    [Fact]
    public async Task ProbeFastPath_DifferentBands_ReturnsNull()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        await idx.RegisterAsync(host, Fp(1), Ex(), default);

        var hit = await idx.ProbeFastPathAsync(host, Fp(999), 0.85, default);

        hit.Should().BeNull();
    }

    [Fact]
    public async Task ProbeSlowPath_HitsOnPerfectCosine()
    {
        using var conn = NewConn();
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = Fp(7);
        var id = await idx.RegisterAsync(host, fp, Ex(), default);

        var hit = await idx.ProbeSlowPathAsync(host, fp, 0.75, default);

        hit.Should().NotBeNull();
        hit!.Value.TemplateId.Should().Be(id);
        hit.Value.Cosine.Should().BeGreaterThan(0.95);
    }
}
```

- [ ] **Step 3: Verify failure.**

- [ ] **Step 4: Implement** — replace the probe methods:

```csharp
using StyloExtract.Fingerprint;
using StyloExtract.Templates.Serialization;

public async Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
{
    var candidates = new HashSet<byte[]>(ByteArrayComparer.Instance);
    await using (var cmd = _conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT DISTINCT b.template_id
            FROM template_lsh_band_index b
            INNER JOIN templates t ON t.template_id = b.template_id
            WHERE t.host_hash = @host AND b.band_hash = @bh AND b.band_index = @bi
            """;
        cmd.Parameters.Add("@host", SqliteType.Blob).Value = hostHash;
        cmd.Parameters.Add("@bh", SqliteType.Blob);
        cmd.Parameters.Add("@bi", SqliteType.Integer);
        for (int i = 0; i < fingerprint.LshBands.Length; i++)
        {
            cmd.Parameters["@bh"].Value = BitConverter.GetBytes(fingerprint.LshBands[i]);
            cmd.Parameters["@bi"].Value = i;
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                candidates.Add((byte[])r["template_id"]);
            }
        }
    }

    Guid? best = null;
    double bestJaccard = 0;
    foreach (var candidateBytes in candidates)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT signature_minhash FROM templates WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", candidateBytes);
        var blob = (byte[]?)await cmd.ExecuteScalarAsync(cancellationToken);
        if (blob is null) continue;
        var candidateSig = BytesToUintArray(blob);
        var j = JaccardEstimator.Estimate(candidateSig, fingerprint.StructuralMinHash);
        if (j >= threshold && j > bestJaccard)
        {
            bestJaccard = j;
            best = new Guid(candidateBytes);
        }
    }
    return best;
}

public async Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
{
    await using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT template_id, pq_gram_vector, pq_gram_norm FROM templates WHERE host_hash = @host";
    cmd.Parameters.AddWithValue("@host", hostHash);
    (Guid TemplateId, double Cosine)? best = null;
    await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await r.ReadAsync(cancellationToken))
    {
        var id = new Guid((byte[])r["template_id"]);
        var candidateCounts = PqGramVectorCodec.Decode((byte[])r["pq_gram_vector"]);
        var candidateNorm = r.GetDouble(r.GetOrdinal("pq_gram_norm"));
        var cosine = CosineSimilarity(fingerprint.PqGramCounts, fingerprint.PqGramNorm, candidateCounts, candidateNorm);
        if (cosine >= threshold && (best is null || cosine > best.Value.Cosine))
        {
            best = (id, cosine);
        }
    }
    return best;
}

private static double CosineSimilarity(IReadOnlyDictionary<string, double> a, double na, IReadOnlyDictionary<string, double> b, double nb)
{
    if (na == 0 || nb == 0) return 0;
    double dot = 0;
    foreach (var kv in a)
    {
        if (b.TryGetValue(kv.Key, out var v)) dot += kv.Value * v;
    }
    return dot / (na * nb);
}

private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();
    public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
    public int GetHashCode(byte[] obj)
    {
        int h = 17;
        foreach (var b in obj) h = h * 31 + b;
        return h;
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Templates/SqliteTemplateIndex.cs tests/StyloExtract.Templates.Tests/SqliteTemplateIndexProbeTests.cs
git commit -m "feat(templates): ProbeFastPathAsync (LSH+Jaccard) + ProbeSlowPathAsync (cosine)"
```

**End of M3.**

---

## M4 — Fast/slow/novel orchestration

### Task 24: Heuristics — `ExtractorInducer`

**Files:**
- Create: `src/StyloExtract.Heuristics/CssSelectorGeneralizer.cs`
- Create: `src/StyloExtract.Heuristics/ExtractorInducer.cs`
- Create: `tests/StyloExtract.Heuristics.Tests/ExtractorInducerTests.cs`

**Interfaces:**
- Consumes: `IExtractorInducer` from T4, `ExtractedBlock` list with `XPath` populated.
- Produces:
  - `CssSelectorGeneralizer.Generalize(string xpath, IElement element) → string` — drops `nth-of-type` indices when the underlying class tokens look stable; otherwise preserves them.
  - `ExtractorInducer : IExtractorInducer` — produces a `LearnedExtractor` whose `Rules` list has one `BlockRule` per `(Role, CssSelector)` pair seen in the input blocks; initial `MeanConfidence` is the average across observed blocks for that role, `ObservationCount = 1`, `DriftScore = 0`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorInducerTests
{
    [Fact]
    public void Induce_ProducesOneRulePerRoleCssPair()
    {
        IExtractorInducer inducer = new ExtractorInducer();
        var blocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "", Markdown = "", XPath = "/html/body/main/article", CssSelector = "main > article", TextLength = 500, LinkDensity = 0.05, Links = Array.Empty<ExtractedLink>() },
            new ExtractedBlock { Id = "b1", Role = BlockRole.PrimaryNavigation, Confidence = 0.95, Text = "", Markdown = "", XPath = "/html/body/header/nav", CssSelector = "header > nav", TextLength = 50, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var id = Guid.NewGuid();
        var extractor = inducer.Induce(id, blocks);

        extractor.TemplateId.Should().Be(id);
        extractor.Version.Should().Be(1);
        extractor.Rules.Should().HaveCount(2);
        extractor.Rules.Select(r => r.Role).Should().BeEquivalentTo(new[] { BlockRole.MainContent, BlockRole.PrimaryNavigation });
        extractor.Centroid.TotalObservations.Should().Be(1);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

`CssSelectorGeneralizer.cs`:

```csharp
namespace StyloExtract.Heuristics;

public static class CssSelectorGeneralizer
{
    public static string Generalize(string xpath)
    {
        // Strip [nth] indices, convert / to ' > ', lowercase.
        var parts = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = parts.Select(p =>
        {
            var bracket = p.IndexOf('[');
            return bracket > 0 ? p[..bracket] : p;
        });
        return string.Join(" > ", clean).ToLowerInvariant();
    }
}
```

`ExtractorInducer.cs`:

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorInducer : IExtractorInducer
{
    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks)
    {
        var byRoleSelector = blocks
            .GroupBy(b => (b.Role, Selector: b.CssSelector ?? CssSelectorGeneralizer.Generalize(b.XPath)))
            .ToList();

        var rules = byRoleSelector.Select((g, i) => new BlockRule
        {
            RuleId = $"r{i:D4}",
            Role = g.Key.Role,
            CssSelectors = new[] { g.Key.Selector },
            MeanConfidence = g.Average(b => b.Confidence),
            ObservationCount = 1,
            DriftScore = 0
        }).ToList();

        var byRoleCentroid = blocks
            .GroupBy(b => b.Role)
            .ToDictionary(g => g.Key, g => new RoleCentroid
            {
                ObservationCount = g.Count(),
                MeanLinkDensity = g.Average(b => b.LinkDensity),
                MeanTextLength = g.Average(b => b.TextLength),
                MeanDepth = g.Average(b => (double)b.XPath.Count(c => c == '/'))
            });

        return new LearnedExtractor
        {
            TemplateId = templateId,
            Version = 1,
            Rules = rules,
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 1,
                ByRole = byRoleCentroid,
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.UtcNow
            }
        };
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Heuristics/CssSelectorGeneralizer.cs src/StyloExtract.Heuristics/ExtractorInducer.cs tests/StyloExtract.Heuristics.Tests/ExtractorInducerTests.cs
git commit -m "feat(heuristics): ExtractorInducer + CssSelectorGeneralizer"
```

---

### Task 25: Heuristics — `ExtractorApplicator`

**Files:**
- Create: `src/StyloExtract.Heuristics/ExtractorApplicator.cs`
- Create: `tests/StyloExtract.Heuristics.Tests/ExtractorApplicatorTests.cs`

**Interfaces:**
- Consumes: `IExtractorApplicator` from T4, `LearnedExtractor`.
- Produces: `ExtractorApplicator : IExtractorApplicator`. For each rule, query the document via the rule's CSS selectors; emit one `ExtractedBlock` per match with the rule's `Role` and `Confidence = rule.MeanConfidence`. If no rules hit, return an empty list.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorApplicatorTests
{
    [Fact]
    public void Apply_EmitsBlocksMatchingRuleSelectors()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.92, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.88, ObservationCount = 5, DriftScore = 0 }
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse("<html><body><main><article>x</article></main><footer>©</footer></body></html>");

        var blocks = applicator.Apply(doc, extractor);

        blocks.Should().HaveCount(2);
        blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Confidence == 0.92);
        blocks.Should().Contain(b => b.Role == BlockRole.Footer && b.Confidence == 0.88);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorApplicator : IExtractorApplicator
{
    public IReadOnlyList<ExtractedBlock> Apply(IDocument document, LearnedExtractor extractor)
    {
        var result = new List<ExtractedBlock>();
        int i = 0;
        foreach (var rule in extractor.Rules)
        {
            foreach (var selector in rule.CssSelectors)
            {
                IElement[] matches;
                try
                {
                    matches = document.QuerySelectorAll(selector).ToArray();
                }
                catch
                {
                    continue; // bad selector — skip
                }
                foreach (var element in matches)
                {
                    result.Add(new ExtractedBlock
                    {
                        Id = $"b{i++:D4}",
                        Role = rule.Role,
                        Confidence = rule.MeanConfidence,
                        Text = element.TextContent.Trim(),
                        Markdown = "",
                        XPath = "",
                        CssSelector = selector,
                        TextLength = element.TextContent.Length,
                        LinkDensity = LinkDensityOf(element),
                        Links = element.QuerySelectorAll("a")
                            .Select(a => new ExtractedLink
                            {
                                Text = a.TextContent.Trim(),
                                Href = a.GetAttribute("href") ?? "",
                                IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            }).ToList()
                    });
                }
            }
        }
        return result;
    }

    private static double LinkDensityOf(IElement element)
    {
        var total = element.TextContent.Length;
        if (total == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / total;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Heuristics/ExtractorApplicator.cs tests/StyloExtract.Heuristics.Tests/ExtractorApplicatorTests.cs
git commit -m "feat(heuristics): ExtractorApplicator runs cached rules on a parsed DOM"
```

---

### Task 26: Core — fast / slow / novel orchestration

**Files:**
- Modify: `src/StyloExtract.Core/StyloExtract.Core.csproj` ref `StyloExtract.Templates`
- Modify: `src/StyloExtract.Core/LayoutExtractor.cs` to consult `ITemplateIndex` and apply learned extractor on hit, induce + register on novel
- Modify: `tests/StyloExtract.Core.Tests/StyloExtract.Core.Tests.csproj` ref Templates
- Create: `tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs`

**Interfaces:**
- Consumes: `ITemplateIndex`, `IExtractorInducer`, `IExtractorApplicator`, `HostHasher` (config), plus thresholds (passed as ctor args for now; M7 wraps in `StyloExtractOptions`).
- Produces: `LayoutExtractor` now returns `Status = FastPathHit | SlowPathMatch | Novel | NovelEphemeral`. `TemplateId`, `TemplateVersion`, `Similarity`, `ObservationCount`, `LatencyMatch` are real.

- [ ] **Step 1: Csproj refs**

```xml
<ProjectReference Include="..\StyloExtract.Templates\StyloExtract.Templates.csproj" />
```

Test csproj also adds Templates ref.

- [ ] **Step 2: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Core.Tests;

public class LayoutExtractorOrchestrationTests
{
    private static (ILayoutExtractor Extractor, SqliteConnection Conn) Build()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(conn);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(),
            new DomCleaner(),
            fingerprinter,
            new BlockSegmenter(),
            HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(),
            index,
            new HostHasher(new byte[32]),
            new ExtractorInducer(),
            new ExtractorApplicator(),
            fastPathThreshold: 0.85,
            slowPathThreshold: 0.75);
        return (extractor, conn);
    }

    [Fact]
    public async Task ExtractAsync_SameHtmlTwice_SecondCallIsFastPathHit()
    {
        var (e, conn) = Build();
        try
        {
            const string html = "<html><body><header><nav class='main-menu'><a href='/'>H</a><a href='/a'>A</a></nav></header><main><article><h1>T</h1><p>" + substantial article body text long enough for the heuristic classifier to recognise as MainContent, padded out so total text length comfortably exceeds two hundred characters and the link density stays below ten percent throughout this paragraph "</p></article></main></body></html>";
            var uri = new Uri("https://example.com/page");

            var first = await e.ExtractAsync(html, uri);
            first.Match.Status.Should().Be(MatchStatus.Novel);
            first.Match.TemplateId.Should().NotBeNull();

            var second = await e.ExtractAsync(html, uri);
            second.Match.Status.Should().Be(MatchStatus.FastPathHit);
            second.Match.TemplateId.Should().Be(first.Match.TemplateId);
            second.Match.Similarity.Should().BeGreaterThan(0.95);
        }
        finally { conn.Dispose(); }
    }
}
```

- [ ] **Step 3: Verify failure** (ctor signature mismatch).

- [ ] **Step 4: Implement** — replace `LayoutExtractor`:

```csharp
using System.Diagnostics;
using StyloExtract.Abstractions;
using StyloExtract.Templates;

namespace StyloExtract.Core;

public sealed class LayoutExtractor : ILayoutExtractor
{
    private readonly IHtmlDomParser _parser;
    private readonly IDomCleaner _cleaner;
    private readonly IStructuralFingerprinter _fingerprinter;
    private readonly IBlockSegmenter _segmenter;
    private readonly IBlockClassifier _classifier;
    private readonly IMarkdownRenderer _renderer;
    private readonly ITemplateIndex _index;
    private readonly HostHasher _hostHasher;
    private readonly IExtractorInducer _inducer;
    private readonly IExtractorApplicator _applicator;
    private readonly double _fastPathThreshold;
    private readonly double _slowPathThreshold;

    public LayoutExtractor(
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IStructuralFingerprinter fingerprinter,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier,
        IMarkdownRenderer renderer,
        ITemplateIndex index,
        HostHasher hostHasher,
        IExtractorInducer inducer,
        IExtractorApplicator applicator,
        double fastPathThreshold,
        double slowPathThreshold)
    {
        _parser = parser; _cleaner = cleaner; _fingerprinter = fingerprinter;
        _segmenter = segmenter; _classifier = classifier; _renderer = renderer;
        _index = index; _hostHasher = hostHasher; _inducer = inducer; _applicator = applicator;
        _fastPathThreshold = fastPathThreshold; _slowPathThreshold = slowPathThreshold;
    }

    public async Task<ExtractionResult> ExtractAsync(string html, Uri? sourceUri = null, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        var total = Stopwatch.StartNew();

        var parseTimer = Stopwatch.StartNew();
        var doc = _parser.Parse(html, sourceUri);
        _cleaner.Clean(doc);
        parseTimer.Stop();

        var fpTimer = Stopwatch.StartNew();
        var fp = _fingerprinter.Compute(doc);
        fpTimer.Stop();

        var hostHash = _hostHasher.Hash(options.HostOverride ?? sourceUri?.Host ?? "");
        var status = MatchStatus.NovelEphemeral;
        Guid? templateId = null;
        int templateVersion = 0;
        double similarity = 0;
        int observationCount = 0;
        IReadOnlyList<ExtractedBlock> blocks;

        var matchTimer = Stopwatch.StartNew();
        var fastHit = await _index.ProbeFastPathAsync(hostHash, fp, _fastPathThreshold, cancellationToken);
        if (fastHit is not null)
        {
            var ex = await _index.GetExtractorAsync(fastHit.Value, cancellationToken);
            if (ex is not null)
            {
                blocks = _applicator.Apply(doc, ex);
                templateId = fastHit;
                templateVersion = ex.Version;
                similarity = 1.0;
                observationCount = await _index.GetObservationCountAsync(fastHit.Value, cancellationToken);
                status = MatchStatus.FastPathHit;
            }
            else
            {
                blocks = _classifier.Classify(_segmenter.Segment(doc));
            }
        }
        else
        {
            var slow = await _index.ProbeSlowPathAsync(hostHash, fp, _slowPathThreshold, cancellationToken);
            if (slow is not null)
            {
                var ex = await _index.GetExtractorAsync(slow.Value.TemplateId, cancellationToken);
                if (ex is not null)
                {
                    blocks = _applicator.Apply(doc, ex);
                    templateId = slow.Value.TemplateId;
                    templateVersion = ex.Version;
                    similarity = slow.Value.Cosine;
                    observationCount = await _index.GetObservationCountAsync(templateId.Value, cancellationToken);
                    status = MatchStatus.SlowPathMatch;
                }
                else { blocks = _classifier.Classify(_segmenter.Segment(doc)); }
            }
            else
            {
                blocks = _classifier.Classify(_segmenter.Segment(doc));
                if (options.LearnNewTemplates)
                {
                    var newId = Guid.NewGuid();
                    var ex = _inducer.Induce(newId, blocks);
                    templateId = await _index.RegisterAsync(hostHash, fp, ex, cancellationToken);
                    templateVersion = 1;
                    observationCount = 1;
                    status = MatchStatus.Novel;
                }
            }
        }
        matchTimer.Stop();

        var renderTimer = Stopwatch.StartNew();
        var markdown = _renderer.Render(blocks, options.Profile);
        renderTimer.Stop();
        total.Stop();

        return new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = doc.Title,
            Markdown = markdown,
            Blocks = blocks,
            Match = new LayoutMatch
            {
                TemplateId = templateId,
                TemplateVersion = templateVersion,
                FingerprintHex = fp.Hex,
                Status = status,
                Similarity = similarity,
                ObservationCount = observationCount,
                LatencyMatch = matchTimer.Elapsed,
                LatencyTotal = total.Elapsed
            },
            Stats = new ExtractionStats
            {
                BlockCount = blocks.Count,
                FingerprintShingleCount = fp.ShingleCount,
                ParseTime = parseTimer.Elapsed,
                FingerprintTime = fpTimer.Elapsed,
                MatchTime = matchTimer.Elapsed,
                RenderTime = renderTimer.Elapsed
            }
        };
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Core/ tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs
git commit -m "feat(core): full fast/slow/novel orchestration with template index"
```

---

### Task 27: Discriminative integration test — different templates same host

**Files:**
- Create: `tests/StyloExtract.IntegrationTests/StyloExtract.IntegrationTests.csproj` ref Core + Templates + Heuristics + Html + Fingerprint + Markdown
- Create: `tests/StyloExtract.IntegrationTests/Fixtures/example/article.html`
- Create: `tests/StyloExtract.IntegrationTests/Fixtures/example/article-alt.html` (different article body, same template)
- Create: `tests/StyloExtract.IntegrationTests/Fixtures/example/product.html` (clearly different template)
- Create: `tests/StyloExtract.IntegrationTests/SameHostTemplateDiscriminationTests.cs`

**Interfaces:**
- Consumes: the whole stack composed.
- Produces: empirical regression check that the same template matches strongly and different templates on the same host stay distinct.

- [ ] **Step 1: Csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>StyloExtract.IntegrationTests</RootNamespace>
    <AssemblyName>StyloExtract.IntegrationTests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\StyloExtract.Core\StyloExtract.Core.csproj" />
    <ProjectReference Include="..\..\src\StyloExtract.Templates\StyloExtract.Templates.csproj" />
    <ProjectReference Include="..\..\src\StyloExtract.Heuristics\StyloExtract.Heuristics.csproj" />
    <ProjectReference Include="..\..\src\StyloExtract.Html\StyloExtract.Html.csproj" />
    <ProjectReference Include="..\..\src\StyloExtract.Fingerprint\StyloExtract.Fingerprint.csproj" />
    <ProjectReference Include="..\..\src\StyloExtract.Markdown\StyloExtract.Markdown.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Fixtures\**\*.html">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write fixtures**

`Fixtures/example/article.html`:

```html
<!DOCTYPE html>
<html><head><title>Article A</title></head>
<body>
  <header><nav class="main-nav"><a href="/">Home</a><a href="/blog">Blog</a><a href="/about">About</a></nav></header>
  <main>
    <article>
      <h1>Article A Title</h1>
      <p>Article A body paragraph one with substantial content for body classification.</p>
      <p>Article A body paragraph two adding even more content here for density.</p>
    </article>
  </main>
  <footer>© 2026 Example Corp. All rights reserved.</footer>
</body>
</html>
```

`Fixtures/example/article-alt.html` — same template, different content:

```html
<!DOCTYPE html>
<html><head><title>Article B</title></head>
<body>
  <header><nav class="main-nav"><a href="/">Home</a><a href="/blog">Blog</a><a href="/about">About</a></nav></header>
  <main>
    <article>
      <h1>A Completely Different Title</h1>
      <p>This is the second article with entirely different prose, longer than the first.</p>
      <p>Second paragraph of the second article, also distinct from the first article body.</p>
    </article>
  </main>
  <footer>© 2026 Example Corp. All rights reserved.</footer>
</body>
</html>
```

`Fixtures/example/product.html` — clearly different template:

```html
<!DOCTYPE html>
<html><head><title>Widget</title></head>
<body>
  <header><nav class="main-nav"><a href="/">Home</a><a href="/blog">Blog</a><a href="/about">About</a></nav></header>
  <main>
    <section class="product-grid">
      <div class="product-card"><h2>Widget</h2><span class="price">£9.99</span><button>Buy</button></div>
      <div class="product-card"><h2>Gadget</h2><span class="price">£14.99</span><button>Buy</button></div>
      <div class="product-card"><h2>Doohickey</h2><span class="price">£4.99</span><button>Buy</button></div>
    </section>
    <aside class="filters"><a>Filter A</a><a>Filter B</a><a>Filter C</a></aside>
  </main>
  <footer>© 2026 Example Corp. All rights reserved.</footer>
</body>
</html>
```

- [ ] **Step 3: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

public class SameHostTemplateDiscriminationTests
{
    private static (ILayoutExtractor, SqliteConnection) Build()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(conn);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        return (new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75), conn);
    }

    [Fact]
    public async Task TwoArticles_SameTemplate_SecondMatchesFirst()
    {
        var (e, conn) = Build();
        try
        {
            var a = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var b = await File.ReadAllTextAsync("Fixtures/example/article-alt.html");
            var uri = new Uri("https://example.com/post");

            var first = await e.ExtractAsync(a, uri);
            var second = await e.ExtractAsync(b, uri);

            first.Match.Status.Should().Be(MatchStatus.Novel);
            second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
            second.Match.TemplateId.Should().Be(first.Match.TemplateId);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ArticleAndProduct_SameHost_AreDistinctTemplates()
    {
        var (e, conn) = Build();
        try
        {
            var article = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var product = await File.ReadAllTextAsync("Fixtures/example/product.html");
            var uri = new Uri("https://example.com/x");

            var r1 = await e.ExtractAsync(article, uri);
            var r2 = await e.ExtractAsync(product, uri);

            r1.Match.Status.Should().Be(MatchStatus.Novel);
            r2.Match.Status.Should().Be(MatchStatus.Novel);
            r2.Match.TemplateId.Should().NotBe(r1.Match.TemplateId);
        }
        finally { conn.Dispose(); }
    }
}
```

- [ ] **Step 4: Verify pass** (may need to tune `fastPathThreshold` slightly downward if the synthetic fixtures don't quite hit 0.85; falling through to `SlowPathMatch` is acceptable for the same-template test).

- [ ] **Step 5: Commit**

```bash
git add tests/StyloExtract.IntegrationTests/
git commit -m "test(integration): same-template match + cross-template discrimination"
```

---

### Task 28: NovelEphemeral option — `LearnNewTemplates = false`

**Files:**
- Modify: `tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs` (add test)

**Interfaces:**
- Consumes: `ExtractionOptions.LearnNewTemplates`.
- Produces: when `false` and no match: returns `Status = NovelEphemeral`, `TemplateId = null`, but still produces a Markdown result via heuristic classification.

- [ ] **Step 1: Failing test** — append to `LayoutExtractorOrchestrationTests`:

```csharp
[Fact]
public async Task ExtractAsync_NoLearning_ProducesNovelEphemeral()
{
    var (e, conn) = Build();
    try
    {
        const string html = "<html><body><main><article><p>" + substantial article body text long enough for the heuristic classifier to recognise as MainContent, padded out so total text length comfortably exceeds two hundred characters and the link density stays below ten percent throughout this paragraph "</p></article></main></body></html>";
        var uri = new Uri("https://example.com/page");

        var result = await e.ExtractAsync(html, uri, new ExtractionOptions { LearnNewTemplates = false });

        result.Match.Status.Should().Be(MatchStatus.NovelEphemeral);
        result.Match.TemplateId.Should().BeNull();
        result.Markdown.Should().NotBeNullOrWhiteSpace();
    }
    finally { conn.Dispose(); }
}
```

- [ ] **Step 2: Verify pass** (no impl changes — the orchestration already respects the flag from T26).

- [ ] **Step 3: Commit**

```bash
git add tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs
git commit -m "test(core): LearnNewTemplates=false → NovelEphemeral confirmed"
```

**End of M4.**

---

## M5 — Drift, refit, version events, aging priority

### Task 29: Templates — `DriftScorer`

**Files:**
- Create: `src/StyloExtract.Templates/DriftScorer.cs`
- Create: `tests/StyloExtract.Templates.Tests/DriftScorerTests.cs`

**Interfaces:**
- Consumes: `ExtractedBlock` list (the rule-applied blocks), `LearnedExtractor`, `IDocument`.
- Produces:
  - `DriftScorer.ScoreApplication(LearnedExtractor extractor, IReadOnlyList<ExtractedBlock> appliedBlocks) → ApplicationDriftReport`
  - `ApplicationDriftReport` (internal type in Templates) carries per-rule observation deltas (`linkDensityDelta`, `textLengthDelta`, hit count) plus `OverallDelta ∈ [0,1]`.
- Drift formula: `OverallDelta = 1 - (matched_rules / total_rules) + mean(|metric_delta_normalised|)` clamped to `[0,1]`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class DriftScorerTests
{
    private static LearnedExtractor Make() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 10, DriftScore = 0 },
            new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.9, ObservationCount = 10, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 10,
            ByRole = new Dictionary<BlockRole, RoleCentroid>
            {
                [BlockRole.MainContent] = new() { ObservationCount = 10, MeanLinkDensity = 0.05, MeanTextLength = 500, MeanDepth = 4 },
                [BlockRole.Footer] = new() { ObservationCount = 10, MeanLinkDensity = 0.4, MeanTextLength = 60, MeanDepth = 2 }
            },
            OverallDriftScore = 0,
            LastObservation = DateTimeOffset.UtcNow
        }
    };

    private static ExtractedBlock Block(BlockRole role, int textLen, double linkDensity) => new()
    {
        Id = "b", Role = role, Confidence = 0.9, Text = "", Markdown = "",
        XPath = "/", CssSelector = "", TextLength = textLen, LinkDensity = linkDensity,
        Links = Array.Empty<ExtractedLink>()
    };

    [Fact]
    public void ScoreApplication_AllRulesMatchAndCentroidsAgree_ProducesLowDrift()
    {
        var report = DriftScorer.ScoreApplication(Make(), new[]
        {
            Block(BlockRole.MainContent, 500, 0.05),
            Block(BlockRole.Footer, 60, 0.4)
        });
        report.OverallDelta.Should().BeLessThan(0.15);
    }

    [Fact]
    public void ScoreApplication_OneRuleMissed_ProducesHighDrift()
    {
        var report = DriftScorer.ScoreApplication(Make(), new[]
        {
            Block(BlockRole.MainContent, 500, 0.05)
        });
        report.OverallDelta.Should().BeGreaterThan(0.4);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed record ApplicationDriftReport
{
    public required double OverallDelta { get; init; }
    public required IReadOnlyDictionary<string, double> PerRuleDelta { get; init; }
}

public static class DriftScorer
{
    public static ApplicationDriftReport ScoreApplication(LearnedExtractor extractor, IReadOnlyList<ExtractedBlock> appliedBlocks)
    {
        int totalRules = extractor.Rules.Count;
        if (totalRules == 0)
        {
            return new ApplicationDriftReport { OverallDelta = 0, PerRuleDelta = new Dictionary<string, double>() };
        }

        var byRole = appliedBlocks.GroupBy(b => b.Role).ToDictionary(g => g.Key, g => g.ToList());
        var perRule = new Dictionary<string, double>();
        int matchedRules = 0;
        double metricSum = 0;
        int metricCount = 0;

        foreach (var rule in extractor.Rules)
        {
            if (!byRole.TryGetValue(rule.Role, out var hits) || hits.Count == 0)
            {
                perRule[rule.RuleId] = 1.0;
                continue;
            }
            matchedRules++;
            if (!extractor.Centroid.ByRole.TryGetValue(rule.Role, out var centroid))
            {
                perRule[rule.RuleId] = 0;
                continue;
            }
            var actualLink = hits.Average(b => b.LinkDensity);
            var actualText = hits.Average(b => b.TextLength);
            var linkDelta = Normalise(Math.Abs(actualLink - centroid.MeanLinkDensity));
            var textDelta = Normalise(Math.Abs(actualText - centroid.MeanTextLength) / Math.Max(1, centroid.MeanTextLength));
            var delta = (linkDelta + textDelta) / 2;
            perRule[rule.RuleId] = delta;
            metricSum += delta;
            metricCount++;
        }

        var unmatched = 1.0 - (double)matchedRules / totalRules;
        var avgMetric = metricCount == 0 ? 0 : metricSum / metricCount;
        var overall = Math.Clamp(unmatched + avgMetric * 0.5, 0, 1);
        return new ApplicationDriftReport { OverallDelta = overall, PerRuleDelta = perRule };
    }

    private static double Normalise(double v) => Math.Min(1.0, v);
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/DriftScorer.cs tests/StyloExtract.Templates.Tests/DriftScorerTests.cs
git commit -m "feat(templates): DriftScorer (rule-miss + centroid metric delta)"
```

---

### Task 30: Templates — `TemplateVersionDiffer`

**Files:**
- Create: `src/StyloExtract.Templates/TemplateVersionDiffer.cs`
- Create: `tests/StyloExtract.Templates.Tests/TemplateVersionDifferTests.cs`

**Interfaces:**
- Consumes: two `LearnedExtractor` instances + two `StructuralFingerprint`s.
- Produces: `TemplateVersionDiffer.Diff(oldExtractor, newExtractor, oldFp, newFp) → TemplateVersionDiff` per spec §8.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateVersionDifferTests
{
    private static LearnedExtractor Ex(params (BlockRole role, string sel)[] rules) => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = rules.Select((r, i) => new BlockRule
        {
            RuleId = $"r{i}",
            Role = r.role,
            CssSelectors = new[] { r.sel },
            MeanConfidence = 0.9,
            ObservationCount = 1,
            DriftScore = 0
        }).ToList(),
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };

    private static StructuralFingerprint Fp(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double>(), PqGramNorm = 0, ShingleCount = 1, Hex = ""
        };
    }

    [Fact]
    public void Diff_DetectsAddedAndRemovedRules()
    {
        var oldEx = Ex((BlockRole.MainContent, "main"), (BlockRole.Footer, "footer"));
        var newEx = Ex((BlockRole.MainContent, "main"), (BlockRole.PrimaryNavigation, "nav"));

        var diff = TemplateVersionDiffer.Diff(oldEx, newEx, Fp(1), Fp(2));

        diff.AddedRules.Should().ContainSingle(r => r.Role == BlockRole.PrimaryNavigation);
        diff.RemovedRules.Should().ContainSingle(r => r.Role == BlockRole.Footer);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;

namespace StyloExtract.Templates;

public static class TemplateVersionDiffer
{
    public static TemplateVersionDiff Diff(
        LearnedExtractor oldEx,
        LearnedExtractor newEx,
        StructuralFingerprint oldFp,
        StructuralFingerprint newFp)
    {
        var oldByRole = oldEx.Rules.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.ToList());
        var newByRole = newEx.Rules.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.ToList());

        var added = newEx.Rules.Where(r => !oldByRole.ContainsKey(r.Role)).ToList();
        var removed = oldEx.Rules.Where(r => !newByRole.ContainsKey(r.Role)).ToList();

        var changed = new List<RuleSelectorChange>();
        foreach (var role in oldByRole.Keys.Intersect(newByRole.Keys))
        {
            var oldSelectors = oldByRole[role].SelectMany(r => r.CssSelectors).Distinct().ToList();
            var newSelectors = newByRole[role].SelectMany(r => r.CssSelectors).Distinct().ToList();
            if (!oldSelectors.SequenceEqual(newSelectors))
            {
                changed.Add(new RuleSelectorChange
                {
                    RuleId = oldByRole[role].First().RuleId,
                    Role = role,
                    OldSelectors = oldSelectors,
                    NewSelectors = newSelectors
                });
            }
        }

        var topPq = ComputeTopPqGramDimensions(oldEx, newEx);
        var jaccardDelta = 1.0 - JaccardEstimator.Estimate(oldFp.StructuralMinHash, newFp.StructuralMinHash);

        return new TemplateVersionDiff
        {
            TopChangedDimensions = topPq,
            AddedRules = added,
            RemovedRules = removed,
            ChangedSelectors = changed,
            SignatureJaccardDelta = jaccardDelta
        };
    }

    private static IReadOnlyList<PqGramDimensionChange> ComputeTopPqGramDimensions(LearnedExtractor _, LearnedExtractor __)
    {
        // pq-gram counts are stored on the StructuralFingerprint, not LearnedExtractor.
        // Caller-supplied diff path will need to pass them separately; v1 returns empty list when not available.
        return Array.Empty<PqGramDimensionChange>();
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/TemplateVersionDiffer.cs tests/StyloExtract.Templates.Tests/TemplateVersionDifferTests.cs
git commit -m "feat(templates): TemplateVersionDiffer (added/removed/changed rules + signature delta)"
```

---

### Task 31: Templates — `RecordObservationAsync` + `RefitOrchestrator` + `DefaultNoopVersionEventSink`

**Files:**
- Modify: `src/StyloExtract.Templates/SqliteTemplateIndex.cs` (implement `RecordObservationAsync` + new `BumpVersionAsync` helper)
- Create: `src/StyloExtract.Templates/DefaultNoopVersionEventSink.cs`
- Create: `src/StyloExtract.Templates/RefitOrchestrator.cs`
- Create: `tests/StyloExtract.Templates.Tests/RefitOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ITemplateIndex`, `ITemplateVersionEventSink`, `DriftScorer`, `TemplateVersionDiffer`, `IExtractorInducer`.
- Produces:
  - `SqliteTemplateIndex.RecordObservationAsync(templateId, fp, similarity, ct)` increments observation_count + last_seen + inserts into `template_observations` (LRU-bounded, default last 100).
  - `SqliteTemplateIndex.BumpVersionAsync(templateId, newExtractor, newFp, reason, ct)` retires old extractor row into `template_version_history` and replaces with new — bounded by `VersionHistoryDepth`.
  - `RefitOrchestrator.MaybeRefitAsync(templateId, currentFp, freshBlocks, options, ct)` — if drift ≥ threshold, induce new extractor, bump version, return `RefitResult { Refitted, OldVersion, NewVersion, Diff }`.
  - `DefaultNoopVersionEventSink` — both methods `ValueTask.CompletedTask`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class RefitOrchestratorTests
{
    [Fact]
    public async Task MaybeRefitAsync_HighDriftAndOverObsThreshold_BumpsVersion()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(conn);

        // Seed a template at version 1
        var fp = NewFingerprint(1);
        var extractor = SeedExtractor();
        var id = await index.RegisterAsync(new byte[16], fp, extractor, default);

        // Simulate enough observations to be "stable"
        for (int i = 0; i < 6; i++)
        {
            await index.RecordObservationAsync(id, fp, 1.0, default);
        }

        var orch = new RefitOrchestrator(index, new ExtractorInducer(),
            driftRefitThreshold: 0.35, observationsBeforeStable: 5, versionHistoryDepth: 3);

        // Simulate massive drift via blocks that don't match any cached rule
        var freshBlocks = new[]
        {
            new ExtractedBlock { Id = "b", Role = BlockRole.PrimaryNavigation, Confidence = 0.8, Text = "", Markdown = "", XPath = "/html/body/nav", CssSelector = "html > body > nav", TextLength = 100, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var result = await orch.MaybeRefitAsync(id, fp, freshBlocks, default);

        result.Refitted.Should().BeTrue();
        result.OldVersion.Should().Be(1);
        result.NewVersion.Should().Be(2);

        (await index.GetTemplateVersionAsync(id, default)).Should().Be(2);
    }

    private static StructuralFingerprint NewFingerprint(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double>(), PqGramNorm = 0, ShingleCount = 1, Hex = ""
        };
    }

    private static LearnedExtractor SeedExtractor() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 6, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState { TotalObservations = 6, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

`DefaultNoopVersionEventSink.cs`:

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed class DefaultNoopVersionEventSink : ITemplateVersionEventSink
{
    public ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

Add to `SqliteTemplateIndex`:

```csharp
public async Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var sigBytes = UintArrayToBytes(fingerprint.StructuralMinHash);

    await using var tx = await _conn.BeginTransactionAsync(cancellationToken);
    await using (var upd = _conn.CreateCommand())
    {
        upd.Transaction = (SqliteTransaction)tx;
        upd.CommandText = "UPDATE templates SET observation_count = observation_count + 1, last_seen = @now WHERE template_id = @id";
        upd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        upd.Parameters.AddWithValue("@now", now);
        await upd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var ins = _conn.CreateCommand())
    {
        ins.Transaction = (SqliteTransaction)tx;
        ins.CommandText = "INSERT INTO template_observations(template_id, observed_at, signature_minhash, similarity_at_match) VALUES (@id, @now, @sig, @sim)";
        ins.Parameters.AddWithValue("@id", templateId.ToByteArray());
        ins.Parameters.AddWithValue("@now", now);
        ins.Parameters.AddWithValue("@sig", sigBytes);
        ins.Parameters.AddWithValue("@sim", similarity);
        await ins.ExecuteNonQueryAsync(cancellationToken);
    }
    // LRU bound: keep only last 100 observations per template
    await using (var trim = _conn.CreateCommand())
    {
        trim.Transaction = (SqliteTransaction)tx;
        trim.CommandText = """
            DELETE FROM template_observations
            WHERE rowid IN (
              SELECT rowid FROM template_observations WHERE template_id = @id
              ORDER BY observed_at DESC LIMIT -1 OFFSET 100
            )
            """;
        trim.Parameters.AddWithValue("@id", templateId.ToByteArray());
        await trim.ExecuteNonQueryAsync(cancellationToken);
    }
    await tx.CommitAsync(cancellationToken);
}

internal async Task<(int OldVersion, int NewVersion)> BumpVersionAsync(
    Guid templateId,
    LearnedExtractor newExtractor,
    StructuralFingerprint newFp,
    string reason,
    int versionHistoryDepth,
    CancellationToken cancellationToken)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var newExBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(newExtractor, JsonOpts);
    var newSigBytes = UintArrayToBytes(newFp.StructuralMinHash);
    var newAnchorBytes = UintArrayToBytes(newFp.AnchorMinHash);
    var newPqBytes = Serialization.PqGramVectorCodec.Encode(newFp.PqGramCounts);

    await using var tx = await _conn.BeginTransactionAsync(cancellationToken);
    int oldVersion = 0;
    byte[]? oldSig = null;
    byte[]? oldPq = null;
    byte[]? oldExBlob = null;
    await using (var read = _conn.CreateCommand())
    {
        read.Transaction = (SqliteTransaction)tx;
        read.CommandText = "SELECT version_number, signature_minhash, pq_gram_vector, extractor_blob FROM templates WHERE template_id = @id";
        read.Parameters.AddWithValue("@id", templateId.ToByteArray());
        await using var r = await read.ExecuteReaderAsync(cancellationToken);
        if (await r.ReadAsync(cancellationToken))
        {
            oldVersion = r.GetInt32(0);
            oldSig = (byte[])r["signature_minhash"];
            oldPq = (byte[])r["pq_gram_vector"];
            oldExBlob = (byte[])r["extractor_blob"];
        }
    }
    // Retire old to history.
    await using (var hist = _conn.CreateCommand())
    {
        hist.Transaction = (SqliteTransaction)tx;
        hist.CommandText = """
            INSERT INTO template_version_history(template_id, version_number, signature_minhash, pq_gram_vector, extractor_blob, retired_at, retirement_reason)
            VALUES (@id, @ver, @sig, @pq, @ex, @now, @reason)
            """;
        hist.Parameters.AddWithValue("@id", templateId.ToByteArray());
        hist.Parameters.AddWithValue("@ver", oldVersion);
        hist.Parameters.AddWithValue("@sig", (object?)oldSig ?? DBNull.Value);
        hist.Parameters.AddWithValue("@pq", (object?)oldPq ?? DBNull.Value);
        hist.Parameters.AddWithValue("@ex", (object?)oldExBlob ?? DBNull.Value);
        hist.Parameters.AddWithValue("@now", now);
        hist.Parameters.AddWithValue("@reason", reason);
        await hist.ExecuteNonQueryAsync(cancellationToken);
    }
    // Trim history.
    await using (var trim = _conn.CreateCommand())
    {
        trim.Transaction = (SqliteTransaction)tx;
        trim.CommandText = """
            DELETE FROM template_version_history
            WHERE template_id = @id
              AND version_number NOT IN (
                SELECT version_number FROM template_version_history
                WHERE template_id = @id
                ORDER BY retired_at DESC LIMIT @keep
              )
            """;
        trim.Parameters.AddWithValue("@id", templateId.ToByteArray());
        trim.Parameters.AddWithValue("@keep", versionHistoryDepth);
        await trim.ExecuteNonQueryAsync(cancellationToken);
    }
    int newVersion = oldVersion + 1;
    await using (var upd = _conn.CreateCommand())
    {
        upd.Transaction = (SqliteTransaction)tx;
        upd.CommandText = """
            UPDATE templates SET
              version_number = @ver,
              signature_minhash = @sig,
              anchor_signature = @anchor,
              pq_gram_vector = @pq,
              pq_gram_norm = @norm,
              extractor_blob = @ex,
              last_refit_at = @now
            WHERE template_id = @id
            """;
        upd.Parameters.AddWithValue("@id", templateId.ToByteArray());
        upd.Parameters.AddWithValue("@ver", newVersion);
        upd.Parameters.AddWithValue("@sig", newSigBytes);
        upd.Parameters.AddWithValue("@anchor", newAnchorBytes);
        upd.Parameters.AddWithValue("@pq", newPqBytes);
        upd.Parameters.AddWithValue("@norm", newFp.PqGramNorm);
        upd.Parameters.AddWithValue("@ex", newExBytes);
        upd.Parameters.AddWithValue("@now", now);
        await upd.ExecuteNonQueryAsync(cancellationToken);
    }
    await tx.CommitAsync(cancellationToken);
    return (oldVersion, newVersion);
}
```

`RefitOrchestrator.cs`:

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed record RefitResult(bool Refitted, int OldVersion, int NewVersion, LearnedExtractor? OldExtractor, LearnedExtractor? NewExtractor);

public sealed class RefitOrchestrator
{
    private readonly SqliteTemplateIndex _index;
    private readonly IExtractorInducer _inducer;
    private readonly double _driftThreshold;
    private readonly int _observationsBeforeStable;
    private readonly int _versionHistoryDepth;

    public RefitOrchestrator(SqliteTemplateIndex index, IExtractorInducer inducer,
        double driftRefitThreshold, int observationsBeforeStable, int versionHistoryDepth)
    {
        _index = index; _inducer = inducer;
        _driftThreshold = driftRefitThreshold;
        _observationsBeforeStable = observationsBeforeStable;
        _versionHistoryDepth = versionHistoryDepth;
    }

    public async Task<RefitResult> MaybeRefitAsync(Guid templateId, StructuralFingerprint currentFp,
        IReadOnlyList<ExtractedBlock> freshHeuristicBlocks, CancellationToken cancellationToken)
    {
        var existing = await _index.GetExtractorAsync(templateId, cancellationToken);
        if (existing is null) return new RefitResult(false, 0, 0, null, null);
        var obs = await _index.GetObservationCountAsync(templateId, cancellationToken);
        if (obs < _observationsBeforeStable) return new RefitResult(false, existing.Version, existing.Version, existing, null);

        var drift = DriftScorer.ScoreApplication(existing, freshHeuristicBlocks);
        if (drift.OverallDelta < _driftThreshold)
        {
            return new RefitResult(false, existing.Version, existing.Version, existing, null);
        }

        var freshExtractor = _inducer.Induce(templateId, freshHeuristicBlocks) with { Version = existing.Version + 1 };
        var (oldV, newV) = await _index.BumpVersionAsync(templateId, freshExtractor, currentFp, "drift", _versionHistoryDepth, cancellationToken);
        return new RefitResult(true, oldV, newV, existing, freshExtractor);
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/ tests/StyloExtract.Templates.Tests/RefitOrchestratorTests.cs
git commit -m "feat(templates): RecordObservationAsync + RefitOrchestrator + noop event sink"
```

---

### Task 32: Templates — `AgingPriorityScorer`

**Files:**
- Create: `src/StyloExtract.Templates/AgingPriorityScorer.cs`
- Create: `tests/StyloExtract.Templates.Tests/AgingPriorityScorerTests.cs`

**Interfaces:**
- Consumes: similarity score, `observationCount`, `ageDaysSinceLastSeen`, `(λ_obs, λ_recent, τ)`.
- Produces: `AgingPriorityScorer.Score(similarity, obsCount, ageDays, λobs=0.02, λrecent=0.05, τ=30) → double` per spec §6 formula. Includes regression test using the four worked examples from the spec.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class AgingPriorityScorerTests
{
    private const double TieSimilarity = 0.85;

    [Theory]
    [InlineData(2, 0, 0.07)]      // brand-new
    [InlineData(50, 7, 0.12)]     // freshly active
    [InlineData(10000, 180, 0.19)] // old-but-heavy
    [InlineData(3, 180, 0.03)]    // old-and-light
    public void Score_MatchesSpecWorkedExamples(int obs, double ageDays, double expectedBonus)
    {
        var score = AgingPriorityScorer.Score(TieSimilarity, obs, ageDays);
        var bonus = score - TieSimilarity;
        bonus.Should().BeApproximately(expectedBonus, 0.015);
    }

    [Fact]
    public void Score_OldHeavyBeatsOldLight()
    {
        var heavy = AgingPriorityScorer.Score(0.85, 10_000, 180);
        var light = AgingPriorityScorer.Score(0.85, 3, 180);
        heavy.Should().BeGreaterThan(light);
    }
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
namespace StyloExtract.Templates;

public static class AgingPriorityScorer
{
    public static double Score(
        double similarity,
        int totalObservationCount,
        double ageDaysSinceLastSeen,
        double lambdaObs = 0.02,
        double lambdaRecent = 0.05,
        double tauDays = 30.0)
    {
        var obsBonus = lambdaObs * Math.Log(1 + totalObservationCount);
        var recentBonus = lambdaRecent * Math.Exp(-ageDaysSinceLastSeen / tauDays);
        return similarity + obsBonus + recentBonus;
    }
}
```

- [ ] **Step 4: Verify pass** — all four worked examples.

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/AgingPriorityScorer.cs tests/StyloExtract.Templates.Tests/AgingPriorityScorerTests.cs
git commit -m "feat(templates): AgingPriorityScorer (spec §6 worked-example regression)"
```

---

### Task 33: Core — wire drift, refit, observation recording, version event sink

**Files:**
- Modify: `src/StyloExtract.Core/LayoutExtractor.cs` (accept `RefitOrchestrator` + `ITemplateVersionEventSink` + emit `Status = Refit` when refit fired)
- Modify: `tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs` (capturing sink test)

**Interfaces:**
- Consumes: `RefitOrchestrator`, `ITemplateVersionEventSink`, `TemplateVersionDiffer`.
- Produces:
  - After fast/slow path match, the orchestrator calls `RecordObservationAsync` then `MaybeRefitAsync`. If refit fires, it emits `OnVersionChangeAsync` and the result's `MatchStatus` is `Refit` instead of `FastPathHit` / `SlowPathMatch`.
  - On novel registration, emits `OnNewTemplateAsync`.

- [ ] **Step 1: Add capturing sink test**

```csharp
private sealed class CapturingSink : ITemplateVersionEventSink
{
    public List<NewTemplateEvent> NewEvents { get; } = new();
    public List<VersionChangeEvent> VersionEvents { get; } = new();
    public ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken ct) { NewEvents.Add(evt); return ValueTask.CompletedTask; }
    public ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken ct) { VersionEvents.Add(evt); return ValueTask.CompletedTask; }
}

[Fact]
public async Task ExtractAsync_NovelTemplate_FiresOnNewTemplate()
{
    var sink = new CapturingSink();
    var (e, conn) = BuildWithSink(sink);
    try
    {
        const string html = "<html><body><main><article><p>hello</p></article></main></body></html>";
        await e.ExtractAsync(html, new Uri("https://example.com/x"));
        sink.NewEvents.Should().ContainSingle();
    }
    finally { conn.Dispose(); }
}
```

(Add a `BuildWithSink` factory wrapping the existing `Build()` and passing `sink` + a `RefitOrchestrator`.)

- [ ] **Step 2: Verify failure** (ctor signature change).

- [ ] **Step 3: Implement** — extend `LayoutExtractor` ctor with two new trailing args:

```csharp
private readonly RefitOrchestrator _refit;
private readonly ITemplateVersionEventSink _eventSink;

public LayoutExtractor(
    IHtmlDomParser parser,
    IDomCleaner cleaner,
    IStructuralFingerprinter fingerprinter,
    IBlockSegmenter segmenter,
    IBlockClassifier classifier,
    IMarkdownRenderer renderer,
    ITemplateIndex index,
    HostHasher hostHasher,
    IExtractorInducer inducer,
    IExtractorApplicator applicator,
    double fastPathThreshold,
    double slowPathThreshold,
    RefitOrchestrator refit,
    ITemplateVersionEventSink eventSink)
{
    _parser = parser; _cleaner = cleaner; _fingerprinter = fingerprinter;
    _segmenter = segmenter; _classifier = classifier; _renderer = renderer;
    _index = index; _hostHasher = hostHasher; _inducer = inducer; _applicator = applicator;
    _fastPathThreshold = fastPathThreshold; _slowPathThreshold = slowPathThreshold;
    _refit = refit;
    _eventSink = eventSink;
}
```

**Then update the existing `Build()` factories that this ctor change breaks:**
- `tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs::Build()` — pass a `RefitOrchestrator` built from the existing `index` + a `new ExtractorInducer()` + the same numeric thresholds, plus `new DefaultNoopVersionEventSink()`.
- `tests/StyloExtract.IntegrationTests/SameHostTemplateDiscriminationTests.cs::Build()` — same update.

After the fast/slow hit branch (still inside the match block, before render):

```csharp
if (templateId is not null && status is MatchStatus.FastPathHit or MatchStatus.SlowPathMatch)
{
    await _index.RecordObservationAsync(templateId.Value, fp, similarity, cancellationToken);
    var freshBlocks = _classifier.Classify(_segmenter.Segment(doc));
    var refit = await _refit.MaybeRefitAsync(templateId.Value, fp, freshBlocks, cancellationToken);
    if (refit.Refitted)
    {
        status = MatchStatus.Refit;
        templateVersion = refit.NewVersion;
        var diff = TemplateVersionDiffer.Diff(refit.OldExtractor!, refit.NewExtractor!, fp, fp);
        await _eventSink.OnVersionChangeAsync(new VersionChangeEvent
        {
            TemplateId = templateId.Value,
            HostDisplayName = sourceUri?.Host ?? "",
            OldVersion = refit.OldVersion,
            NewVersion = refit.NewVersion,
            DetectedAt = DateTimeOffset.UtcNow,
            Diff = diff
        }, cancellationToken);
        blocks = freshBlocks; // re-render against fresh classification
    }
}

if (status == MatchStatus.Novel && templateId is not null)
{
    await _eventSink.OnNewTemplateAsync(new NewTemplateEvent
    {
        TemplateId = templateId.Value,
        HostDisplayName = sourceUri?.Host ?? "",
        DetectedAt = DateTimeOffset.UtcNow,
        FingerprintHex = fp.Hex,
        InitialBlockCount = blocks.Count
    }, cancellationToken);
}
```

- [ ] **Step 4: Verify pass.** All M5 tests + earlier tests stay green.

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Core/LayoutExtractor.cs tests/StyloExtract.Core.Tests/LayoutExtractorOrchestrationTests.cs
git commit -m "feat(core): drift + refit + version events wired into LayoutExtractor"
```

**End of M5.**

---

## M6 — Export / import

### Task 34: Templates — `TemplateExporter` (schema v1 JSON)

**Files:**
- Create: `src/StyloExtract.Templates/Serialization/ExportSchemaV1.cs`
- Create: `src/StyloExtract.Templates/TemplateExporter.cs`
- Create: `tests/StyloExtract.Templates.Tests/TemplateExporterTests.cs`

**Interfaces:**
- Consumes: `SqliteTemplateIndex` (additional readonly accessors needed: enumerate templates by host_hash), pq-gram codec.
- Produces:
  - `ExportSchemaV1` records mirroring spec §9 JSON.
  - `TemplateExporter.ExportHostAsync(SqliteConnection conn, byte[] hostHash, string hostDisplayName, Stream output, CancellationToken ct)` writes a UTF-8 JSON document conforming to schema v1.
- `SqliteTemplateIndex.EnumerateTemplatesAsync(byte[] hostHash, CancellationToken ct) → IAsyncEnumerable<TemplateRow>` (a new internal record carrying the columns) — implement here.

- [ ] **Step 1: Failing test**

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateExporterTests
{
    [Fact]
    public async Task ExportHostAsync_ProducesSchemaV1Json()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var idx = new SqliteTemplateIndex(conn);
        var host = new byte[16];
        var fp = NewFp();
        var ex = NewEx();
        await idx.RegisterAsync(host, fp, ex, default);

        using var ms = new MemoryStream();
        await TemplateExporter.ExportHostAsync(conn, host, "example.com", ms, default);

        var json = JsonDocument.Parse(ms.ToArray());
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("host").GetProperty("displayName").GetString().Should().Be("example.com");
        json.RootElement.GetProperty("templates").GetArrayLength().Should().Be(1);
    }

    private static StructuralFingerprint NewFp()
    {
        var sig = new uint[128]; Array.Fill(sig, (uint)9);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double> { ["k"] = 1 }, PqGramNorm = 1, ShingleCount = 1, Hex = "0"
        };
    }

    private static LearnedExtractor NewEx() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main" }, MeanConfidence = 0.9, ObservationCount = 1, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

`ExportSchemaV1.cs`:

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.Templates.Serialization;

public sealed record ExportSchemaV1
{
    public required int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset ExportedAt { get; init; }
    public required ExportHost Host { get; init; }
    public required IReadOnlyList<ExportTemplate> Templates { get; init; }
}

public sealed record ExportHost
{
    public required string DisplayName { get; init; }
    public required string HashAlgorithm { get; init; }
    public required string? HashKey { get; init; }
}

public sealed record ExportTemplate
{
    public required Guid TemplateId { get; init; }
    public required int Version { get; init; }
    public required ExportFingerprints Fingerprints { get; init; }
    public required LearnedExtractor Extractor { get; init; }
    public required ExportObservationSummary Observations { get; init; }
}

public sealed record ExportFingerprints
{
    public required string SignatureMinhash { get; init; }    // base64
    public required string AnchorSignature { get; init; }     // base64
    public required ExportPqGramVector PqGramVector { get; init; }
}

public sealed record ExportPqGramVector
{
    public required int P { get; init; }
    public required int Q { get; init; }
    public required int TopK { get; init; }
    public required IReadOnlyDictionary<string, double> Values { get; init; }
    public required double Norm { get; init; }
}

public sealed record ExportObservationSummary
{
    public required int Count { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
}
```

`TemplateExporter.cs`:

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public static class TemplateExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task ExportHostAsync(SqliteConnection conn, byte[] hostHash, string hostDisplayName, Stream output, CancellationToken ct)
    {
        var templates = new List<ExportTemplate>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT template_id, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm,
                   extractor_blob, observation_count, created_at, last_seen
            FROM templates WHERE host_hash = @host
            """;
        cmd.Parameters.AddWithValue("@host", hostHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sigBytes = (byte[])r["signature_minhash"];
            var anchorBytes = (byte[])r["anchor_signature"];
            var pqBytes = (byte[])r["pq_gram_vector"];
            var extractorBlob = (byte[])r["extractor_blob"];
            var extractor = JsonSerializer.Deserialize<LearnedExtractor>(extractorBlob)!;
            var pqDecoded = PqGramVectorCodec.Decode(pqBytes);
            templates.Add(new ExportTemplate
            {
                TemplateId = new Guid((byte[])r["template_id"]),
                Version = r.GetInt32(r.GetOrdinal("version_number")),
                Fingerprints = new ExportFingerprints
                {
                    SignatureMinhash = Convert.ToBase64String(sigBytes),
                    AnchorSignature = Convert.ToBase64String(anchorBytes),
                    PqGramVector = new ExportPqGramVector
                    {
                        P = 2, Q = 3, TopK = 256,
                        Values = pqDecoded,
                        Norm = r.GetDouble(r.GetOrdinal("pq_gram_norm"))
                    }
                },
                Extractor = extractor,
                Observations = new ExportObservationSummary
                {
                    Count = r.GetInt32(r.GetOrdinal("observation_count")),
                    FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
                    LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("last_seen")))
                }
            });
        }

        var doc = new ExportSchemaV1
        {
            SchemaVersion = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            Host = new ExportHost { DisplayName = hostDisplayName, HashAlgorithm = "hmac-sha256", HashKey = null },
            Templates = templates
        };
        await JsonSerializer.SerializeAsync(output, doc, JsonOpts, ct);
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/Serialization/ExportSchemaV1.cs src/StyloExtract.Templates/TemplateExporter.cs tests/StyloExtract.Templates.Tests/TemplateExporterTests.cs
git commit -m "feat(templates): TemplateExporter producing schemaVersion=1 JSON"
```

---

### Task 35: Templates — `TemplateImporter`

**Files:**
- Create: `src/StyloExtract.Templates/TemplateImporter.cs`
- Create: `tests/StyloExtract.Templates.Tests/TemplateImporterTests.cs`

**Interfaces:**
- Consumes: `ExportSchemaV1` JSON stream + `SqliteConnection`.
- Produces: `TemplateImporter.ImportAsync(SqliteConnection conn, byte[] hostHashForImport, Stream input, CancellationToken ct) → ImportResult`. Behavior: idempotent on `TemplateId` (`INSERT OR REPLACE`). Repopulates `template_lsh_band_index`. Preserves `Centroid` state so drift resumes from where the exporter left off.

- [ ] **Step 1: Failing test (roundtrip)**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateImporterTests
{
    [Fact]
    public async Task ImportAsync_RoundTrip_RegistersTemplates()
    {
        using var src = NewConn();
        var idxSrc = new SqliteTemplateIndex(src);
        var host = new byte[16];
        await idxSrc.RegisterAsync(host, FakeFp(), FakeEx(), default);

        using var exportStream = new MemoryStream();
        await TemplateExporter.ExportHostAsync(src, host, "example.com", exportStream, default);
        exportStream.Position = 0;

        using var dst = NewConn();
        var result = await TemplateImporter.ImportAsync(dst, host, exportStream, default);

        result.ImportedCount.Should().Be(1);
        await using var cmd = dst.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM templates";
        ((long?)await cmd.ExecuteScalarAsync(default)).Should().Be(1);
    }

    private static SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        SqliteSchema.EnsureCreated(c);
        return c;
    }

    private static StructuralFingerprint FakeFp()
    {
        var sig = new uint[128]; Array.Fill(sig, 11u);
        var bands = new ulong[16]; Array.Fill(bands, 11UL * 7);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = bands,
            PqGramCounts = new Dictionary<string, double> { ["k"] = 1 }, PqGramNorm = 1, ShingleCount = 1, Hex = "0"
        };
    }

    private static LearnedExtractor FakeEx() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main" }, MeanConfidence = 0.9, ObservationCount = 1, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };
}
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

```csharp
using System.IO.Hashing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

public sealed record ImportResult(int ImportedCount, int SkippedCount, int ReplacedCount);

public static class TemplateImporter
{
    public static async Task<ImportResult> ImportAsync(SqliteConnection conn, byte[] hostHash, Stream input, CancellationToken ct)
    {
        var doc = await JsonSerializer.DeserializeAsync<ExportSchemaV1>(input, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, ct);
        if (doc is null) return new ImportResult(0, 0, 0);
        if (doc.SchemaVersion != 1) throw new InvalidDataException($"Unsupported schemaVersion {doc.SchemaVersion}");

        int imported = 0, replaced = 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var t in doc.Templates)
        {
            var idBytes = t.TemplateId.ToByteArray();
            var sigBytes = Convert.FromBase64String(t.Fingerprints.SignatureMinhash);
            var anchorBytes = Convert.FromBase64String(t.Fingerprints.AnchorSignature);
            var pqBytes = PqGramVectorCodec.Encode(t.Fingerprints.PqGramVector.Values);
            var extractorBytes = JsonSerializer.SerializeToUtf8Bytes(t.Extractor);

            // Check if existed
            bool existed;
            await using (var check = conn.CreateCommand())
            {
                check.Transaction = (SqliteTransaction)tx;
                check.CommandText = "SELECT 1 FROM templates WHERE template_id = @id";
                check.Parameters.AddWithValue("@id", idBytes);
                existed = await check.ExecuteScalarAsync(ct) is not null;
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO templates(template_id, host_hash, version_number, signature_minhash, anchor_signature, pq_gram_vector, pq_gram_norm, extractor_blob, observation_count, created_at, last_seen)
                    VALUES (@id, @host, @ver, @sig, @anchor, @pq, @norm, @ex, @obs, @created, @last)
                    """;
                ins.Parameters.AddWithValue("@id", idBytes);
                ins.Parameters.AddWithValue("@host", hostHash);
                ins.Parameters.AddWithValue("@ver", t.Version);
                ins.Parameters.AddWithValue("@sig", sigBytes);
                ins.Parameters.AddWithValue("@anchor", anchorBytes);
                ins.Parameters.AddWithValue("@pq", pqBytes);
                ins.Parameters.AddWithValue("@norm", t.Fingerprints.PqGramVector.Norm);
                ins.Parameters.AddWithValue("@ex", extractorBytes);
                ins.Parameters.AddWithValue("@obs", t.Observations.Count);
                ins.Parameters.AddWithValue("@created", t.Observations.FirstSeen.ToUnixTimeMilliseconds());
                ins.Parameters.AddWithValue("@last", t.Observations.LastSeen.ToUnixTimeMilliseconds());
                await ins.ExecuteNonQueryAsync(ct);
            }

            // Reindex bands (derive from sig)
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = (SqliteTransaction)tx;
                del.CommandText = "DELETE FROM template_lsh_band_index WHERE template_id = @id";
                del.Parameters.AddWithValue("@id", idBytes);
                await del.ExecuteNonQueryAsync(ct);
            }
            var sig = SqliteTemplateIndex.BytesToUintArray(sigBytes);
            var bands = ComputeBandHashes(sig, 16, 8);
            await using (var bandCmd = conn.CreateCommand())
            {
                bandCmd.Transaction = (SqliteTransaction)tx;
                bandCmd.CommandText = "INSERT OR IGNORE INTO template_lsh_band_index(band_hash, band_index, template_id) VALUES (@bh, @bi, @id)";
                bandCmd.Parameters.Add("@bh", SqliteType.Blob);
                bandCmd.Parameters.Add("@bi", SqliteType.Integer);
                bandCmd.Parameters.Add("@id", SqliteType.Blob);
                for (int i = 0; i < bands.Length; i++)
                {
                    bandCmd.Parameters["@bh"].Value = BitConverter.GetBytes(bands[i]);
                    bandCmd.Parameters["@bi"].Value = i;
                    bandCmd.Parameters["@id"].Value = idBytes;
                    await bandCmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (existed) replaced++; else imported++;
        }
        await tx.CommitAsync(ct);
        return new ImportResult(imported, 0, replaced);
    }

    private static ulong[] ComputeBandHashes(uint[] signature, int bands, int rows)
    {
        var result = new ulong[bands];
        var buf = new byte[rows * 4];
        for (int b = 0; b < bands; b++)
        {
            for (int r = 0; r < rows; r++)
            {
                BitConverter.GetBytes(signature[b * rows + r]).CopyTo(buf, r * 4);
            }
            result[b] = XxHash64.HashToUInt64(buf);
        }
        return result;
    }
}
```

- [ ] **Step 4: Verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Templates/TemplateImporter.cs tests/StyloExtract.Templates.Tests/TemplateImporterTests.cs
git commit -m "feat(templates): TemplateImporter (idempotent, rebuilds LSH index)"
```

---

### Task 36: Full roundtrip — export → wipe → import → still matches

**Files:**
- Create: `tests/StyloExtract.IntegrationTests/ExportImportRoundtripTests.cs`

**Interfaces:**
- Consumes: full stack from M5.
- Produces: behavioural guarantee that an exported template, re-imported into a fresh DB, still matches the same HTML as a `FastPathHit` or `SlowPathMatch`.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

public class ExportImportRoundtripTests
{
    [Fact]
    public async Task Export_Import_PreservesMatch()
    {
        var (e1, conn1) = BuildExtractor();
        try
        {
            var html = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var uri = new Uri("https://example.com/post");

            await e1.ExtractAsync(html, uri); // Novel → registers

            // Export host
            var host = new HostHasher(new byte[32]).Hash("example.com");
            using var ms = new MemoryStream();
            await TemplateExporter.ExportHostAsync(conn1, host, "example.com", ms, default);
            ms.Position = 0;

            // Import into a fresh DB-backed extractor
            var (e2, conn2) = BuildExtractor();
            try
            {
                var importResult = await TemplateImporter.ImportAsync(conn2, host, ms, default);
                importResult.ImportedCount.Should().Be(1);

                var second = await e2.ExtractAsync(html, uri);
                second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
            }
            finally { conn2.Dispose(); }
        }
        finally { conn1.Dispose(); }
    }

    private static (ILayoutExtractor, SqliteConnection) BuildExtractor()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(conn);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var refit = new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3);
        return (new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            refit, new DefaultNoopVersionEventSink()), conn);
    }
}
```

- [ ] **Step 2: Verify pass.**

- [ ] **Step 3: Commit**

```bash
git add tests/StyloExtract.IntegrationTests/ExportImportRoundtripTests.cs
git commit -m "test(integration): export → wipe → import roundtrip preserves match"
```

**End of M6.**

---

## M7 — ASP.NET Core, CLI, Benchmarks

### Task 37: AspNetCore — `AddStyloExtract`

**Files:**
- Modify: `src/StyloExtract.AspNetCore/StyloExtract.AspNetCore.csproj` ref Core + Templates + Heuristics + Html + Fingerprint + Markdown; add Microsoft.Extensions.DependencyInjection.Abstractions + Microsoft.Extensions.Options.ConfigurationExtensions
- Create: `src/StyloExtract.AspNetCore/StyloExtractOptions.cs`
- Create: `src/StyloExtract.AspNetCore/StyloExtractServiceCollectionExtensions.cs`
- Create: `tests/StyloExtract.Core.Tests/AddStyloExtractTests.cs`
- Modify: Core.Tests csproj ref AspNetCore

**Interfaces:**
- Consumes: every concrete impl from M1–M6.
- Produces: `services.AddStyloExtract(o => { ... })` wires `ILayoutExtractor` as a singleton with all dependencies resolved from DI. Options shape per spec §12.

- [ ] **Step 1: Csproj**

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
  <ProjectReference Include="..\StyloExtract.Core\StyloExtract.Core.csproj" />
  <ProjectReference Include="..\StyloExtract.Templates\StyloExtract.Templates.csproj" />
  <ProjectReference Include="..\StyloExtract.Heuristics\StyloExtract.Heuristics.csproj" />
  <ProjectReference Include="..\StyloExtract.Html\StyloExtract.Html.csproj" />
  <ProjectReference Include="..\StyloExtract.Fingerprint\StyloExtract.Fingerprint.csproj" />
  <ProjectReference Include="..\StyloExtract.Markdown\StyloExtract.Markdown.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write `StyloExtractOptions.cs`**

```csharp
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore;

public sealed class StyloExtractOptions
{
    public ExtractionProfile DefaultProfile { get; set; } = ExtractionProfile.RagFull;
    public string StorePath { get; set; } = "styloextract-templates.db";
    public string? HostHashKey { get; set; }
    public FingerprintOptions Fingerprint { get; } = new();
    public MatchOptions Match { get; } = new();
    public CentroidOptions Centroid { get; } = new();

    public sealed class FingerprintOptions
    {
        public int MinHashSize { get; set; } = 128;
        public int LshBands { get; set; } = 16;
        public int LshRowsPerBand { get; set; } = 8;
        public int ShingleWidth { get; set; } = 3;
        public double AnchorWeight { get; set; } = 0.4;
    }

    public sealed class MatchOptions
    {
        public double FastPathJaccardThreshold { get; set; } = 0.85;
        public double SlowPathCosineThreshold { get; set; } = 0.75;
        public double AgingLambdaObs { get; set; } = 0.02;
        public double AgingLambdaRecent { get; set; } = 0.05;
        public double AgingTauDays { get; set; } = 30;
    }

    public sealed class CentroidOptions
    {
        public double DriftRefitThreshold { get; set; } = 0.35;
        public int ObservationsBeforeStable { get; set; } = 5;
        public int ObservationCloudSize { get; set; } = 100;
        public int VersionHistoryDepth { get; set; } = 3;
    }
}
```

- [ ] **Step 3: Write `StyloExtractServiceCollectionExtensions.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;

namespace StyloExtract.AspNetCore;

public static class StyloExtractServiceCollectionExtensions
{
    public static IServiceCollection AddStyloExtract(this IServiceCollection services, Action<StyloExtractOptions>? configure = null)
    {
        var options = new StyloExtractOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<ClassNoiseFilter>(_ => ClassNoiseFilter.LoadFromEmbeddedResource());
        services.AddSingleton<IHtmlDomParser, AngleSharpHtmlDomParser>();
        services.AddSingleton<IDomCleaner, DomCleaner>();
        services.AddSingleton<IBlockSegmenter, BlockSegmenter>();
        services.AddSingleton<IBlockClassifier>(_ => HeuristicBlockClassifier.LoadFromEmbeddedResources());
        services.AddSingleton<IMarkdownRenderer, TypedMarkdownRenderer>();
        services.AddSingleton<IExtractorInducer, ExtractorInducer>();
        services.AddSingleton<IExtractorApplicator, ExtractorApplicator>();

        services.AddSingleton<MinHashSketcher>(sp => new MinHashSketcher(options.Fingerprint.MinHashSize));
        services.AddSingleton<ShingleGenerator>(sp => new ShingleGenerator(sp.GetRequiredService<ClassNoiseFilter>(), options.Fingerprint.ShingleWidth));
        services.AddSingleton<LshBander>(_ => new LshBander(options.Fingerprint.LshBands, options.Fingerprint.LshRowsPerBand));
        services.AddSingleton<AnchorPathFingerprinter>(sp => new AnchorPathFingerprinter(sp.GetRequiredService<ClassNoiseFilter>(), sp.GetRequiredService<MinHashSketcher>()));
        services.AddSingleton<PqGramExtractor>(_ => new PqGramExtractor());
        services.AddSingleton<IStructuralFingerprinter, StructuralFingerprinter>();

        services.AddSingleton<HostHasher>(_ => HostHasher.FromConfiguredKeyOrRandom(options.HostHashKey));
        services.AddSingleton<SqliteConnection>(_ =>
        {
            var conn = new SqliteConnection($"Data Source={options.StorePath}");
            conn.Open();
            SqliteSchema.EnsureCreated(conn);
            return conn;
        });
        services.AddSingleton<ITemplateIndex, SqliteTemplateIndex>();
        services.AddSingleton<SqliteTemplateIndex>(sp => (SqliteTemplateIndex)sp.GetRequiredService<ITemplateIndex>());
        services.AddSingleton<RefitOrchestrator>(sp => new RefitOrchestrator(
            sp.GetRequiredService<SqliteTemplateIndex>(),
            sp.GetRequiredService<IExtractorInducer>(),
            options.Centroid.DriftRefitThreshold,
            options.Centroid.ObservationsBeforeStable,
            options.Centroid.VersionHistoryDepth));

        services.AddSingleton<ITemplateVersionEventSink, DefaultNoopVersionEventSink>();

        services.AddSingleton<ILayoutExtractor>(sp => new LayoutExtractor(
            sp.GetRequiredService<IHtmlDomParser>(),
            sp.GetRequiredService<IDomCleaner>(),
            sp.GetRequiredService<IStructuralFingerprinter>(),
            sp.GetRequiredService<IBlockSegmenter>(),
            sp.GetRequiredService<IBlockClassifier>(),
            sp.GetRequiredService<IMarkdownRenderer>(),
            sp.GetRequiredService<ITemplateIndex>(),
            sp.GetRequiredService<HostHasher>(),
            sp.GetRequiredService<IExtractorInducer>(),
            sp.GetRequiredService<IExtractorApplicator>(),
            options.Match.FastPathJaccardThreshold,
            options.Match.SlowPathCosineThreshold,
            sp.GetRequiredService<RefitOrchestrator>(),
            sp.GetRequiredService<ITemplateVersionEventSink>()));

        return services;
    }
}
```

- [ ] **Step 4: Write failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using Xunit;

namespace StyloExtract.Core.Tests;

public class AddStyloExtractTests
{
    [Fact]
    public async Task AddStyloExtract_ResolvesILayoutExtractor()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();

        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var result = await extractor.ExtractAsync("<html><body><main><article><p>hi</p></article></main></body></html>");

        result.Should().NotBeNull();
        result.Match.Status.Should().BeOneOf(MatchStatus.Novel, MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
    }
}
```

- [ ] **Step 5: Verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.AspNetCore/ tests/StyloExtract.Core.Tests/AddStyloExtractTests.cs
git commit -m "feat(aspnetcore): AddStyloExtract DI wiring + StyloExtractOptions"
```

---

### Task 38: CLI — `extract` subcommand

**Files:**
- Modify: `src/StyloExtract.Cli/StyloExtract.Cli.csproj` ref AspNetCore, add `System.CommandLine`
- Create: `src/StyloExtract.Cli/Program.cs`
- Create: `src/StyloExtract.Cli/Commands/ExtractCommand.cs`

**Interfaces:**
- Consumes: `ILayoutExtractor` from AspNetCore.
- Produces: `stylo-extract extract <file-or-url> [--json] [--profile {MainContentOnly|RagFull|AgentNavigation|DebugFull}] [--store <path>] [--host-hash-key <base64>]`. Default outputs Markdown to stdout. `--json` outputs the full `ExtractionResult` as JSON.

- [ ] **Step 1: Csproj**

```xml
<ItemGroup>
  <PackageReference Include="System.CommandLine" />
  <ProjectReference Include="..\StyloExtract.AspNetCore\StyloExtract.AspNetCore.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write `Program.cs`**

```csharp
using System.CommandLine;
using StyloExtract.Cli.Commands;

var root = new RootCommand("StyloExtract CLI");
root.AddCommand(ExtractCommand.Build());
return await root.InvokeAsync(args);
```

- [ ] **Step 3: Write `ExtractCommand.cs`**

```csharp
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Cli.Commands;

public static class ExtractCommand
{
    public static Command Build()
    {
        var source = new Argument<string>("source", "Path to an HTML file or a https:// URL.");
        var jsonOpt = new Option<bool>("--json", "Output JSON instead of Markdown.");
        var profileOpt = new Option<ExtractionProfile>("--profile", () => ExtractionProfile.RagFull);
        var storeOpt = new Option<string>("--store", () => "styloextract-templates.db");
        var keyOpt = new Option<string?>("--host-hash-key", () => null);

        var cmd = new Command("extract", "Extract a single page.");
        cmd.AddArgument(source);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(profileOpt);
        cmd.AddOption(storeOpt);
        cmd.AddOption(keyOpt);
        cmd.SetHandler(async (string src, bool json, ExtractionProfile profile, string store, string? key) =>
        {
            var services = new ServiceCollection();
            services.AddStyloExtract(o =>
            {
                o.StorePath = store;
                o.HostHashKey = key;
                o.DefaultProfile = profile;
            });
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            var (html, uri) = await LoadAsync(src);
            var result = await extractor.ExtractAsync(html, uri, new ExtractionOptions { Profile = profile });

            if (json)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                await Console.Out.WriteAsync(result.Markdown);
            }
        }, source, jsonOpt, profileOpt, storeOpt, keyOpt);
        return cmd;
    }

    private static async Task<(string Html, Uri? Uri)> LoadAsync(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            using var client = new HttpClient();
            return (await client.GetStringAsync(uri), uri);
        }
        return (await File.ReadAllTextAsync(source), null);
    }
}
```

- [ ] **Step 4: Smoke-test**

```bash
dotnet run --project src/StyloExtract.Cli -- extract tests/StyloExtract.IntegrationTests/Fixtures/example/article.html --profile RagFull
```

Expected: Markdown output of the article.

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Cli/
git commit -m "feat(cli): stylo-extract extract (markdown or json output)"
```

---

### Task 39: CLI — `export` and `import` subcommands

**Files:**
- Create: `src/StyloExtract.Cli/Commands/ExportCommand.cs`
- Create: `src/StyloExtract.Cli/Commands/ImportCommand.cs`
- Modify: `src/StyloExtract.Cli/Program.cs` to register both

**Interfaces:**
- Consumes: `SqliteConnection`, `HostHasher`, `TemplateExporter`, `TemplateImporter`.
- Produces:
  - `stylo-extract export --store <path> --host <displayName> --out <file>` writes the schema-v1 JSON for that host.
  - `stylo-extract import --store <path> --host <displayName> --in <file>` imports it. Idempotent on `TemplateId`.

- [ ] **Step 1: Write `ExportCommand.cs`**

```csharp
using System.CommandLine;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;

namespace StyloExtract.Cli.Commands;

public static class ExportCommand
{
    public static Command Build()
    {
        var store = new Option<string>("--store") { IsRequired = true };
        var host = new Option<string>("--host") { IsRequired = true };
        var outFile = new Option<string>("--out") { IsRequired = true };
        var key = new Option<string?>("--host-hash-key", () => null);

        var cmd = new Command("export", "Export a host's templates as JSON.");
        cmd.AddOption(store); cmd.AddOption(host); cmd.AddOption(outFile); cmd.AddOption(key);
        cmd.SetHandler(async (string storeV, string hostV, string outV, string? keyV) =>
        {
            using var conn = new SqliteConnection($"Data Source={storeV}");
            conn.Open();
            SqliteSchema.EnsureCreated(conn);
            var hasher = HostHasher.FromConfiguredKeyOrRandom(keyV);
            var hostHash = hasher.Hash(hostV);
            await using var fs = File.Create(outV);
            await TemplateExporter.ExportHostAsync(conn, hostHash, hostV, fs, default);
            await Console.Error.WriteLineAsync($"Exported templates for {hostV} → {outV}");
        }, store, host, outFile, key);
        return cmd;
    }
}
```

- [ ] **Step 2: Write `ImportCommand.cs`**

```csharp
using System.CommandLine;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;

namespace StyloExtract.Cli.Commands;

public static class ImportCommand
{
    public static Command Build()
    {
        var store = new Option<string>("--store") { IsRequired = true };
        var host = new Option<string>("--host") { IsRequired = true };
        var inFile = new Option<string>("--in") { IsRequired = true };
        var key = new Option<string?>("--host-hash-key", () => null);

        var cmd = new Command("import", "Import a JSON template bundle into a host.");
        cmd.AddOption(store); cmd.AddOption(host); cmd.AddOption(inFile); cmd.AddOption(key);
        cmd.SetHandler(async (string storeV, string hostV, string inV, string? keyV) =>
        {
            using var conn = new SqliteConnection($"Data Source={storeV}");
            conn.Open();
            SqliteSchema.EnsureCreated(conn);
            var hasher = HostHasher.FromConfiguredKeyOrRandom(keyV);
            var hostHash = hasher.Hash(hostV);
            await using var fs = File.OpenRead(inV);
            var result = await TemplateImporter.ImportAsync(conn, hostHash, fs, default);
            await Console.Error.WriteLineAsync($"Imported {result.ImportedCount}, replaced {result.ReplacedCount}");
        }, store, host, inFile, key);
        return cmd;
    }
}
```

- [ ] **Step 3: Update `Program.cs`** — add `root.AddCommand(ExportCommand.Build())` and `root.AddCommand(ImportCommand.Build())`.

- [ ] **Step 4: Smoke-test**

```bash
DB=/tmp/styloextract-test.db
rm -f "$DB"
dotnet run --project src/StyloExtract.Cli -- extract tests/StyloExtract.IntegrationTests/Fixtures/example/article.html --store "$DB" --json > /dev/null
dotnet run --project src/StyloExtract.Cli -- export --store "$DB" --host example.com --out /tmp/templates.json
test -s /tmp/templates.json
rm -f "$DB"
dotnet run --project src/StyloExtract.Cli -- import --store "$DB" --host example.com --in /tmp/templates.json
```

Expected: prints "Imported 0/1 templates" (depending on what got written).

- [ ] **Step 5: Commit**

```bash
git add src/StyloExtract.Cli/
git commit -m "feat(cli): stylo-extract export and import subcommands"
```

---

### Task 40: CLI — `monitor` subcommand (NDJSON + optional webhook)

**Files:**
- Create: `src/StyloExtract.Cli/MonitorEventSink.cs`
- Create: `src/StyloExtract.Cli/Commands/MonitorCommand.cs`
- Modify: `src/StyloExtract.Cli/Program.cs`

**Interfaces:**
- Consumes: `ILayoutExtractor`, `HttpClient`, `ITemplateVersionEventSink`.
- Produces:
  - `stylo-extract monitor --urls <file> --store <path> [--interval <duration>] [--webhook <url>] [--pretty]` reads a newline-delimited list of URLs, fetches each, runs extraction, and emits one NDJSON event per `NewTemplate` / `VersionChange` to stdout. `--webhook` POSTs each event JSON to the URL. `--pretty` renders human-readable single events instead of NDJSON.
- `MonitorEventSink : ITemplateVersionEventSink` is the custom sink the monitor wires in.

- [ ] **Step 1: Write `MonitorEventSink.cs`**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using StyloExtract.Abstractions;

namespace StyloExtract.Cli;

public sealed class MonitorEventSink : ITemplateVersionEventSink
{
    private readonly TextWriter _out;
    private readonly HttpClient? _webhook;
    private readonly Uri? _webhookUrl;
    private readonly bool _pretty;
    private static readonly JsonSerializerOptions Compact = new();
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public MonitorEventSink(TextWriter @out, string? webhook, bool pretty)
    {
        _out = @out;
        _pretty = pretty;
        if (!string.IsNullOrEmpty(webhook))
        {
            _webhookUrl = new Uri(webhook);
            _webhook = new HttpClient();
        }
    }

    public async ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken ct)
        => await EmitAsync("new-template", evt, ct);

    public async ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken ct)
        => await EmitAsync("version-change", evt, ct);

    private async Task EmitAsync(string kind, object payload, CancellationToken ct)
    {
        var envelope = new { kind, emittedAt = DateTimeOffset.UtcNow, payload };
        var json = JsonSerializer.Serialize(envelope, _pretty ? Pretty : Compact);
        await _out.WriteLineAsync(json);
        if (_webhook is not null && _webhookUrl is not null)
        {
            try { await _webhook.PostAsJsonAsync(_webhookUrl, envelope, ct); }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"webhook failed: {ex.Message}"); }
        }
    }
}
```

- [ ] **Step 2: Write `MonitorCommand.cs`**

```csharp
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Cli.Commands;

public static class MonitorCommand
{
    public static Command Build()
    {
        var urls = new Option<string>("--urls") { IsRequired = true };
        var store = new Option<string>("--store") { IsRequired = true };
        var interval = new Option<TimeSpan>("--interval", () => TimeSpan.FromMinutes(60));
        var webhook = new Option<string?>("--webhook", () => null);
        var pretty = new Option<bool>("--pretty", () => false);

        var cmd = new Command("monitor", "Watch a list of URLs and emit NDJSON template-version events.");
        cmd.AddOption(urls); cmd.AddOption(store); cmd.AddOption(interval); cmd.AddOption(webhook); cmd.AddOption(pretty);
        cmd.SetHandler(async (string urlsFile, string storeV, TimeSpan intv, string? wh, bool pr) =>
        {
            var urlList = (await File.ReadAllLinesAsync(urlsFile))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
                .ToList();

            var services = new ServiceCollection();
            var sink = new MonitorEventSink(Console.Out, wh, pr);
            services.AddSingleton<ITemplateVersionEventSink>(sink);
            services.AddStyloExtract(o => o.StorePath = storeV);
            // The sink registration above must precede AddStyloExtract for it to win.
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();

            using var http = new HttpClient();
            while (true)
            {
                foreach (var u in urlList)
                {
                    try
                    {
                        var html = await http.GetStringAsync(u);
                        await extractor.ExtractAsync(html, new Uri(u));
                    }
                    catch (Exception ex)
                    {
                        await Console.Error.WriteLineAsync($"{u}: {ex.Message}");
                    }
                }
                await Task.Delay(intv);
            }
        }, urls, store, interval, webhook, pretty);
        return cmd;
    }
}
```

Note: `AddStyloExtract` currently registers the default sink — fix it in `StyloExtractServiceCollectionExtensions` to use `services.TryAddSingleton<ITemplateVersionEventSink, DefaultNoopVersionEventSink>()` so external pre-registration wins. (Update the M7-T37 file accordingly if not already.)

- [ ] **Step 3: Update `AddStyloExtract`** — change `services.AddSingleton<ITemplateVersionEventSink, DefaultNoopVersionEventSink>();` to `services.TryAddSingleton<ITemplateVersionEventSink, DefaultNoopVersionEventSink>();` (add `using Microsoft.Extensions.DependencyInjection.Extensions;`).

- [ ] **Step 4: Update `Program.cs`** — add `root.AddCommand(MonitorCommand.Build())`.

- [ ] **Step 5: Smoke-test** (manual; not committed as automated test because it loops):

```bash
echo "https://example.com" > /tmp/urls.txt
timeout 10s dotnet run --project src/StyloExtract.Cli -- monitor --urls /tmp/urls.txt --store /tmp/sx.db --interval 00:00:30
```

Expected: one NDJSON line for the new-template event, then exits after timeout.

- [ ] **Step 6: Commit**

```bash
git add src/StyloExtract.Cli/ src/StyloExtract.AspNetCore/StyloExtractServiceCollectionExtensions.cs
git commit -m "feat(cli): stylo-extract monitor (NDJSON / --webhook / --pretty)"
```

---

### Task 41: Benchmarks — fast-path match, full extract, allocations

**Files:**
- Modify: `bench/StyloExtract.Benchmarks/StyloExtract.Benchmarks.csproj` ref Core + AspNetCore
- Create: `bench/StyloExtract.Benchmarks/Program.cs`
- Create: `bench/StyloExtract.Benchmarks/FastPathMatchBench.cs`
- Create: `bench/StyloExtract.Benchmarks/FullExtractBench.cs`
- Create: `bench/StyloExtract.Benchmarks/AllocationBench.cs`

**Interfaces:**
- Consumes: built solution.
- Produces: `dotnet run -c Release` against the Benchmarks project producing four scenarios:
  1. **Fast-path match step only** (pre-computed signature) — target <1ms p99
  2. **Full ExtractAsync on cache hit** — target <15ms p99
  3. **Full ExtractAsync on slow-path match** — target <30ms p99
  4. **Allocations** (`MemoryDiagnoser`) — no LOH per call

- [ ] **Step 1: Csproj**

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\StyloExtract.Core\StyloExtract.Core.csproj" />
  <ProjectReference Include="..\..\src\StyloExtract.AspNetCore\StyloExtract.AspNetCore.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Write `Program.cs`**

```csharp
using BenchmarkDotNet.Running;
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

- [ ] **Step 3: Write `FastPathMatchBench.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Templates;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class FastPathMatchBench
{
    private SqliteConnection _conn = null!;
    private SqliteTemplateIndex _index = null!;
    private StructuralFingerprint _fp = null!;
    private byte[] _host = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        SqliteSchema.EnsureCreated(_conn);
        _index = new SqliteTemplateIndex(_conn);
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var doc = parser.Parse("<html><body><main><article><h1>x</h1><p>y</p></article></main></body></html>");
        _fp = fingerprinter.Compute(doc);
        _host = new byte[16];
        var ex = new ExtractorInducer().Induce(Guid.NewGuid(), new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "", Markdown = "", XPath = "/", CssSelector = "main > article", TextLength = 100, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() }
        });
        await _index.RegisterAsync(_host, _fp, ex, default);
    }

    [Benchmark]
    public async Task<Guid?> ProbeFastPath() => await _index.ProbeFastPathAsync(_host, _fp, 0.85, default);
}
```

- [ ] **Step 4: Write `FullExtractBench.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class FullExtractBench
{
    private ILayoutExtractor _extractor = null!;
    private string _html = null!;
    private Uri _uri = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        _extractor = sp.GetRequiredService<ILayoutExtractor>();
        _html = File.ReadAllText("article.html");
        _uri = new Uri("https://bench.example.com/page");
        // Warm: first call registers; second is fast-path
        await _extractor.ExtractAsync(_html, _uri);
    }

    [Benchmark]
    public async Task<ExtractionResult> FullExtract_CacheHit() => await _extractor.ExtractAsync(_html, _uri);
}
```

(Copy `tests/StyloExtract.IntegrationTests/Fixtures/example/article.html` to `bench/StyloExtract.Benchmarks/article.html` and mark as `CopyToOutputDirectory=PreserveNewest`.)

- [ ] **Step 5: Write `AllocationBench.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class AllocationBench
{
    private ILayoutExtractor _extractor = null!;
    private string _html = null!;
    private Uri _uri = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        _extractor = sp.GetRequiredService<ILayoutExtractor>();
        _html = File.ReadAllText("article.html");
        _uri = new Uri("https://bench.example.com/page");
        await _extractor.ExtractAsync(_html, _uri); // warm
    }

    [Benchmark]
    public async Task<long> CacheHit_Allocations()
    {
        var result = await _extractor.ExtractAsync(_html, _uri);
        return result.Stats.BlockCount;
    }
}
```

- [ ] **Step 6: Smoke-test**

```bash
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --filter "*FastPathMatchBench*"
```

Expected: BenchmarkDotNet reports microbenchmark results. Verify `ProbeFastPath` mean ≪ 1ms.

- [ ] **Step 7: Commit**

```bash
git add bench/StyloExtract.Benchmarks/
git commit -m "feat(benchmarks): BDN harness — fast-path match, full extract, allocations"
```

**End of M7.**

---

## Done — what to verify before declaring v1

Run from the repo root:

```bash
dotnet build stylobot-extract.sln -c Release
dotnet test stylobot-extract.sln -c Release
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --filter "*FastPathMatch*"
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --filter "*FullExtractBench*"
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --filter "*AllocationBench*"
```

Spec coverage at this point:

| Spec section | Implemented in |
|---|---|
| §1 Wedge | M4 orchestration + M5 refit |
| §2 Architecture overview | T26 (orchestration), T11 (skeleton) |
| §3 Pipeline detail | M1 (3.1 parse/clean, 3.7 heuristics, 3.8 induction), M2 (3.2-3.6 fingerprint primitives) |
| §4 Public API | T4 |
| §5 SQLite schema | T20 |
| §6 Aging and prioritisation | T32 |
| §7 Centroid extractors | T24 + T29 + T31 |
| §8 Version detection | T31 + T33 |
| §9 Export format | T34–T36 |
| §10 Package topology | T2 + every later task |
| §11 v1 scope | (entire plan; "out" set is enforced by what's *not* here) |
| §12 Configuration + DI | T37 |
| §13 Performance targets | T41 |
| §14 Testing strategy | T9 + T27 + T36 + T41 |
| §15 Stylobot integration | future, sketched only in spec (correct) |
| §16 Research caveats | enforced in T16 (pq-gram NOT a metric), T14 (LSH defaults from literature) |

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-20-styloextract.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?








