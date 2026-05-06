# Pack System + Reaction Packs Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the extensible pack system foundation (hook interfaces, PackRegistry, IDashboardTab, pack loader) and complete the remaining reaction packs tasks (middleware wiring, dashboard service, dashboard UI tab).

**Architecture:** Hook interfaces (`IStylobotPreActionHook`, `IStylobotPostResponseHook`) replace direct reaction-pack dependency in `BotDetectionMiddleware`; `PackRegistry<T>` provides a mutable `IEnumerable<T>` that DI resolves at startup and packs append to at load time; reaction pack types implement these interfaces to make the feature the model pack. FOSS tier: packs are read-only (observe + display, no config writes); commercial tier will extend later.

**Tech Stack:** .NET 10, ASP.NET Core, xUnit, YamlDotNet, Tailwind/DaisyUI/HTMX for dashboard UI.

---

## File Map

**New files (BotDetection):**
- `src/Mostlylucid.BotDetection/Services/IStylobotPreActionHook.cs`
- `src/Mostlylucid.BotDetection/Services/IStylobotPostResponseHook.cs`
- `src/Mostlylucid.BotDetection/Services/ResponseContext.cs`
- `src/Mostlylucid.BotDetection/Packs/PackRegistry.cs`
- `src/Mostlylucid.BotDetection/Packs/IStylobotPack.cs`
- `src/Mostlylucid.BotDetection/Packs/IPackCapabilities.cs`
- `src/Mostlylucid.BotDetection/Packs/PackCapabilities.cs`
- `src/Mostlylucid.BotDetection/Packs/PackManifest.cs`
- `src/Mostlylucid.BotDetection/Packs/PackLoader.cs`

**New files (BotDetection.UI):**
- `src/Mostlylucid.BotDetection.UI/Services/IDashboardTab.cs`
- `src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs`
- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_ReactionPacksTab.cshtml`

**New test files:**
- `src/Mostlylucid.BotDetection.Test/Packs/PackRegistryTests.cs`
- `src/Mostlylucid.BotDetection.Test/Packs/PackLoaderTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/PreActionHookTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/PostResponseHookTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/ReactionPackDashboardServiceTests.cs`

**Modified files:**
- `src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs` - implement IStylobotPreActionHook
- `src/Mostlylucid.BotDetection/Services/DegradationAtom.cs` - implement IStylobotPostResponseHook
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` - register PackRegistry<T>, PackLoader, hook-implementing singletons
- `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` - consume IEnumerable<IStylobotPreActionHook> and IEnumerable<IStylobotPostResponseHook>
- `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` - add reaction-packs tab routing
- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` - add Reaction Packs tab to nav

---

## Task 1: Hook interfaces and ResponseContext

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/IStylobotPreActionHook.cs`
- Create: `src/Mostlylucid.BotDetection/Services/IStylobotPostResponseHook.cs`
- Create: `src/Mostlylucid.BotDetection/Services/ResponseContext.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/PreActionHookTests.cs`

- [ ] **Step 1: Create ResponseContext**

```csharp
// src/Mostlylucid.BotDetection/Services/ResponseContext.cs
namespace Mostlylucid.BotDetection.Services;

public sealed record ResponseContext(
    int StatusCode,
    long LatencyMs,
    string Path,
    string? ActionPolicyName);
```

- [ ] **Step 2: Create IStylobotPreActionHook**

```csharp
// src/Mostlylucid.BotDetection/Services/IStylobotPreActionHook.cs
namespace Mostlylucid.BotDetection.Services;

public interface IStylobotPreActionHook
{
    int Priority { get; }
    ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct);
}
```

- [ ] **Step 3: Create IStylobotPostResponseHook**

```csharp
// src/Mostlylucid.BotDetection/Services/IStylobotPostResponseHook.cs
namespace Mostlylucid.BotDetection.Services;

public interface IStylobotPostResponseHook
{
    ValueTask OnResponseCompletedAsync(ResponseContext context, CancellationToken ct);
}
```

- [ ] **Step 4: Write failing tests**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/PreActionHookTests.cs
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class PreActionHookTests
{
    [Fact]
    public async Task Hook_ReturnsNull_WhenNoOverride()
    {
        var hook = new NullPreActionHook();
        var result = await hook.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Hook_ReturnsPolicy_WhenOverrideAvailable()
    {
        var hook = new FixedPolicyHook("block");
        var result = await hook.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Equal("block", result);
    }

    private sealed class NullPreActionHook : IStylobotPreActionHook
    {
        public int Priority => 0;
        public ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class FixedPolicyHook(string policy) : IStylobotPreActionHook
    {
        public int Priority => 0;
        public ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct)
            => ValueTask.FromResult<string?>(policy);
    }
}
```

- [ ] **Step 5: Run tests to verify they fail (type not found)**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PreActionHookTests" -v minimal`
Expected: FAIL - `IStylobotPreActionHook` not found.

- [ ] **Step 6: Confirm tests pass after creating files above**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PreActionHookTests" -v minimal`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/IStylobotPreActionHook.cs \
        src/Mostlylucid.BotDetection/Services/IStylobotPostResponseHook.cs \
        src/Mostlylucid.BotDetection/Services/ResponseContext.cs \
        src/Mostlylucid.BotDetection.Test/Services/PreActionHookTests.cs
git commit -m "feat(packs): add IStylobotPreActionHook, IStylobotPostResponseHook, ResponseContext"
```

---

## Task 2: PackRegistry and IDashboardTab

**Files:**
- Create: `src/Mostlylucid.BotDetection/Packs/PackRegistry.cs`
- Create: `src/Mostlylucid.BotDetection.UI/Services/IDashboardTab.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Packs/PackRegistryTests.cs`

- [ ] **Step 1: Write failing test for PackRegistry**

```csharp
// src/Mostlylucid.BotDetection.Test/Packs/PackRegistryTests.cs
using Mostlylucid.BotDetection.Packs;

namespace Mostlylucid.BotDetection.Test.Packs;

public class PackRegistryTests
{
    [Fact]
    public void Add_ItemAppearsInEnumeration()
    {
        var registry = new PackRegistry<string>();
        registry.Add("hello");
        Assert.Contains("hello", registry);
    }

    [Fact]
    public void EmptyRegistry_EnumeratesEmpty()
    {
        var registry = new PackRegistry<string>();
        Assert.Empty(registry);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var registry = new PackRegistry<string>();
        registry.Add("a");
        registry.Add("b");
        registry.Clear();
        Assert.Empty(registry);
    }

    [Fact]
    public void MultipleItems_AllEnumerated()
    {
        var registry = new PackRegistry<int>();
        registry.Add(1);
        registry.Add(2);
        registry.Add(3);
        Assert.Equal(3, registry.Count());
    }
}
```

- [ ] **Step 2: Run tests to verify fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackRegistryTests" -v minimal`
Expected: FAIL - `PackRegistry<T>` not found.

- [ ] **Step 3: Create PackRegistry**

```csharp
// src/Mostlylucid.BotDetection/Packs/PackRegistry.cs
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackRegistry<T> : IEnumerable<T>
{
    private readonly ConcurrentBag<T> _items = new();

    public void Add(T item) => _items.Add(item);

    public void Clear()
    {
        while (!_items.IsEmpty)
            _items.TryTake(out _);
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
```

- [ ] **Step 4: Create IDashboardTab**

```csharp
// src/Mostlylucid.BotDetection.UI/Services/IDashboardTab.cs
namespace Mostlylucid.BotDetection.UI.Services;

public interface IDashboardTab
{
    string TabId { get; }
    string DisplayName { get; }
    string PartialViewPath { get; }
    int Order { get; }
    bool RequiresWrite { get; }
}
```

- [ ] **Step 5: Verify tests pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackRegistryTests" -v minimal`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Packs/PackRegistry.cs \
        src/Mostlylucid.BotDetection.UI/Services/IDashboardTab.cs \
        src/Mostlylucid.BotDetection.Test/Packs/PackRegistryTests.cs
git commit -m "feat(packs): add PackRegistry<T> and IDashboardTab"
```

---

## Task 3: Pack manifest model and PackLoader

**Files:**
- Create: `src/Mostlylucid.BotDetection/Packs/IStylobotPack.cs`
- Create: `src/Mostlylucid.BotDetection/Packs/IPackCapabilities.cs`
- Create: `src/Mostlylucid.BotDetection/Packs/PackCapabilities.cs`
- Create: `src/Mostlylucid.BotDetection/Packs/PackManifest.cs`
- Create: `src/Mostlylucid.BotDetection/Packs/PackLoader.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Packs/PackLoaderTests.cs`

- [ ] **Step 1: Create pack contract interfaces**

```csharp
// src/Mostlylucid.BotDetection/Packs/IPackCapabilities.cs
namespace Mostlylucid.BotDetection.Packs;

public interface IPackCapabilities
{
    bool CanWrite { get; }
    string Tier { get; }
}
```

```csharp
// src/Mostlylucid.BotDetection/Packs/PackCapabilities.cs
namespace Mostlylucid.BotDetection.Packs;

public sealed class PackCapabilities(bool canWrite) : IPackCapabilities
{
    public static readonly IPackCapabilities Foss = new PackCapabilities(false);
    public static readonly IPackCapabilities Commercial = new PackCapabilities(true);

    public bool CanWrite { get; } = canWrite;
    public string Tier => CanWrite ? "commercial" : "foss";
}
```

```csharp
// src/Mostlylucid.BotDetection/Packs/IStylobotPack.cs
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Packs;

public interface IStylobotPack
{
    string Name { get; }
    string Version { get; }
    void ConfigureServices(IServiceCollection services, IPackCapabilities capabilities);
}
```

- [ ] **Step 2: Create PackManifest**

```csharp
// src/Mostlylucid.BotDetection/Packs/PackManifest.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; init; } = "1.0.0";

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "author")]
    public string Author { get; init; } = "";

    [YamlMember(Alias = "requires_tier")]
    public string RequiresTier { get; init; } = "foss";

    [YamlMember(Alias = "min_core_version")]
    public string MinCoreVersion { get; init; } = "1.0.0";

    [YamlMember(Alias = "assembly")]
    public string? Assembly { get; init; }

    [YamlMember(Alias = "entry_type")]
    public string? EntryType { get; init; }
}
```

- [ ] **Step 3: Write failing test for PackLoader**

```csharp
// src/Mostlylucid.BotDetection.Test/Packs/PackLoaderTests.cs
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Packs;

namespace Mostlylucid.BotDetection.Test.Packs;

public class PackLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"packs_{Guid.NewGuid():N}");

    public PackLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ScanDirectory_EmptyDir_LoadsNothing()
    {
        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
    }

    [Fact]
    public void ScanDirectory_NonStylopackFiles_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a pack");

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
    }

    [Fact]
    public void ScanDirectory_YamlOnlyPack_LoadsManifest()
    {
        var packPath = Path.Combine(_tempDir, "test-pack-1.0.0.stylopack");
        using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.yaml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                name: test-pack
                version: 1.0.0
                requires_tier: foss
                """);
        }

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(1, loader.LoadedCount);
        Assert.Equal("test-pack", loader.LoadedManifests[0].Name);
    }

    [Fact]
    public void ScanDirectory_CommercialPack_SkippedOnFoss()
    {
        var packPath = Path.Combine(_tempDir, "paid-pack-1.0.0.stylopack");
        using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.yaml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                name: paid-pack
                version: 1.0.0
                requires_tier: commercial
                """);
        }

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
        Assert.Single(loader.SkippedManifests);
        Assert.Equal("paid-pack", loader.SkippedManifests[0].Name);
    }
}
```

- [ ] **Step 4: Run tests to verify fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackLoaderTests" -v minimal`
Expected: FAIL - `PackLoader` not found.

- [ ] **Step 5: Create PackLoader**

```csharp
// src/Mostlylucid.BotDetection/Packs/PackLoader.cs
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackLoader(
    string packsDirectory,
    IPackCapabilities capabilities,
    IServiceCollection services,
    ILogger<PackLoader> logger)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly List<PackManifest> _loaded = [];
    private readonly List<PackManifest> _skipped = [];

    public int LoadedCount => _loaded.Count;
    public IReadOnlyList<PackManifest> LoadedManifests => _loaded;
    public IReadOnlyList<PackManifest> SkippedManifests => _skipped;

    public void LoadAll()
    {
        if (!Directory.Exists(packsDirectory))
            return;

        foreach (var path in Directory.EnumerateFiles(packsDirectory, "*.stylopack"))
            LoadOne(path);
    }

    private void LoadOne(string path)
    {
        PackManifest? manifest = null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.GetEntry("manifest.yaml");
            if (manifestEntry == null)
            {
                logger.LogWarning("Pack {Path} has no manifest.yaml, skipping", path);
                return;
            }

            using var reader = new StreamReader(manifestEntry.Open());
            manifest = Deserializer.Deserialize<PackManifest>(reader.ReadToEnd());

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                logger.LogWarning("Pack {Path} manifest has no name, skipping", path);
                return;
            }

            var requiredTier = manifest.RequiresTier;
            if (requiredTier.Equals("commercial", StringComparison.OrdinalIgnoreCase) && !capabilities.CanWrite)
            {
                logger.LogInformation(
                    "Pack {Name} requires commercial tier, skipping on {Tier}",
                    manifest.Name, capabilities.Tier);
                _skipped.Add(manifest);
                return;
            }

            if (!string.IsNullOrWhiteSpace(manifest.EntryType) && !string.IsNullOrWhiteSpace(manifest.Assembly))
            {
                LoadAssemblyPack(zip, manifest);
            }

            _loaded.Add(manifest);
            logger.LogInformation("Loaded pack {Name} v{Version}", manifest.Name, manifest.Version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load pack {Path}", path);
            if (manifest != null) _skipped.Add(manifest);
        }
    }

    private void LoadAssemblyPack(ZipArchive zip, PackManifest manifest)
    {
        var dllEntry = zip.GetEntry(manifest.Assembly!);
        if (dllEntry == null)
        {
            logger.LogWarning("Pack {Name} declares assembly {Assembly} but it is not in the zip", manifest.Name, manifest.Assembly);
            return;
        }

        var tempDll = Path.Combine(Path.GetTempPath(), $"{manifest.Name}_{Guid.NewGuid():N}.dll");
        try
        {
            using (var fs = File.Create(tempDll))
            using (var entryStream = dllEntry.Open())
                entryStream.CopyTo(fs);

            var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(tempDll);
            var entryType = asm.GetType(manifest.EntryType!);
            if (entryType == null || !typeof(IStylobotPack).IsAssignableFrom(entryType))
            {
                logger.LogWarning("Pack {Name} entry type {Type} not found or does not implement IStylobotPack", manifest.Name, manifest.EntryType);
                return;
            }

            var pack = (IStylobotPack)Activator.CreateInstance(entryType)!;
            pack.ConfigureServices(services, capabilities);
            logger.LogInformation("Pack {Name} configured services", manifest.Name);
        }
        finally
        {
            try { File.Delete(tempDll); } catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackLoaderTests" -v minimal`
Expected: PASS (4 tests).

- [ ] **Step 7: Run full test suite to verify no regressions**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ -v minimal`
Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Packs/ \
        src/Mostlylucid.BotDetection.Test/Packs/PackLoaderTests.cs
git commit -m "feat(packs): add IStylobotPack, PackManifest, PackLoader"
```

---

## Task 4: Wire DegradationAtom and ReactionPackContext to hook interfaces

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/DegradationAtom.cs`
- Modify: `src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs`
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/PostResponseHookTests.cs`

Context: `DegradationAtom` already records upstream response health; it just needs to implement `IStylobotPostResponseHook` so the middleware can call it via the interface. `ReactionPackContext` already has `GetOverridePolicy`; it needs to implement `IStylobotPreActionHook`. The middleware must then consume `IEnumerable<IStylobotPreActionHook>` instead of the concrete `IReactionPackContext`.

- [ ] **Step 1: Write failing test for DegradationAtom as post-response hook**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/PostResponseHookTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class PostResponseHookTests
{
    [Fact]
    public async Task DegradationAtom_IsPostResponseHook()
    {
        var atom = new DegradationAtom();
        Assert.IsAssignableFrom<IStylobotPostResponseHook>(atom);
        await atom.OnResponseCompletedAsync(
            new ResponseContext(200, 50, "/api/test", "logonly"),
            CancellationToken.None);
        atom.Dispose();
    }

    [Fact]
    public async Task DegradationAtom_Records5xx_ViaHookInterface()
    {
        var atom = new DegradationAtom(windowSeconds: 60, emaAlpha: 1.0);
        var hook = (IStylobotPostResponseHook)atom;

        await hook.OnResponseCompletedAsync(
            new ResponseContext(500, 100, "/api/test", null),
            CancellationToken.None);

        Assert.True(atom.GetSignalValue("response.error_rate_5xx") > 0);
        atom.Dispose();
    }

    [Fact]
    public async Task ReactionPackContext_IsPreActionHook()
    {
        var ctx = new ReactionPackContext();
        Assert.IsAssignableFrom<IStylobotPreActionHook>(ctx);
        var result = await ctx.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run tests to verify fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PostResponseHookTests" -v minimal`
Expected: FAIL - `OnResponseCompletedAsync` not found.

- [ ] **Step 3: Add IStylobotPostResponseHook to DegradationAtom**

In `src/Mostlylucid.BotDetection/Services/DegradationAtom.cs`, change the class declaration from:
```csharp
public sealed class DegradationAtom : IDisposable
```
to:
```csharp
public sealed class DegradationAtom : IStylobotPostResponseHook, IDisposable
```

Then add this method to the class (calls the existing `RecordResponse` method internally):
```csharp
public ValueTask OnResponseCompletedAsync(ResponseContext context, CancellationToken ct)
{
    RecordResponse(context.StatusCode, context.LatencyMs, context.Path);
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Add IStylobotPreActionHook to ReactionPackContext**

In `src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs`, change the class declaration from:
```csharp
public sealed class ReactionPackContext : IReactionPackContext
```
to:
```csharp
public sealed class ReactionPackContext : IReactionPackContext, IStylobotPreActionHook
```

Add the Priority property and async wrapper (delegates to existing sync method):
```csharp
public int Priority => 100;

public ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct)
    => ValueTask.FromResult(GetOverridePolicy(endpoint, currentPolicy));
```

- [ ] **Step 5: Verify tests pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PostResponseHookTests" -v minimal`
Expected: PASS (3 tests).

- [ ] **Step 6: Register PackRegistry singletons and update ServiceCollectionExtensions**

In `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`, find the `RegisterCoreServices` method and add PackRegistry registrations before the reaction pack section. The registries must be registered as both their concrete type AND `IEnumerable<T>` so DI and pack system both work:

```csharp
// Pack system registries (must be registered before any pack services)
services.AddSingleton<Packs.PackRegistry<IStylobotPreActionHook>>();
services.AddSingleton<IEnumerable<IStylobotPreActionHook>>(
    sp => sp.GetRequiredService<Packs.PackRegistry<IStylobotPreActionHook>>());

services.AddSingleton<Packs.PackRegistry<IStylobotPostResponseHook>>();
services.AddSingleton<IEnumerable<IStylobotPostResponseHook>>(
    sp => sp.GetRequiredService<Packs.PackRegistry<IStylobotPostResponseHook>>());
```

Then update the existing reaction pack registrations (they are currently `AddSingleton<DegradationAtom>` and `AddSingleton<ReactionPackContext>`). After these singletons are registered, add them to the registries. The cleanest approach is to use `IStartupFilter` or register an `IHostedService` that populates the registries on startup. However, since these are built-in services (not external packs), the simplest approach is to use `AddSingleton` overload that resolves and registers:

Replace the existing:
```csharp
services.AddSingleton<Services.DegradationAtom>();
services.AddSingleton<Services.ReactionPackContext>();
services.AddSingleton<Services.IReactionPackContext>(sp => sp.GetRequiredService<Services.ReactionPackContext>());
```

With:
```csharp
services.AddSingleton<Services.DegradationAtom>();
services.AddSingleton<Services.IStylobotPostResponseHook>(
    sp => sp.GetRequiredService<Services.DegradationAtom>());

services.AddSingleton<Services.ReactionPackContext>();
services.AddSingleton<Services.IReactionPackContext>(
    sp => sp.GetRequiredService<Services.ReactionPackContext>());
services.AddSingleton<Services.IStylobotPreActionHook>(
    sp => sp.GetRequiredService<Services.ReactionPackContext>());
```

Note: the `IEnumerable<T>` registered above will be populated by a startup populator. Add a `BuiltinPackPopulator` hosted service (registered in this same block) that adds the singletons to the registries:

```csharp
services.AddHostedService<Packs.BuiltinPackPopulator>();
```

- [ ] **Step 7: Create BuiltinPackPopulator**

```csharp
// src/Mostlylucid.BotDetection/Packs/BuiltinPackPopulator.cs
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.Packs;

using Mostlylucid.BotDetection.Services;

internal sealed class BuiltinPackPopulator(
    PackRegistry<IStylobotPreActionHook> preActionRegistry,
    PackRegistry<IStylobotPostResponseHook> postResponseRegistry,
    IEnumerable<IStylobotPreActionHook> preActionHooks,
    IEnumerable<IStylobotPostResponseHook> postResponseHooks) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var hook in preActionHooks.OrderByDescending(h => h.Priority))
            preActionRegistry.Add(hook);
        foreach (var hook in postResponseHooks)
            postResponseRegistry.Add(hook);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Wait -- this creates a circular dependency because `IEnumerable<IStylobotPreActionHook>` resolves to `PackRegistry<IStylobotPreActionHook>` which is what we're populating. Instead, resolve the concrete singletons directly:

```csharp
// src/Mostlylucid.BotDetection/Packs/BuiltinPackPopulator.cs
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Packs;

internal sealed class BuiltinPackPopulator(
    PackRegistry<IStylobotPreActionHook> preActionRegistry,
    PackRegistry<IStylobotPostResponseHook> postResponseRegistry,
    ReactionPackContext reactionPackContext,
    DegradationAtom degradationAtom) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        preActionRegistry.Add(reactionPackContext);
        postResponseRegistry.Add(degradationAtom);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

And remove the `IStylobotPreActionHook`/`IStylobotPostResponseHook` singleton registrations from step 6 (we don't need them since the populator handles it). The `ServiceCollectionExtensions` update is just:

```csharp
// Pack system registries
services.AddSingleton<Packs.PackRegistry<Services.IStylobotPreActionHook>>();
services.AddSingleton<IEnumerable<Services.IStylobotPreActionHook>>(
    sp => sp.GetRequiredService<Packs.PackRegistry<Services.IStylobotPreActionHook>>());

services.AddSingleton<Packs.PackRegistry<Services.IStylobotPostResponseHook>>();
services.AddSingleton<IEnumerable<Services.IStylobotPostResponseHook>>(
    sp => sp.GetRequiredService<Packs.PackRegistry<Services.IStylobotPostResponseHook>>());

// Reaction pack services (built-in pack)
services.AddSingleton<Services.DegradationAtom>();
services.AddSingleton<Services.ReactionPackContext>();
services.AddSingleton<Services.IReactionPackContext>(
    sp => sp.GetRequiredService<Services.ReactionPackContext>());
services.AddSingleton<Services.ReactionRuleEvaluator>();
services.AddSingleton<Data.ReactionPackTransitionStore>();
services.AddHostedService<Services.ReactionPackEngine>();
services.AddHostedService<Packs.BuiltinPackPopulator>();
```

- [ ] **Step 8: Update BotDetectionMiddleware to use hook interfaces**

In `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`, the middleware's `InvokeAsync` method receives its dependencies as parameters (it uses the service locator pattern via method injection). The current signature has `IActionPolicyRegistry actionPolicyRegistry` and the middleware resolves action policies directly.

Find the section in `InvokeAsync` starting at line ~438 where `aggregatedResult.TriggeredActionPolicyName` is resolved. Before this block, add the pre-action hook evaluation. The hook should override `aggregatedResult.TriggeredActionPolicyName` if a non-null value is returned.

Add `IEnumerable<IStylobotPreActionHook>? preActionHooks = null` to the `InvokeAsync` parameter list (optional, so existing callers aren't broken):

```csharp
public async Task InvokeAsync(
    HttpContext context,
    // ... existing params ...
    IEnumerable<Services.IStylobotPreActionHook>? preActionHooks = null)
```

Then after line ~437 (after `if (_options.ResponseHeaders.Enabled)`), insert:

```csharp
// Pre-action hook: reaction packs and other pack extensions can override the action policy
if (preActionHooks != null && !string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
{
    var endpoint = context.Request.Path.Value ?? "";
    var currentPolicy = aggregatedResult.TriggeredActionPolicyName;
    foreach (var hook in preActionHooks.OrderByDescending(h => h.Priority))
    {
        var overridePolicy = await hook.GetOverridePolicyAsync(endpoint, currentPolicy, context.RequestAborted);
        if (overridePolicy != null)
        {
            aggregatedResult = aggregatedResult with { TriggeredActionPolicyName = overridePolicy };
            break;
        }
    }
}
```

For the post-response hook, find the `context.Response.OnCompleted` callback (around line 356). At the end of the callback body (before `return Task.CompletedTask`), add the post-response hook calls. Note: `OnCompleted` is a sync callback. Since `OnResponseCompletedAsync` is async, fire-and-forget is fine here (same pattern as `responseCoordinator.RecordResponseAsync`):

The middleware constructor already accepts optional parameters. Add:
```csharp
IEnumerable<Services.IStylobotPostResponseHook>? postResponseHooks = null
```

Then inside the `OnCompleted` callback, after the `_reactiveTracker` block:
```csharp
if (postResponseHooks != null)
{
    var rc = new Services.ResponseContext(statusCode, (long)processingTimeMs, capturedReq.Path, null);
    foreach (var hook in postResponseHooks)
        _ = hook.OnResponseCompletedAsync(rc, CancellationToken.None);
}
```

- [ ] **Step 9: Build to verify compilation**

Run: `dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -v minimal`
Expected: 0 errors, 0 warnings.

- [ ] **Step 10: Run full test suite**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ -v minimal`
Expected: All tests pass (at least 1379 + 3 new = 1382 passing).

- [ ] **Step 11: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/DegradationAtom.cs \
        src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs \
        src/Mostlylucid.BotDetection/Packs/BuiltinPackPopulator.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection.Test/Services/PostResponseHookTests.cs
git commit -m "feat(packs): wire hook interfaces into middleware; built-in pack populator"
```

---

## Task 5: Add all-packs query overload and ReactionPackDashboardService

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs`
- Create: `src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/ReactionPackDashboardServiceTests.cs`

Context: `ReactionPackTransitionStore.GetRecentTransitionsAsync(packName, limit)` is pack-specific; the dashboard needs all packs. `ReactionPackContext.GetActiveStates()` already exists and returns `IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)>` tuples (the `ActivePackState` record is private to that class). `ReactionPackTransition.OccurredAt` is already `DateTimeOffset` (converted on read from SQLite integer). The JSON response pattern in `StyloBotDashboardMiddleware` is: set `context.Response.ContentType = "application/json"`, then `await context.Response.WriteAsync(JsonSerializer.Serialize(obj, CamelCaseJson))`.

- [ ] **Step 1: Add all-packs overload to ReactionPackTransitionStore**

In `src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs`, add this method after `GetRecentTransitionsAsync`:

```csharp
public async Task<IReadOnlyList<ReactionPackTransition>> GetAllRecentTransitionsAsync(
    int limit = 50, CancellationToken ct = default)
{
    var (conn, owned) = await GetConnectionAsync(ct);
    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pack_name, from_level, to_level, triggered_by, signal_value, occurred_at
            FROM reaction_pack_transitions
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ReactionPackTransition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new ReactionPackTransition(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetDouble(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
        return results;
    }
    finally { if (owned) await conn.DisposeAsync(); }
}
```

- [ ] **Step 2: Define the dashboard model and service**

```csharp
// src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed record ReactionPackStatusEntry(
    string PackName,
    int CurrentLevel,
    string? CurrentLevelName,
    string? CurrentPolicy,
    string Scope);

public sealed record ReactionPackTransitionEntry(
    string PackName,
    int FromLevel,
    int ToLevel,
    string TriggeredBy,
    double SignalValue,
    DateTimeOffset OccurredAt);

public sealed record ReactionPackDashboardModel(
    IReadOnlyList<ReactionPackStatusEntry> ActivePacks,
    IReadOnlyList<ReactionPackStatusEntry> InactivePacks,
    IReadOnlyList<ReactionPackTransitionEntry> RecentTransitions);

public sealed class ReactionPackDashboardService(
    IReactionPackContext packContext,
    ReactionPackTransitionStore transitionStore,
    IEnumerable<ReactionPackDefinition> packDefinitions)
{
    public async Task<ReactionPackDashboardModel> GetDashboardModelAsync(CancellationToken ct = default)
    {
        // GetActiveStates() returns IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)>
        var activeStates = packContext.GetActiveStates();
        var activeNames = activeStates.Select(s => s.PackName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activePacks = activeStates
            .Select(s =>
            {
                var def = packDefinitions.FirstOrDefault(d =>
                    string.Equals(d.Name, s.PackName, StringComparison.OrdinalIgnoreCase));
                var step = def?.Steps.FirstOrDefault(st => st.Level == s.Level);
                return new ReactionPackStatusEntry(
                    s.PackName, s.Level, step?.Name, s.PolicyName, s.Scope);
            })
            .OrderByDescending(p => p.CurrentLevel)
            .ToList();

        var inactivePacks = packDefinitions
            .Where(d => !activeNames.Contains(d.Name) && d.Enabled)
            .Select(d => new ReactionPackStatusEntry(d.Name, 0, null, null,
                d.IsGlobal ? "global" : (d.ScopedEndpoint ?? "global")))
            .ToList();

        var transitions = await transitionStore.GetAllRecentTransitionsAsync(50, ct);
        var recentTransitions = transitions
            .Select(t => new ReactionPackTransitionEntry(
                t.PackName, t.FromLevel, t.ToLevel, t.TriggeredBy, t.SignalValue, t.OccurredAt))
            .ToList();

        return new ReactionPackDashboardModel(activePacks, inactivePacks, recentTransitions);
    }
}
```

- [ ] **Step 3: Write failing test for dashboard service**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/ReactionPackDashboardServiceTests.cs
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackDashboardServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ReactionPackTransitionStore _store;
    private readonly ReactionPackContext _context;

    public ReactionPackDashboardServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE reaction_pack_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pack_name TEXT NOT NULL,
                from_level INTEGER NOT NULL,
                to_level INTEGER NOT NULL,
                triggered_by TEXT NOT NULL,
                signal_value REAL NOT NULL,
                occurred_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _store = new ReactionPackTransitionStore(_conn);
        _context = new ReactionPackContext();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task GetDashboardModel_NoPacks_ReturnsEmpty()
    {
        var svc = new ReactionPackDashboardService(_context, _store, []);
        var model = await svc.GetDashboardModelAsync();
        Assert.Empty(model.ActivePacks);
        Assert.Empty(model.InactivePacks);
        Assert.Empty(model.RecentTransitions);
    }

    [Fact]
    public async Task GetDashboardModel_InactivePack_AppearsInInactive()
    {
        var def = new ReactionPackDefinition
        {
            Name = "test-pack",
            Enabled = true,
            Scope = "global",
            Steps = []
        };
        var svc = new ReactionPackDashboardService(_context, _store, [def]);
        var model = await svc.GetDashboardModelAsync();
        Assert.Empty(model.ActivePacks);
        Assert.Single(model.InactivePacks);
        Assert.Equal("test-pack", model.InactivePacks[0].PackName);
    }

    [Fact]
    public async Task GetDashboardModel_TransitionsAppear_AcrossAllPacks()
    {
        await _store.RecordTransitionAsync("pack-a", 0, 1, "response.error_rate_5xx", 0.07);
        await _store.RecordTransitionAsync("pack-b", 0, 1, "response.rate_429", 0.04);
        var svc = new ReactionPackDashboardService(_context, _store, []);
        var model = await svc.GetDashboardModelAsync();
        Assert.Equal(2, model.RecentTransitions.Count);
    }
}
```

- [ ] **Step 4: Run tests to verify fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~ReactionPackDashboardServiceTests" -v minimal`
Expected: FAIL - `ReactionPackDashboardService` not found / `GetAllRecentTransitionsAsync` not found.

- [ ] **Step 5: Register ReactionPackDashboardService in UI project**

Find where UI services are registered. Look for `src/Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs` or the equivalent registration file. Add:
```csharp
services.AddSingleton<ReactionPackDashboardService>();
```

The `IEnumerable<ReactionPackDefinition>` is registered in core `BotDetection` via `ServiceCollectionExtensions.RegisterCoreServices`. Since the UI project already references the core project, it resolves from the same DI container.

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~ReactionPackDashboardServiceTests" -v minimal`
Expected: PASS (3 tests).

- [ ] **Step 7: Add API endpoint in StyloBotDashboardMiddleware**

In `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`, find where path constants are defined (the section near the top of the main handler method around line 756 where `var tab = ...`). Find a similar pattern like:
```csharp
var summaryPath = $"{basePath}/api/summary";
```
Then add:
```csharp
var reactionPacksPath = $"{basePath}/api/reaction-packs";
```

Then in the same if-chain that routes API paths, add:
```csharp
if (path.Equals(reactionPacksPath, StringComparison.OrdinalIgnoreCase))
{
    var svc = context.RequestServices.GetRequiredService<ReactionPackDashboardService>();
    var data = await svc.GetDashboardModelAsync(context.RequestAborted);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(
        System.Text.Json.JsonSerializer.Serialize(data, CamelCaseJson), context.RequestAborted);
    return;
}
```

Note: `CamelCaseJson` is a static field `private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };` -- it's already defined in the middleware class (line ~112).

- [ ] **Step 8: Build to verify compilation**

Run: `dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -v minimal`
Expected: 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs \
        src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs \
        src/Mostlylucid.BotDetection.Test/Services/ReactionPackDashboardServiceTests.cs
git commit -m "feat(packs): reaction pack dashboard service and API endpoint"
```

---

## Task 6: Dashboard tab view and navigation

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_ReactionPacksTab.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

Context: The dashboard's `Index.cshtml` has a hardcoded tab list. Reaction Packs tab is visible to all tiers (FOSS-read-only view is fine here). The tab renders server-side via HTMX partial (same pattern as other tabs). Data flows from `ReactionPackDashboardService` through the middleware to the partial view.

- [ ] **Step 1: Create the partial view**

```cshtml
@* src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_ReactionPacksTab.cshtml *@
@model Mostlylucid.BotDetection.UI.Services.ReactionPackDashboardModel

<div class="space-y-4">

    @* Active packs *@
    <div class="card bg-base-100 shadow-sm">
        <div class="card-body p-4">
            <h3 class="text-sm font-semibold mb-3">Active Protection</h3>
            @if (!Model.ActivePacks.Any())
            {
                <p class="text-xs text-base-content/50">No packs currently active. All signals within normal thresholds.</p>
            }
            else
            {
                <div class="space-y-2">
                    @foreach (var pack in Model.ActivePacks)
                    {
                        var levelColor = pack.CurrentLevel >= 3 ? "badge-error"
                            : pack.CurrentLevel == 2 ? "badge-warning"
                            : "badge-info";
                        <div class="flex items-center justify-between py-2 border-b border-base-200 last:border-0">
                            <div>
                                <span class="text-sm font-medium">@pack.PackName</span>
                                <span class="text-xs text-base-content/50 ml-2">@pack.Scope</span>
                            </div>
                            <div class="flex items-center gap-2">
                                <span class="badge badge-sm @levelColor">Level @pack.CurrentLevel: @(pack.CurrentLevelName ?? "active")</span>
                                @if (!string.IsNullOrEmpty(pack.CurrentPolicy))
                                {
                                    <span class="badge badge-sm badge-ghost text-xs font-mono">@pack.CurrentPolicy</span>
                                }
                            </div>
                        </div>
                    }
                </div>
            }
        </div>
    </div>

    @* Inactive packs *@
    @if (Model.InactivePacks.Any())
    {
        <div class="card bg-base-100 shadow-sm">
            <div class="card-body p-4">
                <h3 class="text-sm font-semibold mb-3">Configured Packs (Inactive)</h3>
                <div class="space-y-1">
                    @foreach (var pack in Model.InactivePacks)
                    {
                        <div class="flex items-center justify-between py-1.5 text-xs text-base-content/60">
                            <span>@pack.PackName</span>
                            <span class="badge badge-xs badge-ghost">@pack.Scope</span>
                        </div>
                    }
                </div>
            </div>
        </div>
    }

    @* Transition timeline *@
    <div class="card bg-base-100 shadow-sm">
        <div class="card-body p-4">
            <h3 class="text-sm font-semibold mb-3">Transition History</h3>
            @if (!Model.RecentTransitions.Any())
            {
                <p class="text-xs text-base-content/50">No transitions recorded yet.</p>
            }
            else
            {
                <table class="table table-xs w-full">
                    <thead>
                        <tr>
                            <th class="text-left">Pack</th>
                            <th class="text-left">Transition</th>
                            <th class="text-left">Signal</th>
                            <th class="text-right">When</th>
                        </tr>
                    </thead>
                    <tbody>
                        @foreach (var t in Model.RecentTransitions)
                        {
                            var dirClass = t.ToLevel > t.FromLevel ? "text-warning" : "text-success";
                            var dirIcon = t.ToLevel > t.FromLevel ? "bx-up-arrow-alt" : "bx-down-arrow-alt";
                            <tr>
                                <td class="font-mono text-xs">@t.PackName</td>
                                <td>
                                    <span class="@dirClass flex items-center gap-1">
                                        <i class="bx @dirIcon"></i>
                                        @t.FromLevel → @t.ToLevel
                                    </span>
                                </td>
                                <td class="text-xs text-base-content/60">
                                    @(t.TriggeredBy ?? "-") (@t.SignalValue.ToString("P1"))
                                </td>
                                <td class="text-right text-xs text-base-content/50">
                                    @t.OccurredAt.ToLocalTime().ToString("HH:mm:ss")
                                </td>
                            </tr>
                        }
                    </tbody>
                </table>
            }
        </div>
    </div>

</div>
```

- [ ] **Step 2: Add tab to Index.cshtml navigation**

In `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`, find the tab navigation block (lines ~176-192). Add the Reaction Packs tab after "Threats" and before "User Agents":

```cshtml
<a href="@TabUrl("reaction-packs")" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass("reaction-packs")">Reaction Packs</a>
```

Place it between the `threats` and `useragents` tab links.

- [ ] **Step 3: Add tab rendering in Index.cshtml body**

In `Index.cshtml`, find the large `@if (tab == "overview")` block and find where the other tabs are rendered. Each tab has an `else if` branch. Add after the threats rendering block but before useragents:

```cshtml
else if (tab == "reaction-packs")
{
    @await Html.PartialAsync("~/Views/StyloBot/Dashboard/_ReactionPacksTab.cshtml", Model.ReactionPacks)
}
```

This requires `DashboardShellModel` to have a `ReactionPacks` property.

- [ ] **Step 4: Add ReactionPacks to DashboardShellModel**

In `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`, find `DashboardShellModel` (around line 340). Add:

```csharp
public ReactionPackDashboardModel? ReactionPacks { get; init; }
```

Add the using at the top of the file:
```csharp
using Mostlylucid.BotDetection.UI.Services;
```

- [ ] **Step 5: Populate ReactionPacks in the middleware shell builder**

In `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`, find where `DashboardShellModel` is constructed (around line 822). Add the reaction packs data population:

Before the model construction, add:
```csharp
ReactionPackDashboardModel? reactionPacksModel = null;
if (tab.Equals("reaction-packs", StringComparison.OrdinalIgnoreCase))
{
    var rpSvc = context.RequestServices.GetService<ReactionPackDashboardService>();
    if (rpSvc != null)
        reactionPacksModel = await rpSvc.GetDashboardModelAsync(context.RequestAborted);
    else
        reactionPacksModel = new ReactionPackDashboardModel([], [], []);
}
```

Then in the model initializer, add:
```csharp
ReactionPacks = reactionPacksModel,
```

- [ ] **Step 6: Build the solution**

Run: `dotnet build mostlylucid.stylobot.sln -v minimal`
Expected: 0 errors.

- [ ] **Step 7: Run full test suite**

Run: `dotnet test -v minimal`
Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_ReactionPacksTab.cshtml \
        src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml \
        src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs \
        src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs
git commit -m "feat(packs): reaction packs dashboard tab"
```

---

## Final verification

- [ ] **Build and test the entire solution**

```bash
dotnet build mostlylucid.stylobot.sln -v minimal
dotnet test -v minimal
```

Expected: 0 build errors, all tests pass.
