# Fingerprint Verdict Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the per-signature verdict the product has been computing but never reading into the live request path, so a known-fingerprint request can be answered from cache (HighPerformance mode) or biased toward its accumulated prior (Standard mode). Fix the MAX-not-EWMA aggregation bug while we are in there. Make per-request volatility visible as a contribution delta, not as a hysterical absolute score.

**Architecture:**
- `SignatureVerdictCache` is an in-process read-through over the existing `signatures` table. It exposes a hot-path lookup `TryGetVerdict(signatureId) -> (BotProbability, Confidence, RiskBand, ThreatScore, LastSeenUtc, SampleCount)`.
- `SignatureVerdictGate` runs in `BotDetectionMiddleware` BEFORE the orchestrator. Four outcomes per policy:
  - **Skip**: cache hit, **confidence** above `SkipMinConfidence` (i.e., sure-bot OR sure-human; confidence is direction-agnostic) AND freshness OK. Enforce the cached verdict, bypass the heavy detector pipeline, but **always run the `VarianceWatchdog`** and **always record the request in the per-fingerprint sliding window**.
  - **Watchdog-trip**: a Skip that the watchdog vetoed: a cheap signal said the cached verdict no longer fits this request. Downgrades to Miss for this single request, forcing the full pipeline so the new evidence is properly weighed.
  - **Bias**: cache hit but below Skip thresholds. Run the pipeline AND inject the cached verdict as a prior contribution via Wave 0.
  - **Miss**: no usable cache or watchdog tripped. Run the full pipeline.
- `VarianceWatchdog` is a small set of cheap, deterministic checks that fire on every Skip candidate. It does NOT score the request; it answers a single yes/no: "Does the cached verdict still fit?" When it says no, the gate downgrades to Miss. The initial check set:
  - **IP-vs-signature stability**: same signature but the client IP changed to a different /24 within a short window. The signature is supposed to be IP-derived; rotation is suspicious.
  - **Path-vs-centroid match**: the request path's classified `RequestState` is one that the fingerprint's persisted centroid chain does not expect at this position (existing `CentroidSequenceStore` already has the data).
  - **Rate spike**: this fingerprint normally sends N req/minute (rolling) and is now sending 10x that.
  - Each check has a per-policy enable flag and a sensitivity knob; failure of any check trips the watchdog.
- `FingerprintPriorContributor` is a Wave 0 contributor (priority 4, just after FastPathReputation) that reads the Bias prior from signals and emits a calibrated contribution. The orchestrator's existing weighted-sum aggregation does NOT change; the bias arrives as a normal contribution with calibrated weight derived from prior confidence.
- **Sliding window** continues to receive every request (Skip included) via the existing `SignatureCoordinator` write path. This is the same window that powers clustering, learning, and offline drift analysis. Skipping the detectors does NOT skip the observation; we just trust the cached verdict for the policy decision.
- The `signatures` table upsert is fixed from MAX-prob to a proper EWMA so an old false-positive does not pin a fingerprint to high-bot forever.
- The CLI dashboard surfaces the fingerprint's cached score as the headline value with a sparkline of recent observations, and the feed shows each request's **contribution delta** instead of its raw absolute score. Skip rows are marked (small `*` or coloured background) so the operator can see which requests bypassed detection.

**Tech Stack:** .NET 10, xUnit, SQLite, existing detection pipeline. No new packages.

**Out of scope:** Entity-resolution multi-signature merge priors (the `entities` table exists but wiring entity-level reputation in is a separate feature). Drift-detection that re-trains the prior decay (today's EWMA alpha is a constant).

---

## File Structure

**New files:**
- `src/Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs` (read-through cache over `signatures`)
- `src/Mostlylucid.BotDetection/Services/SignatureVerdictGate.cs` (the Skip/Bias/Miss decision)
- `src/Mostlylucid.BotDetection/Services/VarianceWatchdog.cs` (cheap checks that veto a Skip)
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintPriorContributor.cs` (Wave 0 bias)
- `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/fingerprintprior.detector.yaml`
- `src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs` (per-policy thresholds, including watchdog enables)
- `src/Mostlylucid.BotDetection/Policies/VarianceWatchdogOptions.cs` (per-policy watchdog sensitivities)
- `src/Mostlylucid.BotDetection/docs/fingerprint-verdict-cache.md`
- `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictCacheTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictGateTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/VarianceWatchdogTests.cs`
- `src/Mostlylucid.BotDetection.Test/Orchestration/FingerprintPriorContributorTests.cs`

**Modified files:**
- `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs` (replace MAX with EWMA on upsert; add `LastUpdatedUtc` column)
- `src/Mostlylucid.BotDetection/Data/SessionPersistence.cs` (add `LastUpdatedUtc` to `PersistedSignature`)
- `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs` (add `SignatureCache` property)
- `src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs` (bind it from JSON)
- `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` (run the gate before the orchestrator)
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (register cache, gate, contributor)
- `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (add new signal keys: `fingerprint.prior.*`)
- `src/Mostlylucid.BotDetection/Orchestration/DetectionContribution.cs` (add `RequestContributionDelta` to `AggregatedEvidence`)
- `src/Mostlylucid.BotDetection.Console/Services/LiveDetectionTable.cs` (feed shows delta; sidebar shows cache state)
- `CHANGELOG.md`

---

## Task 1: Add `LastUpdatedUtc` column to `signatures` and replace MAX with EWMA

The current `UpsertSignatureAsync` uses `MAX(bot_probability, @prob)`. A signature that scored 0.95 once is pinned forever. Replace with an EWMA so the prior decays toward recent observations.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs` (schema + upsert SQL)
- Modify: `src/Mostlylucid.BotDetection/Data/SessionPersistence.cs` (add `LastUpdatedUtc` to `PersistedSignature`)
- Create: `src/Mostlylucid.BotDetection.Test/Data/SignatureUpsertEwmaTests.cs`

- [ ] **Step 1.1: Add the `last_updated_utc` column to the `signatures` schema and migration**

In `SqliteSessionStore.cs`, find the `CREATE TABLE IF NOT EXISTS signatures` block in `InitializeAsync`. Append:

```csharp
                last_updated_utc TEXT
```

immediately before the closing `);` of the signatures table.

Then add a defensive `ALTER TABLE` migration after the table creation so existing databases pick up the column:

```csharp
            await TryAddColumnAsync(conn, "signatures", "last_updated_utc", "TEXT", ct);
```

If `TryAddColumnAsync` does not exist in this file, search the file for the existing migration pattern (likely uses `MigrateAddColumnAsync` per a prior commit). Use whatever pattern is already there. If nothing matches, inline:

```csharp
            try
            {
                await using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "ALTER TABLE signatures ADD COLUMN last_updated_utc TEXT;";
                await migCmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
```

- [ ] **Step 1.2: Add `LastUpdatedUtc` to `PersistedSignature`**

In `src/Mostlylucid.BotDetection/Data/SessionPersistence.cs`, find `public sealed record PersistedSignature` (or class). Append:

```csharp
    public DateTime? LastUpdatedUtc { get; init; }
```

next to the other timestamp fields.

- [ ] **Step 1.3: Write the failing EWMA test**

Create `src/Mostlylucid.BotDetection.Test/Data/SignatureUpsertEwmaTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class SignatureUpsertEwmaTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sig-ewma-{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;

    private async Task<SqliteSessionStore> GetStore()
    {
        if (_store is not null) return _store;
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        _store = new SqliteSessionStore(NullLogger<SqliteSessionStore>.Instance, options);
        await _store.InitializeAsync();
        return _store;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is IAsyncDisposable d) await d.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); }
            catch { }
    }

    [Fact]
    public async Task UpsertSignature_FirstObservation_StoresLiteralValue()
    {
        var store = await GetStore();
        await store.UpsertSignatureAsync("sig-A", botProbability: 0.8, confidence: 0.6,
            sessionCount: 1, totalRequests: 5, country: null);

        var stored = await store.GetSignatureAsync("sig-A");
        Assert.NotNull(stored);
        Assert.Equal(0.8, stored!.BotProbability, precision: 2);
    }

    [Fact]
    public async Task UpsertSignature_BenignObservation_DecaysHighPrior()
    {
        // EWMA must replace MAX. A 0.95 prior followed by ten 0.05 observations
        // must NOT remain at 0.95.
        var store = await GetStore();
        await store.UpsertSignatureAsync("sig-B", 0.95, 0.6, 1, 10, null);
        for (var i = 0; i < 10; i++)
            await store.UpsertSignatureAsync("sig-B", 0.05, 0.6, 1, 10, null);

        var stored = await store.GetSignatureAsync("sig-B");
        Assert.NotNull(stored);
        Assert.True(stored!.BotProbability < 0.5,
            $"After ten benign observations the EWMA should drop below 0.5, got {stored.BotProbability:F3}");
    }

    [Fact]
    public async Task UpsertSignature_RecordsLastUpdatedUtc()
    {
        var store = await GetStore();
        var before = DateTime.UtcNow.AddSeconds(-1);
        await store.UpsertSignatureAsync("sig-C", 0.5, 0.5, 1, 1, null);
        var after = DateTime.UtcNow.AddSeconds(1);

        var stored = await store.GetSignatureAsync("sig-C");
        Assert.NotNull(stored!.LastUpdatedUtc);
        Assert.InRange(stored.LastUpdatedUtc!.Value, before, after);
    }
}
```

If `UpsertSignatureAsync`'s actual signature has different parameter names or ordering, adapt the test calls. Read the current method first.

- [ ] **Step 1.4: Run the test, expect the EWMA test to fail**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SignatureUpsertEwmaTests"
```
Expected: the `UpsertSignature_BenignObservation_DecaysHighPrior` test fails because the upsert uses `MAX`.

- [ ] **Step 1.5: Replace MAX with EWMA in the upsert SQL**

Find the upsert in `SqliteSessionStore.cs`. The current `ON CONFLICT DO UPDATE` clause includes something like:

```sql
                bot_probability = MAX(bot_probability, excluded.bot_probability),
```

Replace with an EWMA update. The `alpha` here is the weight of the NEW observation (smaller alpha = more memory). Use 0.15 as the default in code; expose as a `SignatureEwmaAlpha` property on `BotDetectionOptions` (default 0.15) so it is tunable.

The replacement SQL clause:

```sql
                bot_probability = COALESCE((1.0 - @alpha) * bot_probability + @alpha * excluded.bot_probability, excluded.bot_probability),
                confidence      = MAX(confidence, excluded.confidence),
                last_updated_utc = excluded.last_updated_utc,
```

Bind `@alpha` from the new option:

```csharp
cmd.Parameters.AddWithValue("@alpha", _options.SignatureEwmaAlpha);
cmd.Parameters.AddWithValue("@last_updated_utc", DateTime.UtcNow.ToString("O"));
```

Insert side: set `last_updated_utc = @last_updated_utc` and `bot_probability = excluded.bot_probability` (literal on first insert).

Adjust the row-mapping reader in `MapSignature` (or wherever `PersistedSignature` is materialised) to read `last_updated_utc`:

```csharp
LastUpdatedUtc = reader.IsDBNull(reader.GetOrdinal("last_updated_utc"))
    ? null
    : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated_utc")),
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind),
```

- [ ] **Step 1.6: Add `SignatureEwmaAlpha` to `BotDetectionOptions`**

In `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs`, add:

```csharp
    /// <summary>
    ///     EWMA weight for the newest observation when updating a signature's persisted
    ///     bot_probability. Smaller values mean stronger memory: 0.10 retains ~90% of
    ///     prior state, 0.30 reacts more quickly to changes. Default 0.15.
    /// </summary>
    public double SignatureEwmaAlpha { get; set; } = 0.15;
```

- [ ] **Step 1.7: Run the tests, expect pass**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SignatureUpsertEwmaTests"
```
Expected: 3 pass.

Then the full project to make sure no other test broke from the schema change:

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName!~Puppeteer"
```
Expected: 0 failures. If any session-store tests broke because they bind the new column at read time, fix the row-mapping inline.

- [ ] **Step 1.8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs \
        src/Mostlylucid.BotDetection/Data/SessionPersistence.cs \
        src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs \
        src/Mostlylucid.BotDetection.Test/Data/SignatureUpsertEwmaTests.cs
git commit -m "$(cat <<'EOF'
fix(persistence): signatures upsert is EWMA, not MAX

A signature that scored bot_probability=0.95 once was pinned at 0.95
forever because the upsert used MAX(prior, observation). Replace with an
exponentially weighted moving average (alpha 0.15 by default, tunable via
BotDetectionOptions.SignatureEwmaAlpha). A high prior now decays toward
benign observations over time, matching how a real entity's risk profile
evolves.

Also adds last_updated_utc column so a downstream verdict cache can apply
recency-based freshness rules.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `SignatureVerdictCache` (read-through over `signatures` table)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictCacheTests.cs`

- [ ] **Step 2.1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictCacheTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignatureVerdictCacheTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"verdictcache-{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;

    private async Task<SqliteSessionStore> GetStore()
    {
        if (_store is not null) return _store;
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        _store = new SqliteSessionStore(NullLogger<SqliteSessionStore>.Instance, options);
        await _store.InitializeAsync();
        return _store;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is IAsyncDisposable d) await d.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); }
            catch { }
    }

    [Fact]
    public async Task TryGetVerdict_NoData_ReturnsNull()
    {
        var store = await GetStore();
        var cache = new SignatureVerdictCache(store, NullLogger<SignatureVerdictCache>.Instance);
        var verdict = await cache.TryGetVerdictAsync("missing-sig");
        Assert.Null(verdict);
    }

    [Fact]
    public async Task TryGetVerdict_PersistedSignature_ReturnsVerdict()
    {
        var store = await GetStore();
        await store.UpsertSignatureAsync("sig-known", 0.85, 0.75, 5, 50, "US");
        var cache = new SignatureVerdictCache(store, NullLogger<SignatureVerdictCache>.Instance);

        var verdict = await cache.TryGetVerdictAsync("sig-known");
        Assert.NotNull(verdict);
        Assert.Equal(0.85, verdict!.BotProbability, precision: 2);
        Assert.Equal(0.75, verdict.Confidence, precision: 2);
        Assert.Equal(50, verdict.TotalRequestCount);
        Assert.NotNull(verdict.LastUpdatedUtc);
    }

    [Fact]
    public async Task TryGetVerdict_CachesAcrossCalls()
    {
        var store = await GetStore();
        await store.UpsertSignatureAsync("sig-cached", 0.6, 0.5, 1, 1, null);
        var cache = new SignatureVerdictCache(store, NullLogger<SignatureVerdictCache>.Instance);

        var v1 = await cache.TryGetVerdictAsync("sig-cached");
        var v2 = await cache.TryGetVerdictAsync("sig-cached");
        Assert.Same(v1, v2); // reference-equal because the same cached entry is returned
    }

    [Fact]
    public async Task Invalidate_DropsCachedEntry()
    {
        var store = await GetStore();
        await store.UpsertSignatureAsync("sig-inv", 0.3, 0.5, 1, 1, null);
        var cache = new SignatureVerdictCache(store, NullLogger<SignatureVerdictCache>.Instance);

        var first = await cache.TryGetVerdictAsync("sig-inv");
        Assert.NotNull(first);
        await store.UpsertSignatureAsync("sig-inv", 0.9, 0.8, 1, 1, null);
        cache.Invalidate("sig-inv");
        var refreshed = await cache.TryGetVerdictAsync("sig-inv");
        Assert.NotSame(first, refreshed);
        Assert.True(refreshed!.BotProbability > 0.4);
    }
}
```

- [ ] **Step 2.2: Run the test, expect compile failure**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SignatureVerdictCacheTests"
```
Expected: build errors (`SignatureVerdictCache` does not exist).

- [ ] **Step 2.3: Implement `SignatureVerdictCache`**

Create `src/Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Immutable snapshot of a fingerprint's persisted verdict. Returned by the cache
///     and consumed by <see cref="SignatureVerdictGate"/> at the middleware entry.
/// </summary>
public sealed record SignatureVerdict
{
    public required string SignatureId { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public RiskBand RiskBand { get; init; }
    public double ThreatScore { get; init; }
    public int TotalRequestCount { get; init; }
    public DateTime? LastUpdatedUtc { get; init; }
}

/// <summary>
///     Read-through cache over <see cref="ISessionStore.GetSignatureAsync"/>. Hot path
///     is a <see cref="ConcurrentDictionary"/> lookup; misses defer to the store and
///     cache the result. Invalidation is explicit (called by the orchestrator after a
///     full pipeline run updates the persisted aggregate).
/// </summary>
public sealed class SignatureVerdictCache
{
    private readonly ISessionStore _store;
    private readonly ILogger<SignatureVerdictCache> _logger;
    private readonly ConcurrentDictionary<string, SignatureVerdict?> _cache = new();

    public SignatureVerdictCache(ISessionStore store, ILogger<SignatureVerdictCache> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<SignatureVerdict?> TryGetVerdictAsync(string signatureId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(signatureId, out var cached)) return cached;

        var persisted = await _store.GetSignatureAsync(signatureId, ct);
        if (persisted is null)
        {
            _cache[signatureId] = null;
            return null;
        }

        var verdict = new SignatureVerdict
        {
            SignatureId = signatureId,
            BotProbability = persisted.BotProbability,
            Confidence = persisted.Confidence,
            RiskBand = persisted.RiskBand,
            ThreatScore = persisted.ThreatScore,
            TotalRequestCount = persisted.TotalRequestCount,
            LastUpdatedUtc = persisted.LastUpdatedUtc,
        };
        _cache[signatureId] = verdict;
        return verdict;
    }

    /// <summary>
    ///     Drop a cached entry. Called by the persistence pipeline after a full
    ///     detection run updates the persisted aggregate so the next lookup re-reads
    ///     fresh state.
    /// </summary>
    public void Invalidate(string signatureId) => _cache.TryRemove(signatureId, out _);

    /// <summary>Drop everything. Used by tests and on configuration reload.</summary>
    public void Clear() => _cache.Clear();
}
```

If `PersistedSignature` does not have `RiskBand` or `ThreatScore` properties yet, fall back to a sane default (`RiskBand.Unknown`, `0.0`). Read the actual record first.

If `ISessionStore.GetSignatureAsync` is not the actual method name, find the equivalent: it returns the persisted signature by primary key. Use whatever the store exposes.

- [ ] **Step 2.4: Run the tests, expect pass**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SignatureVerdictCacheTests"
```
Expected: 4 pass.

- [ ] **Step 2.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs \
        src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictCacheTests.cs
git commit -m "$(cat <<'EOF'
feat(cache): SignatureVerdictCache read-through over signatures table

In-process ConcurrentDictionary cache that serves persisted signature
aggregates to the hot request path. Invalidate() drops a single entry so
the next lookup re-reads the store after the orchestrator writes a fresh
EWMA. Wiring into the middleware and the orchestrator happens in later
tasks.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `SignatureCacheOptions` and `DetectionPolicy.SignatureCache`

The Skip/Bias/Miss decision is parameterised per policy. A high-traffic policy might say "skip the pipeline when confidence >= 0.7 and verdict is < 5 minutes old"; an admin-endpoint policy might always run the pipeline.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs`
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs` (add property)
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs` (DTO + mapping)

- [ ] **Step 3.1: Create `SignatureCacheOptions`**

Create `src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy thresholds for the SignatureVerdictGate. Three behaviours:
///     <list type="bullet">
///         <item>
///             <b>Skip</b>: cache hit meets <see cref="SkipMinConfidence"/> AND is younger
///             than <see cref="SkipMaxAgeSeconds"/>: bypass the detector pipeline and
///             enforce the cached verdict directly.
///         </item>
///         <item>
///             <b>Bias</b>: cache hit meets <see cref="BiasMinConfidence"/> but does not
///             meet Skip: run the pipeline AND inject the cached verdict as a prior
///             contribution. Posterior = blend(prior, request observation).
///         </item>
///         <item>
///             <b>Miss</b>: no usable cache entry: run the full pipeline with no prior.
///         </item>
///     </list>
/// </summary>
public sealed record SignatureCacheOptions
{
    /// <summary>Minimum confidence required to skip the pipeline entirely. Default 0.85.</summary>
    public double SkipMinConfidence { get; init; } = 0.85;

    /// <summary>Maximum age in seconds for a Skip-eligible verdict. Default 300 (5 minutes).</summary>
    public int SkipMaxAgeSeconds { get; init; } = 300;

    /// <summary>Minimum confidence required to inject a prior bias. Default 0.30.</summary>
    public double BiasMinConfidence { get; init; } = 0.30;

    /// <summary>Maximum age in seconds for a Bias-eligible verdict. Default 86400 (24h).</summary>
    public int BiasMaxAgeSeconds { get; init; } = 86_400;

    /// <summary>
    ///     Fraction of Skip-eligible requests that nevertheless run the pipeline so the
    ///     verdict cache stays honest. Default 0.05 (5 percent). Set to 0 to disable
    ///     refresh sampling.
    /// </summary>
    public double SkipSamplingRate { get; init; } = 0.05;

    /// <summary>
    ///     Whether the gate is enabled at all on this policy. Default true. Set to
    ///     false to disable cache-aware behaviour and always run the pipeline.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
```

- [ ] **Step 3.2: Add the property to `DetectionPolicy`**

In `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`, after the `LoadShed` property (added in 6.4.1):

```csharp
    /// <summary>
    ///     Per-policy signature verdict cache thresholds. Controls whether the
    ///     SignatureVerdictGate skips the pipeline on a confident cache hit, biases
    ///     the pipeline with a prior on a less-confident hit, or runs the full
    ///     pipeline. Defaults are tuned for general-purpose sites; high-security
    ///     endpoints should set <see cref="SignatureCacheOptions.Enabled"/> = false.
    /// </summary>
    public SignatureCacheOptions SignatureCache { get; init; } = new();
```

- [ ] **Step 3.3: Bind it from JSON**

In `DetectionPolicyConfiguration.cs`, find the `DetectionPolicyConfig` DTO and the `ToPolicy(name)` mapping. Add:

```csharp
// in DetectionPolicyConfig:
public SignatureCacheDef? SignatureCache { get; set; }

// new nested DTO:
public sealed class SignatureCacheDef
{
    public bool Enabled { get; set; } = true;
    public double SkipMinConfidence { get; set; } = 0.85;
    public int SkipMaxAgeSeconds { get; set; } = 300;
    public double BiasMinConfidence { get; set; } = 0.30;
    public int BiasMaxAgeSeconds { get; set; } = 86_400;
    public double SkipSamplingRate { get; set; } = 0.05;
}
```

In `ToPolicy(name)`:

```csharp
SignatureCache = SignatureCache is { } sc
    ? new SignatureCacheOptions
    {
        Enabled = sc.Enabled,
        SkipMinConfidence = sc.SkipMinConfidence,
        SkipMaxAgeSeconds = sc.SkipMaxAgeSeconds,
        BiasMinConfidence = sc.BiasMinConfidence,
        BiasMaxAgeSeconds = sc.BiasMaxAgeSeconds,
        SkipSamplingRate = sc.SkipSamplingRate,
    }
    : new SignatureCacheOptions(),
```

- [ ] **Step 3.4: Verify build**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```
Expected: 0 errors.

- [ ] **Step 3.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs \
        src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs \
        src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs
git commit -m "$(cat <<'EOF'
feat(policy): SignatureCacheOptions thresholds (Skip/Bias/Miss)

Per-policy thresholds for the SignatureVerdictGate. Skip bypasses the
detector pipeline when a fresh, confident verdict is cached; Bias runs
the pipeline but injects the cached verdict as a prior; Miss runs the
full pipeline. SkipSamplingRate (default 5 percent) forces a pipeline
run on a fraction of Skip-eligible requests so the cache stays honest.

Wiring into the middleware happens in the next task.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `SignatureVerdictGate` and middleware integration

The gate runs at the top of `BotDetectionMiddleware` after the policy is resolved but before the orchestrator. Its decision drives whether we skip detection entirely, bias it, or run the full pipeline.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/SignatureVerdictGate.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictGateTests.cs`
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (new signal keys)

- [ ] **Step 4.1: Add the signal keys**

In `DetectionContext.cs` (the `SignalKeys` static class), add:

```csharp
    // Fingerprint prior, injected by SignatureVerdictGate on Bias decisions
    public const string FingerprintPriorProbability = "fingerprint.prior.probability";
    public const string FingerprintPriorConfidence  = "fingerprint.prior.confidence";
    public const string FingerprintPriorAgeSeconds  = "fingerprint.prior.age_seconds";
    public const string FingerprintPriorRequestCount = "fingerprint.prior.request_count";
```

- [ ] **Step 4.2: Write the failing test for the gate**

Create `src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictGateTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignatureVerdictGateTests
{
    private sealed class StubCache : ISignatureVerdictSource
    {
        private readonly SignatureVerdict? _verdict;
        public StubCache(SignatureVerdict? v) => _verdict = v;
        public Task<SignatureVerdict?> TryGetVerdictAsync(string sig, CancellationToken ct = default)
            => Task.FromResult(_verdict);
        public void Invalidate(string sig) { }
    }

    private static SignatureCacheOptions DefaultOpts() => new()
    {
        SkipMinConfidence = 0.85,
        SkipMaxAgeSeconds = 300,
        BiasMinConfidence = 0.30,
        BiasMaxAgeSeconds = 86_400,
        SkipSamplingRate = 0.0,
    };

    [Fact]
    public async Task NoSignature_ReturnsMiss()
    {
        var gate = new SignatureVerdictGate(new StubCache(null), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync(signature: null, DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task NoCachedVerdict_ReturnsMiss()
    {
        var gate = new SignatureVerdictGate(new StubCache(null), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-X", DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task ConfidentFreshVerdict_ReturnsSkip()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-A",
            BotProbability = 0.9,
            Confidence = 0.9,
            TotalRequestCount = 100,
            LastUpdatedUtc = DateTime.UtcNow.AddSeconds(-10),
        };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-A", DefaultOpts());
        Assert.Equal(GateAction.Skip, result.Action);
        Assert.NotNull(result.Verdict);
    }

    [Fact]
    public async Task StaleVerdict_DoesNotSkip()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-B",
            BotProbability = 0.9,
            Confidence = 0.9,
            LastUpdatedUtc = DateTime.UtcNow.AddSeconds(-3600), // 1h old, beyond Skip window
        };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-B", DefaultOpts());
        Assert.Equal(GateAction.Bias, result.Action);
    }

    [Fact]
    public async Task LowConfidence_ReturnsBias_NotSkip()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-C",
            BotProbability = 0.5,
            Confidence = 0.4,
            LastUpdatedUtc = DateTime.UtcNow,
        };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-C", DefaultOpts());
        Assert.Equal(GateAction.Bias, result.Action);
    }

    [Fact]
    public async Task VeryLowConfidence_ReturnsMiss()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-D",
            BotProbability = 0.5,
            Confidence = 0.10, // below BiasMinConfidence
            LastUpdatedUtc = DateTime.UtcNow,
        };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-D", DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task Disabled_AlwaysMiss()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-E",
            BotProbability = 0.9,
            Confidence = 0.9,
            LastUpdatedUtc = DateTime.UtcNow,
        };
        var opts = DefaultOpts() with { Enabled = false };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        var result = await gate.DecideAsync("sig-E", opts);
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task FullSampling_ForcesPipelineRun_OnSkipEligibleEntry()
    {
        var v = new SignatureVerdict
        {
            SignatureId = "sig-F",
            BotProbability = 0.9,
            Confidence = 0.9,
            LastUpdatedUtc = DateTime.UtcNow,
        };
        var opts = DefaultOpts() with { SkipSamplingRate = 1.0 };
        var gate = new SignatureVerdictGate(new StubCache(v), NullLogger<SignatureVerdictGate>.Instance);
        // With sampling rate = 1.0 every Skip-eligible entry is downgraded to Bias.
        var result = await gate.DecideAsync("sig-F", opts);
        Assert.Equal(GateAction.Bias, result.Action);
    }
}
```

- [ ] **Step 4.3: Implement `SignatureVerdictGate`**

Create `src/Mostlylucid.BotDetection/Services/SignatureVerdictGate.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

public enum GateAction
{
    /// <summary>No cache hit, or cache is too cold to be useful. Run the full pipeline.</summary>
    Miss,

    /// <summary>Cache hit suitable for biasing but not for skipping. Run the pipeline with the prior injected.</summary>
    Bias,

    /// <summary>Cache hit is fresh and confident enough. Skip the pipeline and enforce the cached verdict.</summary>
    Skip,
}

public sealed record GateDecision(GateAction Action, SignatureVerdict? Verdict);

public interface ISignatureVerdictSource
{
    Task<SignatureVerdict?> TryGetVerdictAsync(string signatureId, CancellationToken ct = default);
    void Invalidate(string signatureId);
}

/// <summary>
///     The verdict gate decides, per request, whether to skip the detector pipeline
///     (Skip), run it with a fingerprint prior injected (Bias), or run it fresh
///     (Miss). The decision is parameterised by the policy's
///     <see cref="SignatureCacheOptions"/>.
/// </summary>
public sealed class SignatureVerdictGate
{
    private readonly ISignatureVerdictSource _source;
    private readonly ILogger<SignatureVerdictGate> _logger;

    public SignatureVerdictGate(ISignatureVerdictSource source, ILogger<SignatureVerdictGate> logger)
    {
        _source = source;
        _logger = logger;
    }

    public async Task<GateDecision> DecideAsync(string? signature, SignatureCacheOptions options, CancellationToken ct = default)
    {
        if (!options.Enabled || string.IsNullOrEmpty(signature))
            return new GateDecision(GateAction.Miss, null);

        var verdict = await _source.TryGetVerdictAsync(signature, ct);
        if (verdict is null)
            return new GateDecision(GateAction.Miss, null);

        // Reject very low-confidence entries entirely; they are noise.
        if (verdict.Confidence < options.BiasMinConfidence)
            return new GateDecision(GateAction.Miss, verdict);

        var ageSeconds = verdict.LastUpdatedUtc is { } t
            ? (DateTime.UtcNow - t).TotalSeconds
            : double.MaxValue;

        var skipEligible =
            verdict.Confidence >= options.SkipMinConfidence
            && ageSeconds <= options.SkipMaxAgeSeconds;

        if (skipEligible && !ShouldRefresh(signature, options.SkipSamplingRate))
            return new GateDecision(GateAction.Skip, verdict);

        var biasEligible = ageSeconds <= options.BiasMaxAgeSeconds;
        return new GateDecision(biasEligible ? GateAction.Bias : GateAction.Miss, verdict);
    }

    /// <summary>
    ///     Deterministic refresh decision: a fraction of Skip-eligible requests are
    ///     downgraded to Bias so the pipeline runs and refreshes the persisted
    ///     verdict. Deterministic by signature hash so retries land identically.
    /// </summary>
    private static bool ShouldRefresh(string signature, double rate)
    {
        if (rate <= 0.0) return false;
        if (rate >= 1.0) return true;
        unchecked
        {
            var h = (uint)signature.GetHashCode() * 2654435761u;
            var bucket = (h % 10_000) / 10_000.0;
            return bucket < rate;
        }
    }
}
```

In `SignatureVerdictCache.cs`, add the interface implementation:

```csharp
public sealed class SignatureVerdictCache : ISignatureVerdictSource
{
    // existing implementation
}
```

- [ ] **Step 4.4: Run the tests, expect pass**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SignatureVerdictGateTests"
```
Expected: 8 pass.

- [ ] **Step 4.5: Wire the gate into `BotDetectionMiddleware`**

In `BotDetectionMiddleware.cs`, after the policy is resolved and BEFORE the LoadShed gate (or before the orchestrator try-catch if the LoadShed gate runs immediately above it), inject and consult the verdict gate.

Add to the constructor:

```csharp
    private readonly SignatureVerdictGate _verdictGate;
    private readonly SignatureVerdictCache _verdictCache;
    private readonly VarianceWatchdog _watchdog;
    private readonly ISignatureCoordinator _signatureCoordinator;
    // ... in constructor signature:
    SignatureVerdictGate verdictGate,
    SignatureVerdictCache verdictCache,
    VarianceWatchdog watchdog,
    ISignatureCoordinator signatureCoordinator,
    // ... in body:
    _verdictGate = verdictGate;
    _verdictCache = verdictCache;
    _watchdog = watchdog;
    _signatureCoordinator = signatureCoordinator;
```

Where the orchestrator is invoked (the call wrapped by the try-catch in 6.4.1), do the gate check first. On a Skip the watchdog gets veto power; on watchdog-trip we downgrade to a normal pipeline run. Even on a successful Skip we still record the observation in the sliding window so clustering / drift detection see it:

```csharp
        // Compute primary signature here. The signature engine likely already does this
        // inside the orchestrator. If a precomputed signature is not available, defer
        // gate consultation to inside the orchestrator's Wave 0. For the simple case,
        // assume context.Items["BotDetection.PrimarySignature"] is populated by an
        // upstream signature middleware.
        var precomputedSig = context.Items.TryGetValue("BotDetection.PrimarySignature", out var s)
            ? s as string : null;

        var gateDecision = await _verdictGate.DecideAsync(precomputedSig, policy.SignatureCache, context.RequestAborted);

        if (gateDecision.Action == GateAction.Skip)
        {
            var v = gateDecision.Verdict!;

            // Cheap variance check. The watchdog answers a single yes/no: does the
            // cached verdict still fit THIS request? If no, fall through to full
            // pipeline. Confidence is direction-agnostic so this same path serves
            // both sure-bot and sure-human cached verdicts.
            var watchdogResult = await _watchdog.CheckAsync(context, precomputedSig!, v, policy.SignatureCache.Watchdog);
            if (watchdogResult.Tripped)
            {
                context.Response.Headers["X-StyloBot-VerdictSource"] = "pipeline";
                context.Response.Headers["X-StyloBot-WatchdogTrip"] = watchdogResult.Reason ?? "unknown";
                // Fall through to the orchestrator call below; do NOT return here.
                // The cached verdict is invalidated for this signature so the
                // pipeline-produced fresh aggregate replaces it.
                _verdictCache.Invalidate(precomputedSig!);
            }
            else
            {
                // Enforce cached verdict without running the heavy pipeline.
                var cachedEvidence = new AggregatedEvidence
                {
                    BotProbability = v.BotProbability,
                    Confidence = v.Confidence,
                    RiskBand = v.RiskBand,
                    ThreatBand = ThreatBand.Low,
                    TotalProcessingTimeMs = 0,
                };
                context.Items["BotDetection.AggregatedEvidence"] = cachedEvidence;
                context.Response.Headers["X-StyloBot-VerdictSource"] = "cache";

                // Sliding-window record: clustering and drift analysis must still see
                // this request. NotifyObservation is a lightweight "I saw a request
                // for this signature at this time with this path" entry; it does NOT
                // re-run the orchestrator.
                _signatureCoordinator.NotifyObservation(precomputedSig!, context.Request.Path.Value ?? "/",
                    cachedEvidence.BotProbability);

                await _next(context);
                return;
            }
        }

        if (gateDecision.Action == GateAction.Bias && gateDecision.Verdict is { } v2)
        {
            context.Items[SignalKeys.FingerprintPriorProbability] = v2.BotProbability;
            context.Items[SignalKeys.FingerprintPriorConfidence]  = v2.Confidence;
            context.Items[SignalKeys.FingerprintPriorRequestCount] = v2.TotalRequestCount;
            context.Items[SignalKeys.FingerprintPriorAgeSeconds]   = v2.LastUpdatedUtc is { } t
                ? (DateTime.UtcNow - t).TotalSeconds : double.MaxValue;
        }
```

If `ISignatureCoordinator.NotifyObservation` does not exist, the closest equivalent is the existing per-request observation hook. Find the method that today's pipeline calls when a request completes and use that. If no lightweight observation method exists, add one whose only job is to append a `(signature, timestamp, path, last-known-prob)` entry to the in-memory sliding window WITHOUT triggering aggregate recomputation.

Read the middleware first to find the exact location relative to the LoadShed gate and the orchestrator try-catch. If precomputed signature is not available at this layer (likely the case: signature is computed inside the orchestrator), the gate logic moves INSIDE the orchestrator at the top of Wave 0 instead. Document the actual choice in your task report.

If the signature is computed downstream, an alternative is to add a thin upstream middleware `PrimarySignatureMiddleware` that computes the signature once and stashes it on `context.Items`. Both this middleware and the orchestrator's Wave 0 then use that precomputed value. For this plan, prefer the upstream-middleware approach: it lets the gate run before the orchestrator entirely.

- [ ] **Step 4.6: Add the gate registrations to DI**

In `ServiceCollectionExtensions.cs`, register the cache and gate:

```csharp
services.AddSingleton<SignatureVerdictCache>();
services.AddSingleton<ISignatureVerdictSource>(sp => sp.GetRequiredService<SignatureVerdictCache>());
services.AddSingleton<SignatureVerdictGate>();
```

- [ ] **Step 4.7: Run tests and the solution build**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName!~Puppeteer"
```
Expected: 0 build errors, 0 test failures. Update any middleware fixture that constructs `BotDetectionMiddleware` directly to pass stubs for the new parameters.

- [ ] **Step 4.8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs \
        src/Mostlylucid.BotDetection/Services/SignatureVerdictGate.cs \
        src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection/Models/DetectionContext.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Services/SignatureVerdictGateTests.cs
git commit -m "$(cat <<'EOF'
feat(gate): SignatureVerdictGate runs before the orchestrator

Skip: cache hit meets SkipMinConfidence AND age below SkipMaxAgeSeconds:
bypass the pipeline and enforce the cached verdict. SkipSamplingRate
forces a fraction of Skip-eligible requests to refresh.

Bias: cache hit meets BiasMinConfidence: inject prior as Wave 0 signals
(fingerprint.prior.*).

Miss: no cache or below BiasMinConfidence: run the full pipeline.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4b: `VarianceWatchdog` (cheap checks that veto a Skip)

The Skip path is "trust the cached verdict and bypass the heavy detectors" but the cache is only valid if nothing important changed. The watchdog runs a small set of cheap checks on every Skip candidate. If any one says "this looks different", the gate downgrades to Miss and the full pipeline runs.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/VarianceWatchdog.cs`
- Create: `src/Mostlylucid.BotDetection/Policies/VarianceWatchdogOptions.cs`
- Modify: `src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs` (add `Watchdog` property)
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs` (DTO + mapping)
- Create: `src/Mostlylucid.BotDetection.Test/Services/VarianceWatchdogTests.cs`

- [ ] **Step 4b.1: Create `VarianceWatchdogOptions`**

Create `src/Mostlylucid.BotDetection/Policies/VarianceWatchdogOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy sensitivities for <see cref="Services.VarianceWatchdog"/>. Each check
///     can be independently disabled. Sensitivity defaults are tuned for general-
///     purpose sites; high-security endpoints should set lower thresholds (more
///     watchdog trips, more pipeline runs).
/// </summary>
public sealed record VarianceWatchdogOptions
{
    /// <summary>Trip when the same primary signature appears from a new /24 within this many seconds. 0 to disable.</summary>
    public int IpRotationWindowSeconds { get; init; } = 300;

    /// <summary>Trip when the requested path's <c>RequestState</c> is not in the fingerprint's expected centroid set. Default true.</summary>
    public bool CheckPathCentroid { get; init; } = true;

    /// <summary>Trip when this fingerprint's recent request rate exceeds rolling mean by this multiplier. Default 10x. 0 to disable.</summary>
    public double RateSpikeMultiplier { get; init; } = 10.0;

    /// <summary>Master switch. Default true. Disable to make every Skip-eligible request actually Skip without checks (testing only).</summary>
    public bool Enabled { get; init; } = true;
}
```

- [ ] **Step 4b.2: Add `Watchdog` to `SignatureCacheOptions`**

In `SignatureCacheOptions.cs` (created in Task 3), add the property:

```csharp
    /// <summary>Variance watchdog sensitivities. Defaults are appropriate for general-purpose sites.</summary>
    public VarianceWatchdogOptions Watchdog { get; init; } = new();
```

In `DetectionPolicyConfiguration.cs`, add corresponding DTO field and mapping (parallel to the `SignatureCache` binding from Task 3).

- [ ] **Step 4b.3: Write the failing tests**

Create `src/Mostlylucid.BotDetection.Test/Services/VarianceWatchdogTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class VarianceWatchdogTests
{
    private static SignatureVerdict CachedHuman() => new()
    {
        SignatureId = "sig-X",
        BotProbability = 0.04,
        Confidence = 0.9,
        RiskBand = RiskBand.Low,
        TotalRequestCount = 50,
        LastUpdatedUtc = DateTime.UtcNow.AddSeconds(-30),
    };

    private static HttpContext CtxFrom(string ip, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        ctx.Request.Path = path;
        return ctx;
    }

    [Fact]
    public async Task NoChange_DoesNotTrip()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        // Prime with a baseline observation
        await watchdog.RecordObservationAsync("sig-X", "10.0.0.5", "/blog/post");

        var ctx = CtxFrom("10.0.0.5", "/blog/post");
        var result = await watchdog.CheckAsync(ctx, "sig-X", CachedHuman(), new VarianceWatchdogOptions());
        Assert.False(result.Tripped);
    }

    [Fact]
    public async Task IpRotation_WithinWindow_Trips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        await watchdog.RecordObservationAsync("sig-Y", "10.0.0.5", "/blog/post");

        // Same signature, different /24 (10.1.0.5), still inside default 300s window
        var ctx = CtxFrom("10.1.0.5", "/blog/post");
        var result = await watchdog.CheckAsync(ctx, "sig-Y", CachedHuman(), new VarianceWatchdogOptions());
        Assert.True(result.Tripped);
        Assert.Contains("ip", result.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IpRotation_DisabledViaOptions_DoesNotTrip()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        await watchdog.RecordObservationAsync("sig-Z", "10.0.0.5", "/");
        var ctx = CtxFrom("10.1.0.5", "/");
        var opts = new VarianceWatchdogOptions { IpRotationWindowSeconds = 0 };
        var result = await watchdog.CheckAsync(ctx, "sig-Z", CachedHuman(), opts);
        Assert.False(result.Tripped);
    }

    [Fact]
    public async Task RateSpike_WhenObservedRateExceedsMultiplier_Trips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        for (var i = 0; i < 200; i++)
            await watchdog.RecordObservationAsync("sig-R", "10.0.0.5", "/api");

        var ctx = CtxFrom("10.0.0.5", "/api");
        var opts = new VarianceWatchdogOptions { RateSpikeMultiplier = 2.0 };
        var result = await watchdog.CheckAsync(ctx, "sig-R", CachedHuman(), opts);
        Assert.True(result.Tripped);
    }

    [Fact]
    public async Task Disabled_NeverTrips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        await watchdog.RecordObservationAsync("sig-D", "10.0.0.5", "/");
        var ctx = CtxFrom("10.1.0.5", "/");
        var opts = new VarianceWatchdogOptions { Enabled = false };
        var result = await watchdog.CheckAsync(ctx, "sig-D", CachedHuman(), opts);
        Assert.False(result.Tripped);
    }
}
```

The `CheckPathCentroid` test is intentionally omitted from this first cut because it depends on `CentroidSequenceStore` integration. Add a TODO in the watchdog implementation noting the centroid check is a follow-up, and update the tests at that time. The IP-rotation and rate-spike checks are independent and self-contained for v1.

- [ ] **Step 4b.4: Run the tests, expect compile failure**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~VarianceWatchdogTests"
```
Expected: `VarianceWatchdog` does not exist.

- [ ] **Step 4b.5: Implement `VarianceWatchdog`**

Create `src/Mostlylucid.BotDetection/Services/VarianceWatchdog.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

public sealed record WatchdogResult(bool Tripped, string? Reason);

/// <summary>
///     Lightweight per-signature watchdog that guards the Skip path. The middleware
///     calls <see cref="CheckAsync"/> on every Skip candidate; a Tripped result
///     downgrades Skip to Miss for this request. The same middleware calls
///     <see cref="RecordObservationAsync"/> on every request (Skip, Bias, or Miss)
///     so the watchdog's per-signature state stays current.
///
///     The watchdog answers a yes/no: "does the cached verdict still fit?" It does
///     NOT score the request and is NOT a detector. Its only job is to detect that
///     a known fingerprint is doing something unusual enough that the cache should
///     be invalidated and the full pipeline rerun.
/// </summary>
public sealed class VarianceWatchdog
{
    private readonly ILogger<VarianceWatchdog> _logger;
    private readonly ConcurrentDictionary<string, FingerprintHistory> _history = new();

    private sealed class FingerprintHistory
    {
        public string? LastIp24;
        public DateTime LastIp24SeenUtc;
        public readonly ConcurrentQueue<DateTime> RecentObservations = new();
    }

    public VarianceWatchdog(ILogger<VarianceWatchdog> logger) => _logger = logger;

    public Task RecordObservationAsync(string signature, string clientIp, string path, CancellationToken ct = default)
    {
        var hist = _history.GetOrAdd(signature, _ => new FingerprintHistory());
        var slash24 = Slash24(clientIp);
        if (slash24 is not null)
        {
            hist.LastIp24 = slash24;
            hist.LastIp24SeenUtc = DateTime.UtcNow;
        }
        hist.RecentObservations.Enqueue(DateTime.UtcNow);
        TrimObservationsOlderThan(hist, TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task<WatchdogResult> CheckAsync(HttpContext ctx, string signature, SignatureVerdict cached, VarianceWatchdogOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return Task.FromResult(new WatchdogResult(false, null));

        if (!_history.TryGetValue(signature, out var hist))
            return Task.FromResult(new WatchdogResult(false, null));

        // IP rotation check
        if (options.IpRotationWindowSeconds > 0 && hist.LastIp24 is { } prevIp)
        {
            var currentIp = Slash24(ctx.Connection.RemoteIpAddress?.ToString());
            if (currentIp is not null
                && !string.Equals(currentIp, prevIp, StringComparison.Ordinal)
                && (DateTime.UtcNow - hist.LastIp24SeenUtc).TotalSeconds <= options.IpRotationWindowSeconds)
            {
                return Task.FromResult(new WatchdogResult(true,
                    $"ip-rotation:{prevIp}->{currentIp}"));
            }
        }

        // Rate spike check (current short-window rate vs rolling baseline)
        if (options.RateSpikeMultiplier > 0)
        {
            var (current, baseline) = ComputeRates(hist);
            if (baseline > 0 && current >= baseline * options.RateSpikeMultiplier)
            {
                return Task.FromResult(new WatchdogResult(true,
                    $"rate-spike:{current:F1}vs{baseline:F1}"));
            }
        }

        // CheckPathCentroid is a follow-up: requires CentroidSequenceStore reference.

        return Task.FromResult(new WatchdogResult(false, null));
    }

    private static string? Slash24(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return ip; // IPv6: full address as key
        var bytes = addr.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
    }

    private static (double current, double baseline) ComputeRates(FingerprintHistory hist)
    {
        var now = DateTime.UtcNow;
        var oneMinuteAgo = now - TimeSpan.FromMinutes(1);
        var fiveMinutesAgo = now - TimeSpan.FromMinutes(5);
        var currentCount = 0;
        var baselineCount = 0;
        foreach (var t in hist.RecentObservations)
        {
            if (t >= oneMinuteAgo) currentCount++;
            if (t >= fiveMinutesAgo) baselineCount++;
        }
        var currentRate = currentCount; // per minute
        var baselineRate = baselineCount / 5.0; // avg per minute over 5 min
        return (currentRate, baselineRate);
    }

    private static void TrimObservationsOlderThan(FingerprintHistory hist, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        while (hist.RecentObservations.TryPeek(out var t) && t < cutoff)
            hist.RecentObservations.TryDequeue(out _);
    }
}
```

- [ ] **Step 4b.6: Register the watchdog in DI**

In `ServiceCollectionExtensions.cs`, next to the gate/cache registrations:

```csharp
services.AddSingleton<VarianceWatchdog>();
```

- [ ] **Step 4b.7: Run tests, expect pass**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~VarianceWatchdogTests"
```
Expected: 5 pass.

- [ ] **Step 4b.8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/VarianceWatchdog.cs \
        src/Mostlylucid.BotDetection/Policies/VarianceWatchdogOptions.cs \
        src/Mostlylucid.BotDetection/Policies/SignatureCacheOptions.cs \
        src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Services/VarianceWatchdogTests.cs
git commit -m "$(cat <<'EOF'
feat(watchdog): cheap variance checks that veto a Skip

VarianceWatchdog runs three cheap per-signature checks on every Skip
candidate: IP rotation within a short window (same fingerprint, new /24),
rate spike (current 1-minute rate vs 5-minute baseline), and path
centroid (deferred to a follow-up). A tripped check forces the
middleware to invalidate the cached verdict and rerun the full pipeline.

This closes the cache-correctness hole: known fingerprints get fast-path
treatment unless something cheap-to-detect changes, in which case the
expensive detection still runs.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `FingerprintPriorContributor` (Wave 0, consumes the Bias signals)

The Bias decision writes `fingerprint.prior.*` signals. A Wave 0 contributor reads them and emits a normal `DetectionContribution` so the orchestrator's existing weighted-sum aggregation pulls the posterior toward the prior.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintPriorContributor.cs`
- Create: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/fingerprintprior.detector.yaml`
- Create: `src/Mostlylucid.BotDetection.Test/Orchestration/FingerprintPriorContributorTests.cs`
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (register)

- [ ] **Step 5.1: Create the YAML manifest**

Create `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/fingerprintprior.detector.yaml`:

```yaml
name: FingerprintPriorContributor
priority: 4
enabled: true
description: Injects the cached fingerprint verdict as a prior contribution.

scope:
  sink: botdetection
  coordinator: detection
  atom: priors

taxonomy:
  kind: bias
  determinism: deterministic
  persistence: ephemeral

input:
  accepts:
    - type: botdetection.request
      required: true
  required_signals: []
  optional_signals:
    - fingerprint.prior.probability
    - fingerprint.prior.confidence
    - fingerprint.prior.age_seconds

output:
  signals: []

triggers:
  requires: []
  skip_when: []

lane:
  name: fast
  max_concurrency: 16
  priority: 96

defaults:
  weights:
    base: 0.0
    bot_signal: 1.0
    human_signal: 1.0
    verified: 0.0
    early_exit: 0.0

  confidence:
    neutral: 0.0
    bot_detected: 0.5
    human_indicated: -0.5
    strong_signal: 0.8
    high_threshold: 0.7
    low_threshold: 0.2
    escalation_threshold: 0.0

  timing:
    timeout_ms: 2
    cache_refresh_sec: 0

  features:
    detailed_logging: false
    enable_cache: false
    can_early_exit: false
    can_escalate: false

  parameters:
    # Multiplier applied to prior confidence to derive the contribution weight.
    # A prior_confidence of 0.6 with multiplier 1.0 produces an effective weight
    # of 0.6 against the per-request observation, so the prior pulls the score
    # toward itself by 60 percent.
    prior_weight_multiplier: 1.0
    # Linear decay of prior weight by age. Effective weight =
    # prior_confidence * multiplier * max(0, 1 - age_seconds / decay_horizon).
    age_decay_horizon_seconds: 86400

tags:
  - fast-path
  - prior
  - stage-0
```

- [ ] **Step 5.2: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/Orchestration/FingerprintPriorContributorTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class FingerprintPriorContributorTests
{
    private static BlackboardState StateWith(params (string Key, object Value)[] signals)
    {
        var dict = new ConcurrentDictionary<string, object>(signals.ToDictionary(t => t.Key, t => t.Value));
        var ctx = new DefaultHttpContext();
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = dict,
            SignalWriter = dict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero,
        };
    }

    private static FingerprintPriorContributor Build(Dictionary<string, object>? overrides = null)
    {
        var configProvider = new StubConfigProvider(overrides);
        return new FingerprintPriorContributor(
            NullLogger<FingerprintPriorContributor>.Instance, configProvider);
    }

    // Re-use the StubConfigProvider class defined in ContentSequenceContributorTests; if it's
    // not visible from this test class, copy the minimal stub here.

    [Fact]
    public async Task NoPrior_ContributesNothing()
    {
        var contrib = Build();
        var state = StateWith();
        var result = await contrib.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task HumanPrior_ContributesHumanBias()
    {
        var contrib = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.05),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  10.0));

        var result = await contrib.ContributeAsync(state);
        Assert.Single(result);
        Assert.True(result[0].ConfidenceDelta < 0,
            $"Human prior should produce a negative confidence delta, got {result[0].ConfidenceDelta}");
    }

    [Fact]
    public async Task BotPrior_ContributesBotBias()
    {
        var contrib = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.92),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  10.0));

        var result = await contrib.ContributeAsync(state);
        Assert.Single(result);
        Assert.True(result[0].ConfidenceDelta > 0,
            $"Bot prior should produce a positive confidence delta, got {result[0].ConfidenceDelta}");
    }

    [Fact]
    public async Task OldPrior_DecaysToZeroWeight()
    {
        var contrib = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.95),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  86_400.0 * 10)); // 10x decay horizon

        var result = await contrib.ContributeAsync(state);
        // Effective weight should be clamped to 0; either no contribution or zero weight.
        if (result.Count > 0)
            Assert.Equal(0.0, result[0].Weight, precision: 3);
    }
}
```

If `StubConfigProvider` is private to the existing test file, copy a minimal version into this test file.

- [ ] **Step 5.3: Run the tests, expect compile failures**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~FingerprintPriorContributorTests"
```
Expected: `FingerprintPriorContributor` does not exist.

- [ ] **Step 5.4: Implement `FingerprintPriorContributor`**

Create `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintPriorContributor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Wave 0 contributor that injects the cached fingerprint verdict as a prior bias.
///     Reads fingerprint.prior.* signals written by <see cref="Services.SignatureVerdictGate"/>
///     and emits a single normal-weighted contribution so the orchestrator's existing
///     aggregation pulls the per-request posterior toward the prior. Linear decay by
///     age so very old priors lose all weight.
/// </summary>
public class FingerprintPriorContributor : ConfiguredContributorBase
{
    private readonly ILogger<FingerprintPriorContributor> _logger;

    public FingerprintPriorContributor(
        ILogger<FingerprintPriorContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "FingerprintPrior";
    public override int Priority => Manifest?.Priority ?? 4;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    private double WeightMultiplier => GetParam("prior_weight_multiplier", 1.0);
    private double AgeDecayHorizon => GetParam("age_decay_horizon_seconds", 86_400.0);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var prob = state.GetSignal<double?>(SignalKeys.FingerprintPriorProbability);
        var conf = state.GetSignal<double?>(SignalKeys.FingerprintPriorConfidence);
        if (prob is null || conf is null)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        var age = state.GetSignal<double?>(SignalKeys.FingerprintPriorAgeSeconds) ?? 0.0;
        var decay = Math.Max(0.0, 1.0 - age / AgeDecayHorizon);
        var effectiveWeight = conf.Value * WeightMultiplier * decay;

        if (effectiveWeight <= 0.0)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        // Map prior probability to a confidence delta in [-1, +1]:
        //   prob = 0.0 -> delta = -1.0 (strong human)
        //   prob = 0.5 -> delta =  0.0 (neutral)
        //   prob = 1.0 -> delta = +1.0 (strong bot)
        var delta = 2.0 * (prob.Value - 0.5);

        var contribution = prob.Value >= 0.5
            ? BotContribution(
                "FingerprintPrior",
                $"Cached fingerprint verdict (prob={prob:F2}, conf={conf:F2}, age={age:F0}s)",
                confidenceDelta: delta,
                weightOverride: effectiveWeight)
            : HumanContribution(
                "FingerprintPrior",
                $"Cached fingerprint verdict (prob={prob:F2}, conf={conf:F2}, age={age:F0}s)",
                confidenceDelta: delta,
                weightOverride: effectiveWeight);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[] { contribution });
    }
}
```

If the existing helper signatures `BotContribution` / `HumanContribution` do not accept `weightOverride`, look at the base class and use whichever overload is available. If they all use the base weights from YAML, set the YAML `weights.bot_signal` / `weights.human_signal` to 1.0 and rely on `ConfidenceDelta` scaling alone. If you need to inject a non-default weight, you may need to build the `DetectionContribution` directly: see how other contributors do it.

- [ ] **Step 5.5: Register the contributor**

In `ServiceCollectionExtensions.cs`, register it next to other Wave 0 contributors:

```csharp
services.AddSingleton<IContributingDetector, FingerprintPriorContributor>();
```

- [ ] **Step 5.6: Run tests, expect pass**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~FingerprintPriorContributorTests"
```
Expected: 4 pass.

- [ ] **Step 5.7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintPriorContributor.cs \
        src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/fingerprintprior.detector.yaml \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/FingerprintPriorContributorTests.cs
git commit -m "$(cat <<'EOF'
feat(detector): FingerprintPriorContributor injects cached verdict as bias

Reads fingerprint.prior.{probability,confidence,age_seconds} signals
written by SignatureVerdictGate on Bias decisions and emits a single
Wave 0 contribution. ConfidenceDelta maps prior probability to [-1, +1];
effective weight is prior_confidence * multiplier * linear-age-decay.
Old priors lose weight, fresh confident priors strongly anchor the
posterior.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Per-request contribution delta on `AggregatedEvidence`

So the dashboard can show "this request contributed +0.1pp" instead of "this request scored 38%", expose the delta on the evidence record.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionContribution.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/AggregatorOrSimilar.cs` (wherever `ToAggregatedEvidence` lives)

- [ ] **Step 6.1: Add the delta to `AggregatedEvidence`**

In `DetectionContribution.cs`, in the `AggregatedEvidence` record:

```csharp
    /// <summary>
    ///     Posterior minus prior: how much this single request moved the fingerprint's
    ///     belief, in absolute probability units. Zero when there was no prior (cold
    ///     start). Negative if the request was confirmatory of human, positive if
    ///     confirmatory of bot. The CLI dashboard surfaces this as the per-row delta
    ///     so request rows are not misread as standalone verdicts.
    /// </summary>
    public double RequestContributionDelta { get; init; }

    /// <summary>The prior probability that was applied (zero if no prior was used).</summary>
    public double PriorProbability { get; init; }
```

- [ ] **Step 6.2: Compute the delta during aggregation**

Find the method that builds `AggregatedEvidence` (likely `ToAggregatedEvidence` on a `ContributionAggregator` or similar in `Mostlylucid.BotDetection/Orchestration`). Search:

```bash
grep -rn "ToAggregatedEvidence" src/Mostlylucid.BotDetection --include="*.cs" | head -5
```

At the build site:

```csharp
        var priorContribution = contributions.FirstOrDefault(c => c.DetectorName == "FingerprintPrior");
        var priorProb = priorContribution is { } pc
            ? 0.5 + 0.5 * pc.ConfidenceDelta // inverse of the contributor's mapping
            : 0.0;

        return new AggregatedEvidence
        {
            // ... existing fields
            BotProbability = posterior,
            PriorProbability = priorProb,
            RequestContributionDelta = priorContribution is null ? 0.0 : posterior - priorProb,
        };
```

If reverse-mapping the prior from its contribution is fragile, an alternative is to read `state.GetSignal<double>(SignalKeys.FingerprintPriorProbability)` directly at aggregation time. Choose whichever the aggregator has cleaner access to.

- [ ] **Step 6.3: Quick smoke test (no new test file)**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName!~Puppeteer"
```
Expected: 0 errors, 0 test failures. Existing aggregation tests will exercise the new fields (default 0.0); behaviour is unchanged unless the prior contributor fired.

- [ ] **Step 6.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/DetectionContribution.cs \
        src/Mostlylucid.BotDetection/Orchestration/  # any aggregator file you touched
git commit -m "$(cat <<'EOF'
feat(evidence): expose request-contribution delta on AggregatedEvidence

PriorProbability records what the fingerprint's cached verdict was for
this request; RequestContributionDelta is the posterior minus the prior.
Lets the CLI dashboard surface a stable fingerprint score with the
per-request delta as a separate signal, rather than presenting each
request's absolute probability as if it were the fingerprint's verdict.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: CLI dashboard: surface the fingerprint score and per-request delta

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Console/Services/LiveDetectionTable.cs`

- [ ] **Step 7.1: Plumb the delta through `DetectionEntry`**

Extend the `DetectionEntry` record:

```csharp
public sealed record DetectionEntry(
    DateTime Timestamp,
    string Path,
    double BotProbability,        // posterior shown to the user
    double PriorProbability,      // fingerprint's prior at request time
    double RequestContributionDelta,
    string Verdict,
    string TopDetector,
    string? BotName,
    string? ActionPolicy,
    string? Country,
    double DetectionTimeMs,
    int DetectorCount,
    string? PrimarySignature,
    RiskBand RiskBand,
    ThreatBand ThreatBand,
    double ThreatScore,
    string VerdictSource); // "pipeline" or "cache"
```

In `DetectionTapMiddleware.InvokeAsync`, populate the new fields from `ev`:

```csharp
            _sink.Write(new DetectionEntry(
                DateTime.Now,
                context.Request.Path.Value ?? "/",
                ev.BotProbability,
                ev.PriorProbability,
                ev.RequestContributionDelta,
                isBot ? "BOT" : "HUMAN",
                detector,
                ev.PrimaryBotName,
                ev.TriggeredActionPolicyName,
                country,
                ev.TotalProcessingTimeMs,
                ev.ContributingDetectors?.Count ?? 0,
                primarySig,
                ev.RiskBand,
                ev.ThreatBand,
                ev.ThreatScore,
                context.Response.Headers.TryGetValue("X-StyloBot-VerdictSource", out var vs) ? vs.ToString() : "pipeline"));
```

- [ ] **Step 7.2: Replace the feed Bot% column with a delta column**

Find the feed header and `FormatFeedRow` (touched in the previous CLI work). Replace the `Bot%` column with `\u0394` (delta) showing the per-request contribution to the fingerprint score, signed:

```csharp
        // Header
        + "  " + VPadL("\u0394", 5)
        // Row
        + "  " + deltaColour + VPadL(deltaStr, 5) + C.R
```

where:

```csharp
        var delta = e.RequestContributionDelta;
        var deltaSign = delta >= 0 ? "+" : "";
        var deltaStr = $"{deltaSign}{delta * 100:F1}";
        var deltaColour = Math.Abs(delta) < 0.02 ? C.Dim
            : delta > 0 ? C.Yellow
            : C.Green;
```

Keep the Risk and Intent columns from the prior pass. Drop the absolute Bot% from the feed entirely; the headline number lives in the sidebar now.

- [ ] **Step 7.3: Sidebar shows posterior as the headline + sparkline**

In `BuildSideLines`, the Top Fingerprints section now reads the EWMA in-memory state we already track and shows the posterior (BotProbability), not the latest per-request value:

```csharp
foreach (var (sig, stat) in fps)
{
    var bullet = stat.IsBot ? C.Red + "\u25a0" : C.Green + "\u25a0";
    var sigTail = sig.Length > 10 ? sig[^10..] : sig;
    var posterior = $"{stat.Ewma * 100:F0}%"; // requires Ewma to be wired from Ingest
    var spark = MicroSpark(stat.RecentScores, stat.RecentScoresCount);
    var (rTxt, rCol) = FormatRiskCell(stat.LastRisk);
    var line = " " + bullet + C.R + " "
        + VPad(sigTail, 10)
        + "  " + (stat.IsBot ? C.Red : C.Green) + VPadL(posterior, 4) + C.R
        + " " + C.Blue + spark + C.R
        + "  " + rCol + VPad(rTxt, 3) + C.R;
    Row(line);
}
```

Implement `MicroSpark`:

```csharp
private static string MicroSpark(double[] vals, int count)
{
    if (count == 0) return new string(' ', 8);
    var chars = "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588"; // ▁▂▃▄▅▆▇█
    var sb = new StringBuilder(8);
    var pad = 8 - count;
    sb.Append(' ', pad);
    for (var i = 0; i < count; i++)
    {
        var v = Math.Clamp(vals[i], 0.0, 1.0);
        var idx = (int)Math.Floor(v * (chars.Length - 1));
        sb.Append(chars[idx]);
    }
    return sb.ToString();
}
```

Wire EWMA in `Ingest` (left from the earlier session): when a fingerprint stat is updated, blend with `Ewma = 0.3 * latest + 0.7 * prior`, and call `stat.Push(entry.BotProbability)`.

- [ ] **Step 7.4: Build the Console project**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Mostlylucid.BotDetection.Console/Mostlylucid.BotDetection.Console.csproj
```
Expected: 0 errors.

- [ ] **Step 7.5: Commit**

```bash
git add src/Mostlylucid.BotDetection.Console/Services/LiveDetectionTable.cs
git commit -m "$(cat <<'EOF'
feat(cli-dashboard): show fingerprint posterior and per-request delta

Feed rows now display the request contribution delta (signed percentage
points moved against the fingerprint's prior) instead of the absolute
per-request Bot%. The sidebar's Top Fingerprints shows the fingerprint's
posterior with an 8-sample sparkline so volatility is visible without
being misread as the fingerprint's verdict.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Invalidation hook: clear the cache when the pipeline writes a fresh verdict

When a Miss or Bias decision runs the full pipeline and `SignatureCoordinator.RecordRequestAsync` updates the persisted aggregate, the cache entry for that signature must be invalidated so the next request sees the fresh value.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/SignatureCoordinator.cs` (or wherever `RecordRequestAsync` ends)

- [ ] **Step 8.1: Inject the cache and invalidate after persistence**

Find `SignatureCoordinator.RecordRequestAsync` (or the equivalent write path). Inject `SignatureVerdictCache` into the constructor and call `Invalidate(signature)` after the upsert completes. If `SignatureCoordinator` does not directly call the upsert, the call lives in `SignaturePersistenceService` or similar; place the invalidation there.

The call is one line; do not introduce new abstractions. Pattern:

```csharp
    await _store.UpsertSignatureAsync(...);
    _verdictCache.Invalidate(signatureId);
```

- [ ] **Step 8.2: Smoke-build**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName!~Puppeteer"
```
Expected: 0 build errors, 0 test failures.

- [ ] **Step 8.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/  # whichever file you touched
git commit -m "$(cat <<'EOF'
feat(cache): invalidate SignatureVerdictCache after persistence write

When the pipeline runs and SignaturePersistenceService writes a fresh
aggregate to the signatures table, drop the cached verdict so the next
request sees the updated value. Without this the cache would serve stale
verdicts indefinitely after a pipeline run.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Documentation: `fingerprint-verdict-cache.md`

**Files:**
- Create: `src/Mostlylucid.BotDetection/docs/fingerprint-verdict-cache.md`

- [ ] **Step 9.1: Write the doc**

Cover (each section under 100 words):
1. **The core scaling thesis**: known fingerprints reuse their verdict; only unknown / stale fingerprints run the pipeline.
2. **Skip / Bias / Miss**: the three gate outcomes, thresholds, and policy knobs.
3. **EWMA, not MAX**: how a fingerprint's persisted probability now decays over observations.
4. **Sampling refresh**: why even Skip-eligible requests sometimes run the pipeline (defaults 5 percent), and how to tune.
5. **Per-request contribution delta**: what the new `RequestContributionDelta` means on `AggregatedEvidence` and how the CLI dashboard renders it.
6. **High-security endpoints**: pattern for disabling the cache on admin endpoints via `SignatureCache.Enabled = false`.

Cite types: `SignatureVerdictCache`, `SignatureVerdictGate`, `FingerprintPriorContributor`, `SignatureCacheOptions`.

No em dashes.

- [ ] **Step 9.2: Commit**

```bash
git add src/Mostlylucid.BotDetection/docs/fingerprint-verdict-cache.md
git commit -m "$(cat <<'EOF'
docs: fingerprint-verdict-cache reference

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Verification (tests + AOT + CHANGELOG)

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 10.1: Full test run**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName!~Puppeteer" --logger "console;verbosity=minimal"
dotnet test src/Mostlylucid.BotDetection.Api.Tests --logger "console;verbosity=minimal"
dotnet test src/Mostlylucid.BotDetection.Demo.Tests --logger "console;verbosity=minimal"
dotnet test src/Stylobot.Gateway.Tests --logger "console;verbosity=minimal"
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests --filter "FullyQualifiedName!~Puppeteer" --logger "console;verbosity=minimal"
```
Expected: 0 failures across all projects. The new tests add approximately:
- SignatureUpsertEwmaTests: 3
- SignatureVerdictCacheTests: 4
- SignatureVerdictGateTests: 8
- VarianceWatchdogTests: 5
- FingerprintPriorContributorTests: 4

- [ ] **Step 10.2: AOT publish**

```bash
dotnet publish src/Mostlylucid.BotDetection.Console -c Release -r osx-arm64 -o /tmp/stylobot-aot 2>&1 | tail -5
```
Expected: 0 errors. Confirm no new IL2026 / IL3050 warnings from the added files:

```bash
dotnet publish src/Mostlylucid.BotDetection.Console -c Release -r osx-arm64 -o /tmp/stylobot-aot 2>&1 \
  | grep -E "SignatureVerdict|FingerprintPrior" || echo "no new AOT warnings"
```
Expected: `no new AOT warnings`.

- [ ] **Step 10.3: Add CHANGELOG entry**

In `CHANGELOG.md`, before the existing topmost entry, add a new section. **DO NOT pick a version number; leave it as `[Unreleased]` until the user specifies.** The user has been explicit that version bumps are not Claude's call.

```markdown
## [Unreleased] - 2026-05-13

Wires the per-signature reputation aggregate the product has been computing (in the `signatures` SQLite table) into the live request path. Four behaviours emerge per policy:

- **Skip**: cache hit meets `SkipMinConfidence` (direction-agnostic: sure-bot AND sure-human qualify) AND is younger than `SkipMaxAgeSeconds`. The watchdog confirms nothing variant. The request bypasses the heavy detector pipeline and is answered from the cached verdict. Emits `X-StyloBot-VerdictSource: cache`. Sliding-window observation is still recorded for clustering / drift.
- **Watchdog-trip**: Skip-eligible cache hit BUT the `VarianceWatchdog` detected an unusual signal (IP rotation, rate spike, future: path centroid divergence). Cache is invalidated for this signature; full pipeline runs to produce a fresh verdict. Emits `X-StyloBot-WatchdogTrip: <reason>`.
- **Bias**: cache hit meets `BiasMinConfidence`: pipeline runs with the cached verdict injected as a Wave 0 prior contribution. The posterior is pulled toward the prior in proportion to prior confidence and age decay.
- **Miss**: no usable cache: full pipeline runs from scratch.

Also fixes a latent persistence bug and surfaces the new model in the CLI dashboard.

### Added

- **`SignatureVerdictCache`** (`Mostlylucid.BotDetection/Services/SignatureVerdictCache.cs`) -read-through cache over the `signatures` table, keyed by primary signature. Invalidate on persistence write.
- **`SignatureVerdictGate`** (`Mostlylucid.BotDetection/Services/SignatureVerdictGate.cs`) -decides Skip / Bias / Miss per request based on the policy's `SignatureCacheOptions`. Skip sampling rate (default 5 percent) forces a fraction of Skip-eligible requests to refresh the cache.
- **`VarianceWatchdog`** (`Mostlylucid.BotDetection/Services/VarianceWatchdog.cs`) -cheap per-signature checks (IP rotation within window, rate spike vs rolling baseline; path centroid is a follow-up). Vetoes a Skip when any check trips; the cached verdict is invalidated and the full pipeline runs. The watchdog is what makes the Skip path safe: known fingerprints get fast-path treatment unless cheap signals indicate variance.
- **`FingerprintPriorContributor`** (Wave 0, priority 4) -emits a single contribution from the cached prior when the gate returns Bias. Effective weight is `prior_confidence * multiplier * linear-age-decay`, so old priors lose all weight.
- **`SignatureCacheOptions`** on `DetectionPolicy` -per-policy thresholds (`SkipMinConfidence`, `SkipMaxAgeSeconds`, `BiasMinConfidence`, `BiasMaxAgeSeconds`, `SkipSamplingRate`, `Enabled`) plus a nested `Watchdog` of type `VarianceWatchdogOptions`. JSON-bindable via the existing `DetectionPolicyConfiguration`.
- **`AggregatedEvidence.RequestContributionDelta` and `.PriorProbability`** -let downstream consumers (CLI dashboard, headers, audits) display the per-request contribution to the fingerprint score instead of the absolute per-request probability.
- **CLI dashboard** -feed now shows the per-request delta (signed percentage points) instead of the standalone Bot% per row. Sidebar Top Fingerprints shows the fingerprint's posterior with an 8-sample sparkline so volatility is visible without being mistaken for the fingerprint's verdict.
- **YAML manifest** `fingerprintprior.detector.yaml` for the new contributor.

### Fixed

- **`signatures.bot_probability` upsert was MAX, now EWMA**. A signature that scored 0.95 once was pinned at 0.95 forever, regardless of subsequent benign observations. The upsert now blends `(1 - alpha) * prior + alpha * observation` with `alpha = 0.15` (configurable via `BotDetectionOptions.SignatureEwmaAlpha`). Old high-risk priors now decay toward benign observations as the entity continues to behave.
- **`signatures.last_updated_utc`** column added (with migration) so the verdict gate can apply freshness thresholds.

### Changed

- **`BotDetectionMiddleware`** -now consults `SignatureVerdictGate` at request intake. On Skip the cached verdict is enforced and the heavy pipeline is bypassed, but the watchdog still runs and the sliding window still records the observation (clustering and drift detection do not see a hole). On watchdog-trip the cache is invalidated and the full pipeline runs. On Bias the prior is written to `context.Items` as `fingerprint.prior.*` signals; on Miss behaviour is unchanged.
- **`ISignatureCoordinator.NotifyObservation`** (new lightweight hook, called on Skip) -records signature, timestamp, path, and last-known probability in the sliding window without re-running the orchestrator. Existing callers on the Miss / Bias path continue to use the heavier `RecordRequestAsync`.
- **`SignaturePersistenceService`** (or wherever the upsert call lives) -calls `SignatureVerdictCache.Invalidate(signatureId)` after writing so subsequent requests see fresh state.

### Tests Added

- `SignatureUpsertEwmaTests` (3): literal first observation, EWMA decay on repeated benign observations, `last_updated_utc` recording.
- `SignatureVerdictCacheTests` (4): no-data null, persisted hit, reference-equal caching, invalidation.
- `SignatureVerdictGateTests` (8): no signature, no cache, fresh confident hit (Skip), stale (Bias not Skip), low confidence (Bias), very low (Miss), disabled, sampling refresh (Skip downgraded to Bias).
- `VarianceWatchdogTests` (5): no change does not trip, IP rotation within window trips, IP rotation disabled does not trip, rate spike trips, disabled never trips.
- `FingerprintPriorContributorTests` (4): no prior, human prior, bot prior, old prior decayed.

Total new tests: 24.

### Documentation

- `docs/fingerprint-verdict-cache.md` -the new reference doc covering the Skip/Bias/Miss thesis, threshold tuning, EWMA, sampling refresh, the per-request delta, and the high-security disable pattern.
```

Verify no em dashes:

```bash
grep -- '—' CHANGELOG.md | head -5 && echo "(pre-existing em dashes only)"
```

- [ ] **Step 10.4: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs(changelog): fingerprint verdict cache entry

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Notes for the executor

- Per project rule (`feedback_no_emdash`): no em dashes anywhere, ever. Use hyphens, colons, commas, parentheses.
- Per project rule (`feedback_never_push_without_approval`): never push. Commits stay local until the user instructs.
- Per project rule (`feedback_verify_before_checkin`): run the affected test slice before each commit. Full project before Tasks 6 and 8 commits.
- Per project rule: never decide a version number unilaterally. Leave `[Unreleased]` in the CHANGELOG until the user picks 6.4.x / 6.5.0 / 7.0.
- The plan does NOT implement entity-resolution multi-signature merge priors. If the same actor appears with two primary signatures, each fingerprint has its own EWMA. Merging them when entity resolution promotes one to absorb the other is a separate plan (track in the `entities` schema that already exists).
- The plan keeps the per-request scoring path itself unchanged: `BotProbability` is still a weighted sum of contributions, the prior just becomes one more contribution. No Bayesian rewrite of the aggregator.
- The Skip path emits `X-StyloBot-VerdictSource: cache` so customers can monitor cache hit rate. The metric belongs in the existing monitoring pipeline (not implemented here; follow-up).
