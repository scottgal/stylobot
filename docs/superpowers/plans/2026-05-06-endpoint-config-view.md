# Endpoint Configuration View + Path Pinning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show configured policy and reaction pack coverage per endpoint in the FOSS dashboard (read-only), and let operators pin any path to the endpoint list before traffic arrives.

**Architecture:** Pinned endpoints stored in SQLite (`pinned_endpoints` table), merged with traffic-based endpoints at query time. Policy and reaction pack state injected into endpoint models from existing `IPolicyRegistry` and `IReactionPackContext`. The honeypot flag on a pinned endpoint is a read surface for simulation packs.

**Tech Stack:** ASP.NET Core, SQLite (Microsoft.Data.Sqlite), HTMX, DaisyUI/Tailwind, Razor partials, IReactionPackContext, IPolicyRegistry.

---

## File Map

| Action | File |
|--------|------|
| Create | `src/Mostlylucid.BotDetection/Data/IPinnedEndpointStore.cs` |
| Create | `src/Mostlylucid.BotDetection/Data/SqlitePinnedEndpointStore.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Models/DashboardEndpointStats.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsCompact.cshtml` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointDetail.cshtml` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` |
| Create | `src/Mostlylucid.BotDetection.Test/Data/SqlitePinnedEndpointStoreTests.cs` |

---

### Task 1: IPinnedEndpointStore interface + PinnedEndpoint record

**Files:**
- Create: `src/Mostlylucid.BotDetection/Data/IPinnedEndpointStore.cs`

- [ ] **Step 1: Create the interface file**

```csharp
// src/Mostlylucid.BotDetection/Data/IPinnedEndpointStore.cs
namespace Mostlylucid.BotDetection.Data;

public sealed record PinnedEndpoint(
    long Id,
    string Method,
    string Path,
    bool IsHoneypot,
    string? Note,
    DateTimeOffset CreatedAt);

public interface IPinnedEndpointStore
{
    Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default);
    Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default);
    Task<bool> RemoveAsync(long id, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build to verify no errors**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/IPinnedEndpointStore.cs
git commit -m "feat(data): add IPinnedEndpointStore interface and PinnedEndpoint record"
```

---

### Task 2: SqlitePinnedEndpointStore implementation

**Files:**
- Create: `src/Mostlylucid.BotDetection/Data/SqlitePinnedEndpointStore.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Data/SqlitePinnedEndpointStoreTests.cs`

- [ ] **Step 1: Write failing tests first**

```csharp
// src/Mostlylucid.BotDetection.Test/Data/SqlitePinnedEndpointStoreTests.cs
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Data;

public class SqlitePinnedEndpointStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqlitePinnedEndpointStore _store;

    public SqlitePinnedEndpointStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqlitePinnedEndpointStore(_conn);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task GetAll_Empty_ReturnsEmptyList()
    {
        var result = await _store.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task Add_NewPin_ReturnsPin()
    {
        var pin = await _store.AddAsync("GET", "/config.php", false, "scanner bait");
        Assert.NotNull(pin);
        Assert.Equal("GET", pin!.Method);
        Assert.Equal("/config.php", pin.Path);
        Assert.False(pin.IsHoneypot);
        Assert.Equal("scanner bait", pin.Note);
        Assert.True(pin.Id > 0);
    }

    [Fact]
    public async Task Add_DuplicatePath_ReturnsExisting()
    {
        var first = await _store.AddAsync("GET", "/wp-login.php", true, null);
        var second = await _store.AddAsync("GET", "/wp-login.php", true, "updated note");
        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task GetAll_AfterAdd_ReturnsPin()
    {
        await _store.AddAsync("ANY", "/honeypot", true, null);
        var all = await _store.GetAllAsync();
        Assert.Single(all);
        Assert.True(all[0].IsHoneypot);
    }

    [Fact]
    public async Task Remove_ExistingPin_ReturnsTrue()
    {
        var pin = await _store.AddAsync("GET", "/admin.php", false, null);
        var removed = await _store.RemoveAsync(pin!.Id);
        Assert.True(removed);
        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public async Task Remove_NonExistentId_ReturnsFalse()
    {
        var removed = await _store.RemoveAsync(999);
        Assert.False(removed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqlitePinnedEndpointStoreTests" --no-build 2>&1 | head -20
```

Expected: FAIL — `SqlitePinnedEndpointStore` does not exist yet.

- [ ] **Step 3: Implement SqlitePinnedEndpointStore**

```csharp
// src/Mostlylucid.BotDetection/Data/SqlitePinnedEndpointStore.cs
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

public sealed class SqlitePinnedEndpointStore : IPinnedEndpointStore
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _existingConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqlitePinnedEndpointStore(IOptions<BotDetectionOptions> options)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        _connectionString = $"Data Source={Path.Combine(basePath, "sessions.db")};Cache=Shared";
        InitSchema();
    }

    internal SqlitePinnedEndpointStore(SqliteConnection existingConnection)
    {
        _connectionString = existingConnection.ConnectionString;
        _existingConnection = existingConnection;
        InitSchema();
    }

    private void InitSchema()
    {
        var (conn, owned) = GetConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS pinned_endpoints (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    method     TEXT NOT NULL DEFAULT 'ANY',
                    path       TEXT NOT NULL,
                    is_honeypot INTEGER NOT NULL DEFAULT 0,
                    note       TEXT,
                    created_at INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_pinned_endpoints_method_path
                    ON pinned_endpoints (method, path);
                """;
            cmd.ExecuteNonQuery();
        }
        finally { if (owned) conn.Dispose(); }
    }

    public async Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default)
    {
        var (conn, owned) = await GetConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, method, path, is_honeypot, note, created_at
                FROM pinned_endpoints
                ORDER BY created_at DESC
                """;
            var results = new List<PinnedEndpoint>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadPin(reader));
            return results;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    public async Task<PinnedEndpoint?> AddAsync(
        string method, string path, bool isHoneypot, string? note,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = await GetConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO pinned_endpoints (method, path, is_honeypot, note, created_at)
                    VALUES (@method, @path, @hon, @note, @at)
                    ON CONFLICT (method, path) DO NOTHING;

                    SELECT id, method, path, is_honeypot, note, created_at
                    FROM pinned_endpoints
                    WHERE method = @method AND path = @path;
                    """;
                cmd.Parameters.AddWithValue("@method", method);
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@hon", isHoneypot ? 1 : 0);
                cmd.Parameters.AddWithValue("@note", note ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    return ReadPin(reader);
                return null;
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = await GetConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM pinned_endpoints WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                return rows > 0;
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    private static PinnedEndpoint ReadPin(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.GetInt32(3) != 0, r.IsDBNull(4) ? null : r.GetString(4),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)));

    private (SqliteConnection conn, bool owned) GetConnection()
    {
        if (_existingConnection != null) return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return (conn, true);
    }

    private async Task<(SqliteConnection conn, bool owned)> GetConnectionAsync(CancellationToken ct)
    {
        if (_existingConnection != null) return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return (conn, true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqlitePinnedEndpointStoreTests"
```

Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/SqlitePinnedEndpointStore.cs \
        src/Mostlylucid.BotDetection.Test/Data/SqlitePinnedEndpointStoreTests.cs
git commit -m "feat(data): SqlitePinnedEndpointStore with schema init, add/remove/get, upsert dedup"
```

---

### Task 3: Model additions — DashboardEndpointStats, EndpointDetailModel, EndpointPackCoverage

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardEndpointStats.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`

- [ ] **Step 1: Add pin fields to DashboardEndpointStats**

In `src/Mostlylucid.BotDetection.UI/Models/DashboardEndpointStats.cs`, add to the `DashboardEndpointStats` record after `ActivePolicyName`:

```csharp
    public bool IsPinned { get; init; }
    public bool IsHoneypot { get; init; }
    public long? PinId { get; init; }
```

Also add the new `EndpointPackCoverage` record at the bottom of the same file:

```csharp
public sealed record EndpointPackCoverage(
    string PackName,
    string Scope,
    int CurrentLevel,
    string? CurrentPolicy);
```

- [ ] **Step 2: Add protection fields to EndpointDetailModel**

In `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`, add to `EndpointDetailModel` after `CspNonce`:

```csharp
    public string? PolicyName { get; init; }
    public IReadOnlyList<EndpointPackCoverage> PackCoverage { get; init; } = [];
    public bool IsPinned { get; init; }
    public bool IsHoneypot { get; init; }
    public long? PinId { get; init; }
```

- [ ] **Step 3: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Models/DashboardEndpointStats.cs \
        src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs
git commit -m "feat(models): add pin/honeypot fields to DashboardEndpointStats and EndpointDetailModel"
```

---

### Task 4: DI registration

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs`

- [ ] **Step 1: Register IPinnedEndpointStore in AddStyloBotDashboard**

In `StyloBotDashboardServiceExtensions.cs`, find the line:
```csharp
        // Reaction pack dashboard service - aggregates active pack states and recent transitions
        services.AddSingleton<ReactionPackDashboardService>();
```

Add after it:

```csharp
        // Pinned endpoint store - persists operator-added paths to SQLite
        services.TryAddSingleton<IPinnedEndpointStore, SqlitePinnedEndpointStore>();
```

Also add the using at the top of the file (if not present):

```csharp
using Mostlylucid.BotDetection.Data;
```

- [ ] **Step 2: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(di): register IPinnedEndpointStore in AddStyloBotDashboard"
```

---

### Task 5: Middleware — endpoint enrichment and new API routes

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

This task has three parts: enrichment logic in `GetEndpointsDataAsync`, a new `BuildEndpointDetailCoverage` helper, updates to `ServeEndpointDetailPartialAsync`, and three new API route handlers + their switch cases.

- [ ] **Step 1: Add `DataApiPaths` entries for pin routes**

Find the `DataApiPaths` HashSet (around line 84). It does not need updating — the pin API routes go into the switch directly. However, add them to the `DataApiPaths` set so the bot policy covers them:

```csharp
        "api/endpoint-pins",
```

(Add this line to the `DataApiPaths` HashSet alongside the other `"api/..."` entries.)

- [ ] **Step 2: Add three new switch cases for pin API routes**

In the main `switch (relLower)` block, after the `case "api/reaction-packs":` entry, add:

```csharp
            case "api/endpoint-pins":
                if (context.Request.Method == "GET")
                    await ServeEndpointPinsApiAsync(context);
                else if (context.Request.Method == "POST")
                    await HandlePinEndpointAsync(context);
                else
                    context.Response.StatusCode = 405;
                break;

            case var p when p.StartsWith("api/endpoint-pins/", StringComparison.OrdinalIgnoreCase):
                if (context.Request.Method == "DELETE")
                    await HandleUnpinEndpointAsync(context, relLower["api/endpoint-pins/".Length..]);
                else
                    context.Response.StatusCode = 405;
                break;
```

- [ ] **Step 3: Update GetEndpointsDataAsync to merge pinned endpoints**

Replace the current `GetEndpointsDataAsync` method body with:

```csharp
    private async Task<List<DashboardEndpointStats>> GetEndpointsDataAsync(HttpContext? httpContext = null)
    {
        var cached = _aggregateCache.Current.Endpoints;
        var endpoints = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(100);

        // Load pinned endpoints and build lookup
        IPinnedEndpointStore? pinStore = null;
        IReadOnlyList<PinnedEndpoint> pinned = [];
        if (httpContext != null)
        {
            pinStore = httpContext.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
            if (pinStore != null)
                pinned = await pinStore.GetAllAsync();
        }

        // Build a lookup: (method, path) -> PinnedEndpoint
        var pinnedLookup = pinned.ToDictionary(p => (p.Method.ToUpperInvariant(), p.Path), p => p);

        // Add pinned-only endpoints (zero traffic so far) at the front
        var trafficKeys = endpoints.Select(e => (e.Method.ToUpperInvariant(), e.Path)).ToHashSet();
        var pinnedOnly = pinned
            .Where(p => !trafficKeys.Contains((p.Method.ToUpperInvariant(), p.Path)))
            .Select(p => new DashboardEndpointStats
            {
                Method = p.Method,
                Path = p.Path,
                IsPinned = true,
                IsHoneypot = p.IsHoneypot,
                PinId = p.Id,
                LastSeen = p.CreatedAt.UtcDateTime
            })
            .ToList();

        endpoints = [.. pinnedOnly, .. endpoints];

        // Enrich all endpoints: pin flags + policy name
        if (httpContext != null)
        {
            var policyRegistry = httpContext.RequestServices
                .GetService(typeof(BotDetection.Policies.IPolicyRegistry))
                as BotDetection.Policies.IPolicyRegistry;

            endpoints = endpoints.Select(e =>
            {
                var key = (e.Method.ToUpperInvariant(), e.Path);
                var pin = pinnedLookup.GetValueOrDefault(key);
                return e with
                {
                    IsPinned = pin != null,
                    IsHoneypot = pin?.IsHoneypot ?? false,
                    PinId = pin?.Id,
                    ActivePolicyName = e.ActivePolicyName
                        ?? policyRegistry?.GetPolicyForPath(e.Path).Name
                };
            }).ToList();
        }

        return endpoints;
    }
```

- [ ] **Step 4: Add BuildEndpointDetailCoverage private method**

Add this new private method after `GetEndpointsDataAsync`:

```csharp
    private IReadOnlyList<EndpointPackCoverage> BuildEndpointDetailCoverage(HttpContext context, string path)
    {
        var packContext = context.RequestServices.GetService(typeof(IReactionPackContext)) as IReactionPackContext;
        var packDefs = context.RequestServices.GetService(typeof(IEnumerable<ReactionPackDefinition>))
            as IEnumerable<ReactionPackDefinition> ?? [];

        var coverage = new List<EndpointPackCoverage>();

        if (packContext == null) return coverage;

        var activeStates = packContext.GetActiveStates();
        var activeByName = activeStates.ToDictionary(s => s.PackName, s => s);

        // Include active packs where scope is global or matches this endpoint
        foreach (var state in activeStates)
        {
            if (state.Scope == "global" || state.Scope == path)
                coverage.Add(new EndpointPackCoverage(state.PackName, state.Scope, state.Level, state.PolicyName));
        }

        // Include configured-but-inactive packs scoped to this endpoint
        foreach (var def in packDefs)
        {
            if (activeByName.ContainsKey(def.Name)) continue;
            if (def.Scope == "global" || def.Scope == path)
                coverage.Add(new EndpointPackCoverage(def.Name, def.Scope ?? "global", 0, null));
        }

        return coverage;
    }
```

Note: `IReactionPackContext.GetActiveStates()` returns `IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)>`. The tuple field names are `PackName`, `Level`, `PolicyName`, `Scope` — check the actual interface definition if the names differ and adjust accordingly.

- [ ] **Step 5: Update ServeEndpointDetailPartialAsync to inject protection data**

After the line `var epNonce = ...` and before building the model, add:

```csharp
        var policyRegistry = context.RequestServices.GetService(typeof(BotDetection.Policies.IPolicyRegistry))
            as BotDetection.Policies.IPolicyRegistry;
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        var pinned = pinStore != null ? await pinStore.GetAllAsync() : (IReadOnlyList<PinnedEndpoint>)[];
        var pin = pinned.FirstOrDefault(p =>
            string.Equals(p.Method, method, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Path, path, StringComparison.Ordinal));
        var policyName = policyRegistry?.GetPolicyForPath(path).Name;
        var packCoverage = BuildEndpointDetailCoverage(context, path);
```

Then in both the `Found = false` and `Found = true` model construction, add the protection fields. For `Found = false`:

```csharp
            : new EndpointDetailModel
            {
                Method = method,
                Path = path,
                BasePath = _options.BasePath.TrimEnd('/'),
                Found = false,
                CspNonce = epNonce,
                PolicyName = policyName,
                PackCoverage = packCoverage,
                IsPinned = pin != null,
                IsHoneypot = pin?.IsHoneypot ?? false,
                PinId = pin?.Id
            }
```

For `Found = true` (after `OverallProfile = detail.OverallProfile`):

```csharp
                CspNonce = epNonce,
                PolicyName = policyName,
                PackCoverage = packCoverage,
                IsPinned = pin != null,
                IsHoneypot = pin?.IsHoneypot ?? false,
                PinId = pin?.Id
```

- [ ] **Step 6: Add three new API handler methods**

Add these three methods to the middleware class (e.g., after `ServeReactionPacksApiAsync`):

```csharp
    private async Task ServeEndpointPinsApiAsync(HttpContext context)
    {
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }
        var pins = await pinStore.GetAllAsync();
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, pins, CamelCaseJson);
    }

    private async Task HandlePinEndpointAsync(HttpContext context)
    {
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null) { context.Response.StatusCode = 503; return; }

        PinRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync<PinRequest>(
                context.Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Invalid JSON\"}");
            return;
        }

        if (req == null || string.IsNullOrWhiteSpace(req.Path) || !req.Path.StartsWith('/'))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path is required and must start with /\"}");
            return;
        }

        if (req.Path.Length > 500)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path too long\"}");
            return;
        }

        var method = string.IsNullOrWhiteSpace(req.Method) ? "ANY" : req.Method.ToUpperInvariant();
        var pin = await pinStore.AddAsync(method, req.Path, req.IsHoneypot, req.Note);
        context.Response.StatusCode = 201;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, pin, CamelCaseJson);
    }

    private async Task HandleUnpinEndpointAsync(HttpContext context, string idSegment)
    {
        if (!long.TryParse(idSegment, out var id))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null) { context.Response.StatusCode = 503; return; }

        var removed = await pinStore.RemoveAsync(id);
        context.Response.StatusCode = removed ? 204 : 404;
    }

    private sealed record PinRequest(string Path, string? Method, bool IsHoneypot, string? Note);
```

- [ ] **Step 7: Add required usings to the middleware file**

At the top of `StyloBotDashboardMiddleware.cs`, verify/add:

```csharp
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
```

(Both should already be present; add if missing.)

- [ ] **Step 8: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded.

- [ ] **Step 9: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(middleware): merge pinned endpoints, coverage enrichment, pin/unpin API routes"
```

---

### Task 6: UI — endpoint list policy badge and pin icons

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsCompact.cshtml`

This is the compact list used in overview sidebar slots. The full endpoints tab uses a different partial (`_Endpoints.cshtml` or the list via `ServeEndpointsPartialAsync`). The compact view needs pin/honeypot icons in the path cell and a Policy column. First locate the full endpoint list partial to update it too.

- [ ] **Step 1: Update _EndpointsCompact.cshtml — add Policy column and pin/honeypot icons**

Replace the entire file content with:

```cshtml
@using Mostlylucid.BotDetection.UI.Models
@model EndpointsListModel
@{
    var bp = Model.BasePath;
}
@if (Model.Endpoints.Count == 0)
{
    <div class="p-4 text-xs text-base-content/40 text-center">No endpoint data yet</div>
}
else
{
    <div class="overflow-x-auto">
        <table class="table table-xs w-full">
            <thead>
            <tr class="text-[10px] uppercase tracking-wider">
                <th class="py-1">Method</th>
                <th class="py-1">Path</th>
                <th class="py-1 text-right">Req</th>
                <th class="py-1 text-right">Bot %</th>
                <th class="py-1 text-right hidden sm:table-cell">Sigs</th>
                <th class="py-1 hidden md:table-cell">Policy</th>
            </tr>
            </thead>
            <tbody>
            @foreach (var endpoint in Model.Endpoints)
            {
                var botClass = endpoint.BotRate > 0.5 ? "text-error" : endpoint.BotRate > 0.2 ? "text-warning" : "text-success";
                <tr class="hover:bg-base-200/50 transition-colors cursor-pointer"
                    onclick="window.location='@(bp)?tab=endpoints'">
                    <td class="py-1 text-[10px] font-bold">@endpoint.Method</td>
                    <td class="py-1 text-xs font-medium max-w-[220px] truncate" title="@endpoint.Path">
                        @if (endpoint.IsPinned)
                        {
                            <i class="bx bx-pin text-base-content/30 mr-0.5" title="Pinned"></i>
                        }
                        @if (endpoint.IsHoneypot)
                        {
                            <i class="bx bx-bug text-warning mr-0.5" title="Honeypot"></i>
                        }
                        @endpoint.Path
                    </td>
                    <td class="py-1 text-right text-xs font-mono">@endpoint.TotalCount</td>
                    <td class="py-1 text-right text-xs font-bold @botClass">@((endpoint.BotRate * 100).ToString("F0"))%</td>
                    <td class="py-1 text-right text-xs font-mono hidden sm:table-cell">@endpoint.UniqueSignatures</td>
                    <td class="py-1 hidden md:table-cell">
                        @if (!string.IsNullOrEmpty(endpoint.ActivePolicyName))
                        {
                            <span class="badge badge-xs badge-ghost font-mono text-[9px]">@endpoint.ActivePolicyName</span>
                        }
                        else
                        {
                            <span class="text-[10px] text-base-content/30">—</span>
                        }
                    </td>
                </tr>
            }
            </tbody>
        </table>
    </div>
}
```

- [ ] **Step 2: Find the full endpoints list partial and apply same changes**

```bash
find /Users/scottgalloway/RiderProjects/stylobot/.worktrees/reaction-packs/src -name "_Endpoints*.cshtml" | grep -v Compact
```

If a separate full-list partial exists (e.g., `_EndpointsList.cshtml`), apply identical column additions. If the compact partial is the only one, this step is already done.

- [ ] **Step 3: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsCompact.cshtml
git commit -m "feat(ui): add Policy badge column and pin/honeypot icons to endpoint list"
```

---

### Task 7: UI — endpoint detail Protection section

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointDetail.cshtml`

- [ ] **Step 1: Replace the Bot Policy section with a Protection section**

In `_EndpointDetail.cshtml`, replace the entire block:

```cshtml
        <div class="mt-4 pt-3 border-t border-base-300/40">
            <div class="flex items-center justify-between text-xs">
                <span class="font-semibold text-base-content/70">Bot Policy</span>
                <button type="button"
                        class="btn btn-xs btn-ghost gap-1 text-base-content/60 hover:text-base-content"
                        onclick="sbOpenPolicyModal(event, '@Html.Raw(Model.Method.Replace("'", "\\'"))', '@Html.Raw(Uri.EscapeDataString(Model.Path))', '')">
                    <i class="bx bx-shield-alt text-sm"></i>Apply Policy
                </button>
            </div>
            <div class="text-[10px] text-base-content/40 mt-1">Configure how bots are handled on this endpoint</div>
        </div>
```

with:

```cshtml
        <div class="mt-4 pt-3 border-t border-base-300/40">
            <div class="text-[10px] uppercase tracking-wider text-base-content/40 mb-2">Protection</div>

            <div class="flex items-center gap-2 mb-3">
                <span class="text-xs text-base-content/60">Policy</span>
                @if (!string.IsNullOrEmpty(Model.PolicyName))
                {
                    <span class="badge badge-sm badge-ghost font-mono text-xs">@Model.PolicyName</span>
                }
                else
                {
                    <span class="text-xs text-base-content/30">default</span>
                }
            </div>

            @if (Model.PackCoverage.Count > 0)
            {
                <div class="text-[10px] uppercase tracking-wider text-base-content/40 mb-1">Reaction Packs</div>
                <div class="space-y-1">
                    @foreach (var pack in Model.PackCoverage)
                    {
                        var levelBadge = pack.CurrentLevel == 0 ? "badge-ghost"
                            : pack.CurrentLevel >= 3 ? "badge-error"
                            : pack.CurrentLevel == 2 ? "badge-warning"
                            : "badge-info";
                        <div class="flex items-center justify-between py-1 text-xs">
                            <span class="font-mono text-base-content/70 truncate max-w-[140px]" title="@pack.PackName">@pack.PackName</span>
                            <div class="flex items-center gap-1 shrink-0">
                                <span class="badge badge-xs badge-ghost">@pack.Scope</span>
                                <span class="badge badge-xs @levelBadge">
                                    @if (pack.CurrentLevel == 0)
                                    {
                                        <text>inactive</text>
                                    }
                                    else
                                    {
                                        <text>L@(pack.CurrentLevel)</text>
                                        if (!string.IsNullOrEmpty(pack.CurrentPolicy))
                                        {
                                            <text>: @pack.CurrentPolicy</text>
                                        }
                                    }
                                </span>
                            </div>
                        </div>
                    }
                </div>
            }

            @if (Model.IsPinned)
            {
                <div class="mt-3 flex items-center justify-between">
                    <div class="flex items-center gap-1 text-xs text-base-content/50">
                        <i class="bx bx-pin"></i>
                        <span>Pinned</span>
                        @if (Model.IsHoneypot)
                        {
                            <span class="badge badge-xs badge-warning ml-1">honeypot</span>
                        }
                    </div>
                    @if (Model.PinId.HasValue)
                    {
                        <button class="btn btn-xs btn-ghost text-error/60 hover:text-error"
                                hx-delete="@(Model.BasePath)/api/endpoint-pins/@Model.PinId"
                                hx-confirm="Remove pin for @Model.Path?"
                                hx-target="closest [data-endpoint-detail]"
                                hx-swap="outerHTML">
                            <i class="bx bx-unlink text-sm"></i>Unpin
                        </button>
                    }
                </div>
            }
        </div>
```

- [ ] **Step 2: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointDetail.cshtml
git commit -m "feat(ui): replace Bot Policy section with Protection section (policy + reaction packs + pin controls)"
```

---

### Task 8: UI — Pin Endpoint form in endpoints tab

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

- [ ] **Step 1: Find the endpoints tab header in Index.cshtml**

Search for where the endpoints tab content is rendered to locate the right insertion point:

```bash
grep -n "tab.*endpoint\|endpoint.*tab\|endpoints.*card\|card.*endpoint" \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml | head -20
```

- [ ] **Step 2: Add Pin Endpoint button and inline form**

Locate the endpoints tab card header. Find the `<div class="card-body ...">` that wraps the endpoint list header (look for `"Endpoints"` heading text). After the heading, add:

```cshtml
<div class="flex items-center justify-between mb-3">
    <h3 class="text-sm font-semibold">Endpoints</h3>
    <button class="btn btn-xs btn-ghost gap-1 text-base-content/60"
            onclick="document.getElementById('pin-endpoint-form').classList.toggle('hidden')">
        <i class="bx bx-pin text-sm"></i>Pin Endpoint
    </button>
</div>

<div id="pin-endpoint-form" class="hidden mb-4 p-3 bg-base-200 rounded-lg">
    <form hx-post="@(bp)/api/endpoint-pins"
          hx-target="#endpoint-list-container"
          hx-swap="innerHTML"
          hx-on::after-request="this.reset(); document.getElementById('pin-endpoint-form').classList.add('hidden')"
          class="space-y-2">
        <div class="flex gap-2">
            <select name="method" class="select select-xs select-bordered w-28 font-mono">
                <option value="ANY">ANY</option>
                <option value="GET">GET</option>
                <option value="POST">POST</option>
                <option value="PUT">PUT</option>
                <option value="DELETE">DELETE</option>
                <option value="PATCH">PATCH</option>
            </select>
            <input name="path" type="text" placeholder="/config.php"
                   class="input input-xs input-bordered flex-1 font-mono"
                   required pattern="\/.*" maxlength="500" />
        </div>
        <div class="flex items-center gap-4">
            <label class="flex items-center gap-1.5 text-xs cursor-pointer">
                <input type="checkbox" name="isHoneypot" value="true" class="checkbox checkbox-xs" />
                Mark as honeypot
            </label>
            <input name="note" type="text" placeholder="Optional note"
                   class="input input-xs input-bordered flex-1" maxlength="200" />
        </div>
        <div class="flex gap-2 justify-end">
            <button type="button" class="btn btn-xs btn-ghost"
                    onclick="document.getElementById('pin-endpoint-form').classList.add('hidden')">
                Cancel
            </button>
            <button type="submit" class="btn btn-xs btn-primary">
                <i class="bx bx-pin mr-1"></i>Pin Endpoint
            </button>
        </div>
    </form>
</div>
```

Note: The HTMX form posts JSON but DaisyUI forms post form-encoded by default. The `HandlePinEndpointAsync` handler reads JSON. Add `hx-ext="json-enc"` to the form to encode as JSON, or adjust the handler to read form data. The simplest fix: add `hx-ext="json-enc"` to the form (requires htmx json-enc extension to be loaded). If the extension isn't bundled, read form data in the handler instead. Check whether `htmx-ext-json-enc` is already loaded in Index.cshtml; if not, adjust `HandlePinEndpointAsync` to read form fields:

```csharp
    // Alternative: read as form data if json-enc not available
    var formMethod = context.Request.Form["method"].FirstOrDefault() ?? "ANY";
    var formPath   = context.Request.Form["path"].FirstOrDefault() ?? "";
    var formHoney  = context.Request.Form["isHoneypot"].FirstOrDefault() == "true";
    var formNote   = context.Request.Form["note"].FirstOrDefault();
```

Use the form-data approach if JSON encoding extension is not already in the bundle (checking is part of step 2 above). Update `HandlePinEndpointAsync` to handle `application/x-www-form-urlencoded` in addition to JSON:

```csharp
    private async Task HandlePinEndpointAsync(HttpContext context)
    {
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null) { context.Response.StatusCode = 503; return; }

        string? path, method, note;
        bool isHoneypot;

        var ct = context.Request.ContentType ?? "";
        if (ct.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            PinRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<PinRequest>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { context.Response.StatusCode = 400; return; }
            path = req?.Path; method = req?.Method; note = req?.Note; isHoneypot = req?.IsHoneypot ?? false;
        }
        else
        {
            path = context.Request.Form["path"].FirstOrDefault();
            method = context.Request.Form["method"].FirstOrDefault() ?? "ANY";
            note = context.Request.Form["note"].FirstOrDefault();
            isHoneypot = context.Request.Form["isHoneypot"].FirstOrDefault() == "true";
        }

        if (string.IsNullOrWhiteSpace(path) || !path!.StartsWith('/'))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path is required and must start with /\"}");
            return;
        }

        if (path.Length > 500)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path too long\"}");
            return;
        }

        var methodNorm = string.IsNullOrWhiteSpace(method) ? "ANY" : method.ToUpperInvariant();
        var pin = await pinStore.AddAsync(methodNorm, path, isHoneypot, string.IsNullOrWhiteSpace(note) ? null : note);
        context.Response.StatusCode = 201;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, pin, CamelCaseJson);
    }
```

Replace the simpler version written in Task 5 with this one that handles both content types.

- [ ] **Step 3: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml \
        src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(ui): add Pin Endpoint inline form to endpoints tab header"
```

---

### Task 9: Run all tests and verify clean build

- [ ] **Step 1: Run full test suite**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --logger "console;verbosity=normal"
```

Expected: All tests pass including new `SqlitePinnedEndpointStoreTests` (6 tests).

- [ ] **Step 2: Build UI project**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Final commit if any loose files**

Check `git status` for any unstaged changes and commit if needed.

---

## Notes for Implementer

- **`IReactionPackContext.GetActiveStates()` tuple fields**: The interface was extended in the reaction-packs feature. The exact tuple field names are `PackName`, `Level`, `PolicyName`, `Scope`. Verify against the actual interface file at `src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs` before using them in `BuildEndpointDetailCoverage`.

- **`ReactionPackDefinition` has no `Scope` field**: Check whether `ReactionPackDefinition` has a `Scope` property (it appeared in the pack YAML as `scope:`). If `Scope` is on the model, use `def.Scope`. If not, skip the inactive-pack coverage and only show active packs.

- **HTMX form post and refresh**: After a successful pin, the HTMX form targets `#endpoint-list-container`. Ensure the endpoints partial's containing div in `Index.cshtml` has `id="endpoint-list-container"` — check for that ID and add it if missing.

- **`ServeEndpointDetailPartialAsync` uses `context.RequestServices`**: The existing method already resolves services from DI per-request. The new `pinStore` and `policyRegistry` resolve the same way. No constructor injection changes needed in the middleware.

- **`PinRequest` record**: Defined as a private record inside the middleware class. Ensure it does not conflict with any existing type.
