# Extract Dashboard Persistence from `Mostlylucid.BotDetection.UI`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `Mostlylucid.BotDetection.Api` from transitively dragging the entire `Mostlylucid.BotDetection.UI` package (Razor, SignalR, Identity scaffolding, dashboard middleware, ApexCharts, DaisyUI, Tailwind, HTMX) just to access `IDashboardEventStore`, `IRouteCatalogService`, and a handful of POCOs. The sidecar binary shrinks ~10MB; the Api NuGet package stops having a Razor + SignalR dependency for consumers who only want REST endpoints.

**Architecture:** New `Mostlylucid.BotDetection.Dashboard.Persistence` project containing the persistence interfaces, their SQLite implementations, the write-through caches (`SignatureAggregateCache`, `VisitorListCache`), and the model POCOs. `UI` depends on it (keeps Razor / middleware / SignalR). `Api` depends on it instead of UI.

**Why it's deferred from the cluster/naming work:** ~66 files touch the affected types (`IDashboardEventStore`, `DashboardSignatureEvent`, `IRouteCatalogService`, `SignatureAggregateCache`, `VisitorListCache`, etc.). The cluster persistence + LLM-name-to-SQLite fixes are higher-priority and orthogonal to this restructuring.

**Tech Stack:** .NET 10, MSBuild SDK switch (`Microsoft.NET.Sdk` for the new project, NOT `Microsoft.NET.Sdk.Web` — that's what propagates static web assets).

**Critical invariants:**

1. No type identity changes for consumers — XML/JSON serialization compatibility preserved.
2. New project uses plain `Microsoft.NET.Sdk` (not the Web SDK) so it never propagates static web assets to consumers.
3. The interim quick-win (wwwroot stripped from sidecar publish via `AfterTargets="Publish"` `RemoveDir`) stays — it's a defence-in-depth that protects against future regressions if someone references UI directly.

---

## File Structure

### New project
- `src/Mostlylucid.BotDetection.Dashboard.Persistence/Mostlylucid.BotDetection.Dashboard.Persistence.csproj` (Microsoft.NET.Sdk, no Web SDK)

### Files to move (UI → Dashboard.Persistence)
**Interfaces + impls:**
- `Services/IDashboardEventStore.cs`
- `Services/SqliteDashboardEventStore.cs`
- `Services/IRouteCatalogService.cs`
- `Services/RouteCatalogService.cs`
- `Services/IRouteNameStore.cs`
- `Services/SqliteRouteNameStore.cs` (verify name)

**Models:**
- `Models/DashboardPartialModels.cs` (split as needed — keep only POCOs the persistence layer needs)
- `Models/DashboardFilter.cs` (verify name; may live in the partial-models file)
- `Models/RouteCatalogEntry.cs`
- `Models/InvestigationFilter.cs`
- `Models/InvestigationResult.cs`
- `Models/UserAgentSearchResult.cs`

**Write-through caches:**
- `Services/SignatureAggregateCache.cs`
- `Services/VisitorListCache.cs`

### Files modified in UI (consumers of moved types)
- Anything still in UI that uses `IDashboardEventStore`, the caches, or the models — update `using` statements only. Roughly: middleware, view components, view models, Razor `@using`s in `_ViewImports.cshtml`, `LlmResultSignalRCallback.cs`, dashboard service registrations.

### Files modified in Api
- `Endpoints/RoutesEndpoints.cs`: change `using Mostlylucid.BotDetection.UI.Services.Routes;` → `using Mostlylucid.BotDetection.Dashboard.Persistence.Services;` (or whatever sub-namespace settles on)
- `Endpoints/ReadEndpoints.cs`: same — change `UI.Models` / `UI.Services` usings
- `Mostlylucid.BotDetection.Api.csproj`: remove `<ProjectReference Include="..\Mostlylucid.BotDetection.UI\..." />`; add new project ref instead.

### Solution
- `mostlylucid.stylobot.sln`: add the new project

### Tests
- Tests in `Mostlylucid.BotDetection.UI.Test` (or similar) that reference moved types — update usings.
- Tests touching `IDashboardEventStore` / `SqliteDashboardEventStore` may need to move to a `Dashboard.Persistence.Tests` project, OR stay in their current test project with an updated reference (acceptable to keep grouping).

---

## Phase 1 — Create the new project skeleton

### Task 1.1: Create empty project + add to solution

**Files:**
- Create: `src/Mostlylucid.BotDetection.Dashboard.Persistence/Mostlylucid.BotDetection.Dashboard.Persistence.csproj`
- Modify: `mostlylucid.stylobot.sln`

- [ ] **Step 1.1.1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>true</IsPackable>
    <PackageId>Mostlylucid.BotDetection.Dashboard.Persistence</PackageId>
    <MinVerTagPrefix>allbot-v</MinVerTagPrefix>
    <Authors>Mostlylucid</Authors>
    <Description>SQLite-backed dashboard persistence (event store, route catalog, write-through caches) extracted from the UI project so the Api / sidecar / other headless consumers don't drag Razor + SignalR + wwwroot.</Description>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="..." />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj" />
  </ItemGroup>
</Project>
```

(Match the `Microsoft.Data.Sqlite` version from the existing UI csproj.)

- [ ] **Step 1.1.2: Add to solution**

```bash
dotnet sln mostlylucid.stylobot.sln add src/Mostlylucid.BotDetection.Dashboard.Persistence/Mostlylucid.BotDetection.Dashboard.Persistence.csproj
```

- [ ] **Step 1.1.3: Build empty project**

```bash
dotnet build src/Mostlylucid.BotDetection.Dashboard.Persistence/Mostlylucid.BotDetection.Dashboard.Persistence.csproj --nologo
```
Expected: PASS (empty project).

---

## Phase 2 — Move files in waves

### Task 2.1: Move `IDashboardEventStore` + `SqliteDashboardEventStore` + dashboard models

**Files** (the exact paths from `src/Mostlylucid.BotDetection.UI/`):
- Move: `Services/IDashboardEventStore.cs`
- Move: `Services/SqliteDashboardEventStore.cs`
- Move: `Models/DashboardPartialModels.cs` (review — if the file contains types that depend on Razor or SignalR, split first)

- [ ] **Step 2.1.1: `git mv` each file to new project**

```bash
git mv src/Mostlylucid.BotDetection.UI/Services/IDashboardEventStore.cs \
       src/Mostlylucid.BotDetection.Dashboard.Persistence/Services/IDashboardEventStore.cs
git mv src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs \
       src/Mostlylucid.BotDetection.Dashboard.Persistence/Services/SqliteDashboardEventStore.cs
git mv src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs \
       src/Mostlylucid.BotDetection.Dashboard.Persistence/Models/DashboardPartialModels.cs
```

- [ ] **Step 2.1.2: Rename namespaces in moved files**

```bash
sed -i '' 's|namespace Mostlylucid.BotDetection.UI.Services|namespace Mostlylucid.BotDetection.Dashboard.Persistence.Services|g' \
  src/Mostlylucid.BotDetection.Dashboard.Persistence/Services/*.cs
sed -i '' 's|namespace Mostlylucid.BotDetection.UI.Models|namespace Mostlylucid.BotDetection.Dashboard.Persistence.Models|g' \
  src/Mostlylucid.BotDetection.Dashboard.Persistence/Models/*.cs
```

- [ ] **Step 2.1.3: Update all consumer `using` statements**

```bash
grep -rln "using Mostlylucid.BotDetection.UI.Services;\|using Mostlylucid.BotDetection.UI.Models;" \
  --include="*.cs" --include="*.cshtml" src/ | while read f; do
  sed -i '' '
    s|using Mostlylucid.BotDetection.UI.Services;|using Mostlylucid.BotDetection.Dashboard.Persistence.Services;\nusing Mostlylucid.BotDetection.UI.Services;|;
    s|using Mostlylucid.BotDetection.UI.Models;|using Mostlylucid.BotDetection.Dashboard.Persistence.Models;\nusing Mostlylucid.BotDetection.UI.Models;|
  ' "$f"
done
```

(Adds the new using alongside the old. If the old usings are no longer needed by a given file because the moved types were its only ones from that namespace, the build will simply report unused usings — clean up.)

- [ ] **Step 2.1.4: Make UI reference the new project**

In `src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj` add:

```xml
<ProjectReference Include="..\Mostlylucid.BotDetection.Dashboard.Persistence\Mostlylucid.BotDetection.Dashboard.Persistence.csproj" />
```

- [ ] **Step 2.1.5: Build solution, fix unresolved references**

```bash
dotnet build mostlylucid.stylobot.sln --nologo -v quiet 2>&1 | tail -20
```

Iterate. Common issues: forgotten `using` add in tests, Razor `@using` directives in `_ViewImports.cshtml`, view-component files.

- [ ] **Step 2.1.6: Run unit + integration tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --nologo
dotnet test src/Mostlylucid.BotDetection.UI.Test --nologo
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --nologo
```

- [ ] **Step 2.1.7: Commit**

```bash
git add -A && git commit -m "refactor(persistence): extract IDashboardEventStore + dashboard models"
```

### Task 2.2: Move `IRouteCatalogService` + `IRouteNameStore` + their impls

Same pattern as Task 2.1 but for `Services/Routes/` and `Models/RouteCatalogEntry`. Commit after build + tests pass.

### Task 2.3: Move write-through caches

`SignatureAggregateCache`, `VisitorListCache`. Same pattern. These are write-through over `IDashboardEventStore` now — they live in the persistence project beside the event store.

`LlmResultSignalRCallback.cs` (which calls these caches + the event store) STAYS in UI because it needs the SignalR Hub. Its dependencies are now Persistence (for the caches + the event store + the fingerprint store) + UI (for the Hub).

---

## Phase 3 — Switch Api to depend on Persistence (not UI)

### Task 3.1: Update Api csproj + endpoint usings

- [ ] **Step 3.1.1: Replace UI reference in Api csproj**

In `src/Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj`:

```xml
<!-- Remove: -->
<ProjectReference Include="..\Mostlylucid.BotDetection.UI\Mostlylucid.BotDetection.UI.csproj" />
<!-- Add: -->
<ProjectReference Include="..\Mostlylucid.BotDetection.Dashboard.Persistence\Mostlylucid.BotDetection.Dashboard.Persistence.csproj" />
```

- [ ] **Step 3.1.2: Update endpoint usings**

In `src/Mostlylucid.BotDetection.Api/Endpoints/RoutesEndpoints.cs` and `ReadEndpoints.cs`:

```diff
-using Mostlylucid.BotDetection.UI.Services.Routes;
-using Mostlylucid.BotDetection.UI.Models;
-using Mostlylucid.BotDetection.UI.Services;
+using Mostlylucid.BotDetection.Dashboard.Persistence.Services;
+using Mostlylucid.BotDetection.Dashboard.Persistence.Models;
```

- [ ] **Step 3.1.3: Build + test**

```bash
dotnet build mostlylucid.stylobot.sln --nologo -v quiet 2>&1 | tail -3
dotnet test src/Mostlylucid.BotDetection.Test --nologo
```

- [ ] **Step 3.1.4: Verify Api no longer transitively pulls UI**

```bash
dotnet list src/Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj reference
```

Expected output: NO `Mostlylucid.BotDetection.UI` reference.

- [ ] **Step 3.1.5: Commit**

```bash
git add -A && git commit -m "refactor(api): depend on Dashboard.Persistence instead of UI"
```

---

## Phase 4 — Verify sidecar binary shrinks

### Task 4.1: Publish + measure

- [ ] **Step 4.1.1: Publish self-contained binary**

```bash
rm -rf /tmp/sidecar-after
dotnet publish src/Mostlylucid.BotDetection.Sidecar/Mostlylucid.BotDetection.Sidecar.csproj \
  --configuration Release --runtime osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:Version=0.0.0-after \
  --output /tmp/sidecar-after --nologo
```

- [ ] **Step 4.1.2: Compare to baseline**

Baseline (before this work): 131MB single-file. Expected after: 115-122MB (drop of ~10-15MB from removing the Razor + SignalR + Identity-scaffolding transitive packages).

- [ ] **Step 4.1.3: Sanity-check the binary runs**

```bash
cd /tmp/sidecar-after
BotDetection__ApiKeys__0__Name=test BotDetection__ApiKeys__0__Key=testkey123 \
  ./stylobot-sidecar &
sleep 5
curl -s http://localhost:5091/health
pkill -f stylobot-sidecar
```

Expected: HTTP 200 from `/health`.

- [ ] **Step 4.1.4: Re-run BDF rig (some integration tests may have moved types in mocks)**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "Category=Integration&FullyQualifiedName~BdfReplayTests" --nologo
```

Expected: 17/17.

---

## Acceptance criteria

1. `Mostlylucid.BotDetection.Dashboard.Persistence` exists, contains `IDashboardEventStore`, `SqliteDashboardEventStore`, `IRouteCatalogService`, `IRouteNameStore`, `SignatureAggregateCache`, `VisitorListCache`, and the dashboard model POCOs.
2. `Mostlylucid.BotDetection.Api` no longer references `Mostlylucid.BotDetection.UI` (verified via `dotnet list reference`).
3. The sidecar published binary is at least 8MB smaller than the 131MB baseline.
4. All unit + integration tests pass.
5. The Razor dashboard (`/_stylobot`) still works end-to-end (UI still has all its concrete views + persistence implementations via the new project ref).

---

## Self-review

**Spec coverage:** Phases 1–4 deliver the extraction. The interim wwwroot-strip target in the sidecar csproj stays as defence-in-depth.

**Placeholders:** Every step has the actual command or code. The `sed -i ''` syntax is macOS-specific; Linux uses `sed -i`. Worker should adapt if running on Linux.

**Type consistency:** All moved types keep the same name + same public surface; only namespaces change. Tests reference types via their new namespaces.

**Open question for the worker:** if any of the moved models has a property that references a UI-internal type (e.g. an enum defined in `UI.Components`), that dependency must be moved too OR the property re-typed. Detect by building the persistence project in isolation — unresolved references will list them. Common offenders: enums used in filters, view-state types embedded in event records. None spotted in a pre-extraction grep but worth confirming.

**Risk:** the `LlmResultSignalRCallback` write-through (just committed in `9405f45` + extended in this session) calls both `IDashboardEventStore.UpdateSignatureBotNameAsync` and `SqliteFingerprintStore.UpdateDisplayNameForSignatureAsync`. The callback stays in UI (needs the Hub). After this refactor, the callback's `using`s update; behaviour unchanged.
