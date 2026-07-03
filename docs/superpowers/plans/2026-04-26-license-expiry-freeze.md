# License Expiry Freeze Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a commercial license expires, freeze all learning services and (after a 30-day grace period) drop to log-only mode - detection accuracy degrades naturally over time, restoring on renewal.

**Architecture:** A new `ILicenseState` singleton is the single source of truth for freeze/log-only state. It is computed from the JWT `exp` claim and a locally-persisted `grace_started_at` timestamp. Four learning services check `LearningFrozen` before any write operation. `BotDetectionMiddleware` checks `LogOnly` before dispatching action policies. A background service refreshes state every 60 seconds from `BotDetectionOptions.Licensing.Token` so renewal takes effect without restart. FOSS (no token configured) registers a no-op `FossLicenseState` that is always active.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` (already a dependency), `System.Text.Json` for JWT payload decode, `System.IdentityModel.Tokens.Jwt` is NOT used (simple base64url decode is sufficient - the token is server-admin-provided, not user-forged).

---

## Scope note

The portal-side changes (`grace_eligible` claim in JWT, `grace_consumed_at` column in `License` table) are in the `stylobot-commercial` repo and are NOT covered by this plan. This plan implements the product-side enforcement only. Until the portal emits `grace_eligible`, the product treats a missing claim as `true` (existing customers get grace by default).

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `Mostlylucid.BotDetection/Licensing/ILicenseState.cs` | **Create** | Public interface - IsActive, IsInGrace, LearningFrozen, LogOnly |
| `Mostlylucid.BotDetection/Licensing/LicenseTokenParser.cs` | **Create** | Base64url-decode JWT payload, extract exp + grace_eligible |
| `Mostlylucid.BotDetection/Licensing/SqliteLicenseGraceStore.cs` | **Create** | `license_state` table: read/write/clear grace_started_at |
| `Mostlylucid.BotDetection/Licensing/LicenseState.cs` | **Create** | Thread-safe snapshot holder + Compute() logic |
| `Mostlylucid.BotDetection/Licensing/LicenseStateRefreshService.cs` | **Create** | BackgroundService: refresh every 60s, write grace start, log transitions |
| `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` | Modify | Register ILicenseState (Foss or real), LicenseStateRefreshService |
| `Mostlylucid.BotDetection/Services/ReputationMaintenanceService.cs` | Modify | Inject ILicenseState, guard decay + GC + HandleAsync |
| `Mostlylucid.BotDetection/Services/LearningBackgroundService.cs` | Modify | Inject ILicenseState, guard ProcessEventAsync |
| `Mostlylucid.BotDetection/Services/BotClusterService.cs` | Modify | Inject ILicenseState, guard ExecuteAsync clustering run |
| `Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs` | Modify | Inject ILicenseState, guard OnClustersUpdated |
| `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | Modify | Inject ILicenseState, override to logonly before action dispatch |
| `Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs` | **Create** | Unit tests for state machine transitions |
| `Mostlylucid.BotDetection/docs/licensing.md` | **Create** | Trial, grace, freeze, renewal docs |

---

### Task 1: `ILicenseState` interface + `FossLicenseState`

**Files:**
- Create: `Mostlylucid.BotDetection/Licensing/ILicenseState.cs`
- Test: `Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs` (first test only)

- [ ] **Step 1: Write the first failing test**

Create `Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs`:

```csharp
using Mostlylucid.BotDetection.Licensing;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Licensing;

public class LicenseStateTests
{
    [Fact]
    public void FossLicenseState_AlwaysActive_NeverFrozen()
    {
        ILicenseState state = new FossLicenseState();

        Assert.True(state.IsActive);
        Assert.False(state.IsInGrace);
        Assert.False(state.LearningFrozen);
        Assert.False(state.LogOnly);
        Assert.Null(state.ExpiresAt);
        Assert.Null(state.GraceEndsAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Mostlylucid.BotDetection.Test/ \
  --filter "FullyQualifiedName~LicenseStateTests" 2>&1 | tail -10
```

Expected: compilation error - `ILicenseState` not found.

- [ ] **Step 3: Create `ILicenseState.cs`**

```csharp
namespace Mostlylucid.BotDetection.Licensing;

public interface ILicenseState
{
    /// <summary>License is valid and not expired.</summary>
    bool IsActive { get; }

    /// <summary>License expired but within the 30-day grace window (once per account).</summary>
    bool IsInGrace { get; }

    /// <summary>True when !IsActive. Learning services skip all write operations.</summary>
    bool LearningFrozen { get; }

    /// <summary>True when expired and past grace. All action policies are forced to log-only.</summary>
    bool LogOnly { get; }

    DateTimeOffset? ExpiresAt { get; }
    DateTimeOffset? GraceEndsAt { get; }
}

/// <summary>FOSS implementation: no license needed, always active.</summary>
internal sealed class FossLicenseState : ILicenseState
{
    public bool IsActive => true;
    public bool IsInGrace => false;
    public bool LearningFrozen => false;
    public bool LogOnly => false;
    public DateTimeOffset? ExpiresAt => null;
    public DateTimeOffset? GraceEndsAt => null;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test Mostlylucid.BotDetection.Test/ \
  --filter "FullyQualifiedName~LicenseStateTests" -v normal 2>&1 | tail -10
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Licensing/ILicenseState.cs \
        Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs
git commit -m "feat(licensing): ILicenseState interface + FossLicenseState always-active impl"
```

---

### Task 2: `LicenseTokenParser` + `LicenseStateSnapshot` with state machine tests

**Files:**
- Create: `Mostlylucid.BotDetection/Licensing/LicenseTokenParser.cs`
- Modify: `Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs`

- [ ] **Step 1: Add state machine tests**

Append to `LicenseStateTests.cs`:

```csharp
[Fact]
public void Snapshot_ActiveLicense_NotFrozen()
{
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddDays(30),
        graceEligible: true,
        graceStartedAt: null);

    Assert.True(snapshot.IsActive);
    Assert.False(snapshot.IsInGrace);
    Assert.False(snapshot.LearningFrozen);
    Assert.False(snapshot.LogOnly);
}

[Fact]
public void Snapshot_ExpiredGraceEligible_InGrace_LearningFrozen_NotLogOnly()
{
    var graceStartedAt = DateTimeOffset.UtcNow.AddDays(-5);
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
        graceEligible: true,
        graceStartedAt: graceStartedAt);

    Assert.False(snapshot.IsActive);
    Assert.True(snapshot.IsInGrace);
    Assert.True(snapshot.LearningFrozen);
    Assert.False(snapshot.LogOnly);
    Assert.NotNull(snapshot.GraceEndsAt);
}

[Fact]
public void Snapshot_ExpiredGraceConsumed_LogOnly()
{
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddDays(-40),
        graceEligible: true,
        graceStartedAt: DateTimeOffset.UtcNow.AddDays(-31)); // grace started 31 days ago

    Assert.False(snapshot.IsActive);
    Assert.False(snapshot.IsInGrace);
    Assert.True(snapshot.LearningFrozen);
    Assert.True(snapshot.LogOnly);
}

[Fact]
public void Snapshot_ExpiredNotGraceEligible_ImmediatelyLogOnly()
{
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
        graceEligible: false,
        graceStartedAt: null);

    Assert.False(snapshot.IsActive);
    Assert.False(snapshot.IsInGrace);
    Assert.True(snapshot.LearningFrozen);
    Assert.True(snapshot.LogOnly);
}

[Fact]
public void TokenParser_NullToken_ReturnsNull()
{
    Assert.Null(LicenseTokenParser.TryParse(null));
    Assert.Null(LicenseTokenParser.TryParse(""));
    Assert.Null(LicenseTokenParser.TryParse("not.a.jwt"));
}

[Fact]
public void TokenParser_ValidPayload_ExtractsExpAndGraceEligible()
{
    // Build a minimal JWT payload: {"exp":9999999999,"grace_eligible":false}
    var payload = System.Text.Json.JsonSerializer.Serialize(new
    {
        exp = 9999999999L,
        grace_eligible = false
    });
    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var fakeJwt = $"header.{encoded}.signature";

    var claims = LicenseTokenParser.TryParse(fakeJwt);

    Assert.NotNull(claims);
    Assert.False(claims!.GraceEligible);
    Assert.NotNull(claims.ExpiresAt);
}

[Fact]
public void TokenParser_MissingGraceEligible_DefaultsToTrue()
{
    var payload = System.Text.Json.JsonSerializer.Serialize(new { exp = 9999999999L });
    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var fakeJwt = $"header.{encoded}.signature";

    var claims = LicenseTokenParser.TryParse(fakeJwt);

    Assert.NotNull(claims);
    Assert.True(claims!.GraceEligible); // default true when claim absent
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ \
  --filter "FullyQualifiedName~LicenseStateTests" 2>&1 | tail -10
```

Expected: compilation errors - `LicenseStateSnapshot`, `LicenseTokenParser` not found.

- [ ] **Step 3: Create `LicenseTokenParser.cs`**

```csharp
using System.Text;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Licensing;

internal sealed record LicenseTokenClaims(DateTimeOffset? ExpiresAt, bool GraceEligible);

internal static class LicenseTokenParser
{
    /// <summary>
    ///     Decodes the JWT payload section (base64url) and extracts exp + grace_eligible.
    ///     Does NOT verify the Ed25519 signature - the token is admin-provided via config.
    ///     Returns null if the token is missing, malformed, or not a 3-part JWT.
    /// </summary>
    public static LicenseTokenClaims? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            // Restore base64url padding
            var padded = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expUnix))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);

            var graceEligible = true; // default: eligible if claim absent (existing tokens)
            if (root.TryGetProperty("grace_eligible", out var graceEl))
                graceEligible = graceEl.GetBoolean();

            return new LicenseTokenClaims(expiresAt, graceEligible);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Immutable snapshot of computed license state.</summary>
internal sealed record LicenseStateSnapshot(
    bool IsActive,
    bool IsInGrace,
    bool LearningFrozen,
    bool LogOnly,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceEndsAt)
{
    public static readonly LicenseStateSnapshot Foss =
        new(true, false, false, false, null, null);

    public static LicenseStateSnapshot Compute(
        DateTimeOffset? expiresAt,
        bool graceEligible,
        DateTimeOffset? graceStartedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var isActive = expiresAt == null || now < expiresAt;

        if (isActive)
            return new(true, false, false, false, expiresAt, null);

        // Expired. Learning is frozen from this point.
        if (!graceEligible || graceStartedAt == null)
            return new(false, false, true, true, expiresAt, null); // immediate log-only

        var graceEndsAt = graceStartedAt.Value.AddDays(30);
        if (now < graceEndsAt)
            return new(false, true, true, false, expiresAt, graceEndsAt); // in grace

        return new(false, false, true, true, expiresAt, graceEndsAt); // post-grace
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ \
  --filter "FullyQualifiedName~LicenseStateTests" -v normal 2>&1 | tail -15
```

Expected: 8 passed (1 from Task 1 + 7 new).

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Licensing/LicenseTokenParser.cs \
        Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs
git commit -m "feat(licensing): LicenseTokenParser + LicenseStateSnapshot state machine with 8 tests"
```

---

### Task 3: `SqliteLicenseGraceStore` - grace state persistence

**Files:**
- Create: `Mostlylucid.BotDetection/Licensing/SqliteLicenseGraceStore.cs`

- [ ] **Step 1: Create the store**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Persists grace period start time to SQLite so it survives restarts.
///     Table: license_state (id=1, grace_started_at INTEGER ms, updated_at INTEGER ms)
/// </summary>
internal sealed class SqliteLicenseGraceStore
{
    private readonly string _connectionString;

    public SqliteLicenseGraceStore(IOptions<BotDetectionOptions> options)
    {
        var dbPath = options.Value.DatabasePath
                     ?? System.IO.Path.Combine(AppContext.BaseDirectory, "botdetection.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS license_state (
                id               INTEGER PRIMARY KEY DEFAULT 1,
                grace_started_at INTEGER,
                updated_at       INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO license_state (id, updated_at) VALUES (1, 0);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetGraceStartedAtAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT grace_started_at FROM license_state WHERE id = 1;";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long ms and > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    public async Task SetGraceStartedAtAsync(DateTimeOffset value, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO license_state (id, grace_started_at, updated_at)
            VALUES (1, $grace, $now)
            ON CONFLICT(id) DO UPDATE SET grace_started_at = $grace, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$grace", value.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearGraceStartedAtAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE license_state SET grace_started_at = NULL, updated_at = $now WHERE id = 1;
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection/Licensing/SqliteLicenseGraceStore.cs
git commit -m "feat(licensing): SqliteLicenseGraceStore persists grace_started_at to botdetection.db"
```

---

### Task 4: `LicenseState` (mutable snapshot holder) + `LicenseStateRefreshService`

**Files:**
- Create: `Mostlylucid.BotDetection/Licensing/LicenseState.cs`
- Create: `Mostlylucid.BotDetection/Licensing/LicenseStateRefreshService.cs`

- [ ] **Step 1: Create `LicenseState.cs`**

```csharp
namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Thread-safe mutable holder for the current license state snapshot.
///     Updated by LicenseStateRefreshService every 60 seconds.
///     Registered as ILicenseState singleton when Licensing.Token is configured.
/// </summary>
internal sealed class LicenseState : ILicenseState
{
    private volatile LicenseStateSnapshot _snapshot = LicenseStateSnapshot.Foss;

    public bool IsActive => _snapshot.IsActive;
    public bool IsInGrace => _snapshot.IsInGrace;
    public bool LearningFrozen => _snapshot.LearningFrozen;
    public bool LogOnly => _snapshot.LogOnly;
    public DateTimeOffset? ExpiresAt => _snapshot.ExpiresAt;
    public DateTimeOffset? GraceEndsAt => _snapshot.GraceEndsAt;

    internal void Update(LicenseStateSnapshot snapshot) => _snapshot = snapshot;
}
```

- [ ] **Step 2: Create `LicenseStateRefreshService.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Refreshes ILicenseState every 60 seconds from BotDetectionOptions.Licensing.Token.
///     Handles grace period start (first expiry transition) and grace period clear (renewal).
///     Also initializes the SQLite license_state table on startup.
/// </summary>
internal sealed class LicenseStateRefreshService : BackgroundService
{
    private readonly LicenseState _licenseState;
    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly SqliteLicenseGraceStore _graceStore;
    private readonly ILogger<LicenseStateRefreshService> _logger;

    public LicenseStateRefreshService(
        LicenseState licenseState,
        IOptionsMonitor<BotDetectionOptions> options,
        SqliteLicenseGraceStore graceStore,
        ILogger<LicenseStateRefreshService> logger)
    {
        _licenseState = licenseState;
        _options = options;
        _graceStore = graceStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _graceStore.InitializeAsync(stoppingToken);
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var token = _options.CurrentValue.Licensing?.Token;
            var claims = LicenseTokenParser.TryParse(token);

            if (claims == null)
            {
                // Token removed or unparseable - treat as FOSS (active)
                _licenseState.Update(LicenseStateSnapshot.Foss);
                return;
            }

            var graceStartedAt = await _graceStore.GetGraceStartedAtAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var isActive = claims.ExpiresAt == null || now < claims.ExpiresAt;

            // First transition to expired + grace eligible: record grace start
            if (!isActive && claims.GraceEligible && graceStartedAt == null)
            {
                graceStartedAt = now;
                await _graceStore.SetGraceStartedAtAsync(now, ct);
                _logger.LogWarning(
                    "StyloBot license expired. 30-day grace period started - detection active, learning paused. " +
                    "Renew at https://stylo.bot");
            }

            // Renewal: license is active again, clear grace timer
            if (isActive && graceStartedAt != null)
            {
                await _graceStore.ClearGraceStartedAtAsync(ct);
                graceStartedAt = null;
                _logger.LogInformation("StyloBot license renewed. Learning resumed.");
            }

            var snapshot = LicenseStateSnapshot.Compute(claims.ExpiresAt, claims.GraceEligible, graceStartedAt);
            _licenseState.Update(snapshot);

            if (snapshot.LogOnly)
                _logger.LogWarning(
                    "StyloBot license grace period expired. Running in log-only mode. " +
                    "Renew at https://stylo.bot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing license state");
        }
    }
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Licensing/LicenseState.cs \
        Mostlylucid.BotDetection/Licensing/LicenseStateRefreshService.cs
git commit -m "feat(licensing): LicenseState snapshot holder + LicenseStateRefreshService 60s background refresh"
```

---

### Task 5: DI Registration

**Files:**
- Modify: `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Read the file**

Read `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`. Find the `AddBotDetection` (or `RegisterCoreServices`) method and the `AddDomainEntitlement` method location. Note roughly where to insert.

- [ ] **Step 2: Add the registration**

Find the call to `services.AddDomainEntitlement()` or where `DomainEntitlementValidator` / `LicensingOptions` is first used. Add the following immediately after it:

```csharp
// License state - FossLicenseState when no token configured, real LicenseState when token present
var licensingOpts = services.BuildServiceProvider()
    .GetRequiredService<IOptions<BotDetectionOptions>>().Value.Licensing;
if (!string.IsNullOrWhiteSpace(licensingOpts?.Token))
{
    services.AddSingleton<LicenseState>();
    services.AddSingleton<ILicenseState>(sp => sp.GetRequiredService<LicenseState>());
    services.AddSingleton<SqliteLicenseGraceStore>();
    services.AddHostedService<LicenseStateRefreshService>();
}
else
{
    services.AddSingleton<ILicenseState, FossLicenseState>();
}
```

**Important:** `BuildServiceProvider()` is acceptable here only if options are already registered by the time this runs. If this pattern causes a warning or is not idiomatic in the existing code, use `IOptions<BotDetectionOptions>` resolution via the factory pattern instead:

```csharp
services.AddSingleton<ILicenseState>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opts.Licensing?.Token))
    {
        // LicenseState itself is registered above; get it
        return sp.GetRequiredService<LicenseState>();
    }
    return new FossLicenseState();
});
```

Use whichever pattern is consistent with existing registrations in the file. The goal: `ILicenseState` resolves to `FossLicenseState` when no token, `LicenseState` otherwise.

- [ ] **Step 3: Build the full solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(licensing): register ILicenseState (FossLicenseState or LicenseState) based on token presence"
```

---

### Task 6: Freeze Guards in Learning Services

**Files:**
- Modify: `Mostlylucid.BotDetection/Services/ReputationMaintenanceService.cs`
- Modify: `Mostlylucid.BotDetection/Services/LearningBackgroundService.cs`
- Modify: `Mostlylucid.BotDetection/Services/BotClusterService.cs`
- Modify: `Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs`

Add the same guard pattern to all four. Read each file before editing to confirm field names.

- [ ] **Step 1: Update `ReputationMaintenanceService.cs`**

1. Add constructor parameter `ILicenseState licenseState` and store as `private readonly ILicenseState _licenseState;`
2. In `ExecuteAsync`, wrap the decay sweep and GC operations:

```csharp
// Decay sweep
if (now - lastDecay >= decayInterval)
{
    if (!_licenseState.LearningFrozen)
        await _cache.DecaySweepAsync(stoppingToken);
    lastDecay = now;
}

// Garbage collection
if (now - lastGc >= gcInterval)
{
    if (!_licenseState.LearningFrozen)
    {
        await _cache.GarbageCollectAsync(stoppingToken);
        var stats = _cache.GetStats();
        _logger.LogInformation(
            "Reputation stats: {Total} patterns, {Bad} bad, {Suspect} suspect, {GcEligible} GC-eligible",
            stats.TotalPatterns, stats.ConfirmedBadCount, stats.SuspectCount, stats.GcEligibleCount);
    }
    lastGc = now;
}
// Persistence: always runs (just flushing cache to SQLite, not learning)
if (now - lastPersist >= persistInterval)
{
    await _cache.PersistAsync(stoppingToken);
    lastPersist = now;
}
```

3. In `HandleAsync` (or the top of `HandleDetectionEvent`/`HandleSignatureFeedback`/`HandleUserFeedback`), add at the top of the public `HandleAsync` method:

```csharp
public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
{
    if (_licenseState.LearningFrozen) return Task.CompletedTask;
    // ... existing body
```

- [ ] **Step 2: Update `LearningBackgroundService.cs`**

1. Add `ILicenseState licenseState` to the constructor, store as `_licenseState`.
2. In `ProcessEventAsync`, add at the top:

```csharp
private async Task ProcessEventAsync(LearningEvent evt, CancellationToken ct)
{
    if (_licenseState.LearningFrozen)
    {
        _logger.LogDebug("Learning frozen, skipping event: {Type}", evt.Type);
        return;
    }
    // ... existing body
```

- [ ] **Step 3: Update `BotClusterService.cs`**

Read the file to find `ExecuteAsync` and the main clustering run method (likely `RunClusteringAsync` called from the loop). Add `ILicenseState licenseState` to the constructor and store as `_licenseState`. Then in the loop body that calls the clustering run:

```csharp
// Before calling the clustering run:
if (_licenseState.LearningFrozen)
{
    _logger.LogDebug("Learning frozen, skipping cluster run.");
    // Still wait for next tick, just don't cluster
    continue; // or await delay, matching existing pattern
}
```

- [ ] **Step 4: Update `CentroidSequenceRebuildHostedService.cs`**

Add `ILicenseState licenseState` to the constructor and store as `_licenseState`. In `OnClustersUpdated`:

```csharp
private void OnClustersUpdated(
    IReadOnlyList<BotCluster> clusters,
    IReadOnlyList<SignatureBehavior> behaviors)
{
    if (_licenseState.LearningFrozen)
    {
        _logger.LogDebug("Learning frozen, skipping centroid sequence rebuild.");
        return;
    }
    _ = Task.Run(async () =>
    {
        try
        {
            await _centroidStore.RebuildAsync(clusters, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CentroidSequenceStore rebuild failed after cluster update");
        }
    });
}
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection/Services/ReputationMaintenanceService.cs \
        Mostlylucid.BotDetection/Services/LearningBackgroundService.cs \
        Mostlylucid.BotDetection/Services/BotClusterService.cs \
        Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs
git commit -m "feat(licensing): freeze guards in ReputationMaintenance, LearningBackground, BotCluster, CentroidRebuild"
```

---

### Task 7: Log-Only Override in `BotDetectionMiddleware`

**Files:**
- Modify: `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`

- [ ] **Step 1: Read the middleware constructor**

Read lines 1-80 of `BotDetectionMiddleware.cs` to find the constructor and field declarations. Note how other services (`IOptions`, `IActionPolicyRegistry`, etc.) are injected.

- [ ] **Step 2: Add `ILicenseState` to the constructor**

Add `ILicenseState licenseState` to the constructor parameters and store as `private readonly ILicenseState _licenseState;`.

- [ ] **Step 3: Add the log-only override before action dispatch**

The action dispatch begins at roughly line 346 (check current line numbers after prior edits):

```csharp
// Check for triggered action policy first (takes precedence over built-in actions)
if (!string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
```

Immediately before this block, add:

```csharp
// License log-only override: when license is expired past grace period,
// force log-only regardless of any configured action policy.
if (_licenseState.LogOnly)
{
    var logOnlyPolicy = actionPolicyRegistry.GetPolicy("logonly");
    if (logOnlyPolicy != null)
    {
        await logOnlyPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
        await InvokeNextWithResponseMutationAsync(context);
        return;
    }
    // No logonly policy registered - just pass through
    await InvokeNextWithResponseMutationAsync(context);
    return;
}
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs
git commit -m "feat(licensing): force log-only action policy when license grace period expired"
```

---

### Task 8: Tests

**Files:**
- Modify: `Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs`

- [ ] **Step 1: Add refresh service + grace transition tests**

Append to `LicenseStateTests.cs`:

```csharp
[Fact]
public void Snapshot_JustExpired_GraceNotStarted_StillLogOnly()
{
    // If expired but grace eligible and no grace_started_at yet,
    // Compute() returns log-only (grace hasn't been started - refresh service sets it)
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
        graceEligible: true,
        graceStartedAt: null); // grace not yet started

    // Log-only until refresh service writes grace_started_at
    Assert.True(snapshot.LogOnly);
}

[Fact]
public void Snapshot_GraceStartedToday_IsInGrace_NotLogOnly()
{
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
        graceEligible: true,
        graceStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

    Assert.True(snapshot.IsInGrace);
    Assert.False(snapshot.LogOnly);
    Assert.True(snapshot.LearningFrozen);
}

[Fact]
public void Snapshot_GraceEndsAt_Is30DaysAfterGraceStart()
{
    var graceStart = DateTimeOffset.UtcNow.AddDays(-10);
    var snapshot = LicenseStateSnapshot.Compute(
        expiresAt: DateTimeOffset.UtcNow.AddDays(-11),
        graceEligible: true,
        graceStartedAt: graceStart);

    Assert.NotNull(snapshot.GraceEndsAt);
    Assert.Equal(graceStart.AddDays(30), snapshot.GraceEndsAt!.Value, TimeSpan.FromSeconds(1));
}
```

- [ ] **Step 2: Run all license tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ \
  --filter "FullyQualifiedName~LicenseStateTests" -v normal 2>&1 | tail -15
```

Expected: 11 passed.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test Mostlylucid.BotDetection.Test/ -v minimal 2>&1 | tail -10
```

Expected: no new failures vs baseline.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Test/Licensing/LicenseStateTests.cs
git commit -m "test(licensing): LicenseStateSnapshot state machine edge cases - 11 tests total"
```

---

### Task 9: Documentation

**Files:**
- Create: `Mostlylucid.BotDetection/docs/licensing.md`
- Modify: `Mostlylucid.BotDetection/docs/configuration.md`

- [ ] **Step 1: Create `licensing.md`**

```markdown
# Licensing

## FOSS

No license required. Detection, learning, blocking, and all features work indefinitely.
Configure with `AddBotDetection()` and never set `BotDetection:Licensing:Token`.

## Commercial

A signed license JWT is required. Set it in configuration:

```json
{
  "BotDetection": {
    "Licensing": {
      "Token": "<your-license-jwt>",
      "Domains": ["yourdomain.com"]
    }
  }
}
```

Start a free 30-day trial (one per organization) at https://stylo.bot.

## What happens when a license expires

**30-day grace period (once per account):**
Detection and blocking continue exactly as normal. Learning services pause:
reputation patterns stop updating, cluster detection stops retraining, centroid
sequences freeze. Accuracy degrades naturally as traffic patterns drift.

The dashboard shows the grace period end date. Renew any time to immediately
resume learning.

**After grace period:**
All action policies are forced to log-only mode. Detection still runs and the
dashboard still shows results, but no traffic is blocked or throttled. Learning
remains paused.

**Renewal:**
Drop a new JWT into your configuration file. The system picks it up within
60 seconds and resumes learning automatically - no restart required.

## Second expiry

The grace period is a one-time offer per account. If a license lapses, the
grace period is consumed, you renew, and it lapses again - the second expiry
goes directly to log-only mode with no grace window.
```

- [ ] **Step 2: Update `configuration.md`**

Find the `Licensing` section in `configuration.md` (or add one). Add the two options:

```markdown
### Licensing

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `BotDetection:Licensing:Token` | string | `null` | Signed license JWT. When absent, FOSS mode (no expiry, no limits). |
| `BotDetection:Licensing:Domains` | string[] | `[]` | Licensed eTLD+1 domains for domain entitlement validation. |
```

- [ ] **Step 3: Build solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | tail -5
```

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/docs/licensing.md \
        Mostlylucid.BotDetection/docs/configuration.md
git commit -m "docs: licensing.md - trial, grace period, renewal, expiry behavior"
```

---

## Self-Review

**Spec coverage check:**

| Spec requirement | Task |
|---|---|
| FOSS: no license, runs forever | Task 1 (FossLicenseState) |
| Commercial requires token to start | Task 5 (DI: fail if token missing but... note: current impl treats missing = FOSS not error) |
| Grace period 30 days | Task 2 (LicenseStateSnapshot.Compute + AddDays(30)) |
| Once per account (grace_eligible claim) | Task 2 (LicenseTokenParser extracts grace_eligible) |
| Learning freezes on expiry | Task 6 (4 services) |
| Log-only after grace | Task 7 (BotDetectionMiddleware) |
| Unfreezes on renewal | Task 4 (LicenseStateRefreshService clears grace on active token) |
| Grace persists across restarts | Task 3 (SqliteLicenseGraceStore) |
| Docs | Task 9 |

**Note on "commercial requires license to start":** The current DI registration in Task 5 treats missing token as FOSS mode, not a startup failure. The hard "won't start without license" gate for commercial packages is enforced in the `stylobot-commercial` repo at their startup (outside this plan's scope). This is correct.

**Placeholder scan:** None found.

**Type consistency:** `LicenseStateSnapshot` defined in Task 2, used in Tasks 4 and 8. `LicenseState.Update(LicenseStateSnapshot)` defined in Task 4, called in Task 4. Consistent throughout.
