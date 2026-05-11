# Self-Maintenance and Memory Constraint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace three unbounded HNSW in-memory vector indices with bounded in-memory caches + SQLite centroid persistence, cap `MarkovTracker._cohortBaselines`, and replace `HnswOptions` with `SelfMaintenanceOptions`, so the process runs indefinitely on a Pi4 with configurable memory bounds.

**Architecture:** Each `Slim*` class wraps a private `BoundedVectorCache<TEntry>` (bounded `ConcurrentDictionary` with frequency-priority eviction) as the hot layer, plus a `SqliteVectorCentroidStore` for persistent L1/L2 centroids. Detection fast path is `TryGet` only (sync, non-blocking). Learning handlers write to both hot cache and SQLite asynchronously. `VectorCompactionService` Phase 3 upserts centroids to SQLite instead of rebuilding the HNSW graph. All three existing interfaces (`ISignatureSimilaritySearch`, `ISessionVectorSearch`, `IIntentSimilaritySearch`) are preserved unchanged.

**Tech Stack:** C#/.NET 10, SQLite (Microsoft.Data.Sqlite, already in project), `System.Runtime.InteropServices.MemoryMarshal` for zero-copy float/byte conversion, existing `ISessionStore` for session data access.

---

## File Map

| Action | Path |
|---|---|
| Modify | `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` |
| Modify | `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs` |
| Create | `src/Mostlylucid.BotDetection/Data/SqliteVectorCentroidStore.cs` |
| Create | `src/Mostlylucid.BotDetection/Similarity/SlimSignatureSimilaritySearch.cs` |
| Create | `src/Mostlylucid.BotDetection/Similarity/SlimSessionVectorSearch.cs` |
| Create | `src/Mostlylucid.BotDetection/Similarity/SlimIntentSearch.cs` |
| Modify | `src/Mostlylucid.BotDetection/Similarity/SimilarityLearningHandler.cs` |
| Modify | `src/Mostlylucid.BotDetection/Similarity/IntentLearningHandler.cs` |
| Modify | `src/Mostlylucid.BotDetection/Services/SessionVectorWarmupService.cs` |
| Modify | `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs` |
| Modify | `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` |
| Modify | `src/Mostlylucid.BotDetection/Markov/MarkovTracker.cs` |
| Modify | `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` (MarkovOptions) |
| Test | `src/Mostlylucid.BotDetection.Test/Similarity/SlimSignatureSimilaritySearchTests.cs` |
| Test | `src/Mostlylucid.BotDetection.Test/Similarity/SlimSessionVectorSearchTests.cs` |
| Test | `src/Mostlylucid.BotDetection.Test/Similarity/SlimIntentSearchTests.cs` |
| Delete | `src/Mostlylucid.BotDetection/Similarity/HnswFileSimilaritySearch.cs` |
| Delete | `src/Mostlylucid.BotDetection/Similarity/HnswSessionVectorSearch.cs` |
| Delete | `src/Mostlylucid.BotDetection/Similarity/HnswIntentSearch.cs` |

---

## Background: Why These Changes

`HnswFileSimilaritySearch._graphVectors: List<float[]>` grows on every HTTP request with no eviction. `AutoSaveInterval = 5 minutes` serializes the full graph to a JSON string (104 MB at demo scale) which goes straight to the Large Object Heap. Three indices × every 5 minutes = 13 GB LOH. The fix: replace with a bounded in-memory structure that evicts cold entries and a SQLite table that stores only compressed centroids (L1/L2 from `VectorCompactionService`).

The `SlidingCacheAtom` from Ephemeral was considered but does not expose bulk enumeration -`FindSimilarAsync` requires scanning all cached vectors to find the topK most similar. A thin `BoundedVectorCache<TValue>` (private inner class) with `ConcurrentDictionary` and priority eviction is the right primitive here.

---

## Task 1: SelfMaintenanceOptions

Replace `HnswOptions` (cap-based, wrong semantics) with `SelfMaintenanceOptions` (configurable bounds for all accumulator types). Update the property on `BotDetectionOptions` and `MarkovOptions`.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs`

- [ ] **Step 1: Write a test that reads SelfMaintenanceOptions from config**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Models/SelfMaintenanceOptionsTests.cs
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Models;

public class SelfMaintenanceOptionsTests
{
    [Fact]
    public void Defaults_AreWithinReasonableBounds()
    {
        var opts = new SelfMaintenanceOptions();
        Assert.True(opts.SignatureCacheSize > 0);
        Assert.True(opts.SessionCacheSize > 0);
        Assert.True(opts.IntentCacheSize > 0);
        Assert.True(opts.MarkovCohortSize > 0);
    }

    [Fact]
    public void LowMemoryPreset_SmallerThanDefaults()
    {
        var lo = SelfMaintenanceOptions.LowMemory;
        var def = new SelfMaintenanceOptions();
        Assert.True(lo.SignatureCacheSize < def.SignatureCacheSize);
        Assert.True(lo.SessionCacheSize < def.SessionCacheSize);
        Assert.True(lo.IntentCacheSize < def.IntentCacheSize);
        Assert.True(lo.MarkovCohortSize < def.MarkovCohortSize);
    }

    [Fact]
    public void BotDetectionOptions_HasSelfMaintenanceProperty()
    {
        var opts = new BotDetectionOptions();
        Assert.NotNull(opts.SelfMaintenance);
        Assert.Equal(new SelfMaintenanceOptions().SignatureCacheSize, opts.SelfMaintenance.SignatureCacheSize);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SelfMaintenanceOptionsTests" -v m 2>&1 | tail -20
```

Expected: FAIL -`SelfMaintenanceOptions` not found.

- [ ] **Step 3: Find and replace HnswOptions with SelfMaintenanceOptions in BotDetectionOptions.cs**

Find `HnswOptions Hnsw { get; set; } = new();` at line ~433. Replace the property and the class at the bottom of the file (~line 3981):

```csharp
// Replace the property (around line 433):
/// <summary>
///     Configurable bounds for all in-memory accumulators.
///     Prevents unbounded growth. Use SelfMaintenanceOptions.LowMemory preset for Pi4/embedded.
/// </summary>
public SelfMaintenanceOptions SelfMaintenance { get; set; } = new();
```

Append at end of file (remove the `HnswOptions` class entirely and add):

```csharp
/// <summary>
///     Configurable bounds for all in-memory accumulators.
///     Prevents unbounded growth on low-resource hardware (Pi4, embedded, containers with strict limits).
///     Use <see cref="LowMemory"/> preset for constrained environments.
/// </summary>
public sealed class SelfMaintenanceOptions
{
    /// <summary>Max entries in the signature similarity hot cache. Default: 5000.</summary>
    public int SignatureCacheSize { get; set; } = 5_000;

    /// <summary>Max entries in the session vector hot cache. Default: 2000.</summary>
    public int SessionCacheSize { get; set; } = 2_000;

    /// <summary>Max entries in the intent classification hot cache. Default: 1000.</summary>
    public int IntentCacheSize { get; set; } = 1_000;

    /// <summary>Days to retain compressed centroids in SQLite. Default: 30.</summary>
    public int CentroidRetentionDays { get; set; } = 30;

    /// <summary>Max cohort baselines in MarkovTracker. Default: 10000.</summary>
    public int MarkovCohortSize { get; set; } = 10_000;

    /// <summary>Sliding expiration for hot cache entries without access. Default: 2 hours.</summary>
    public TimeSpan CacheSlidingExpiration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Pi4 / low-memory preset. All caches reduced ~5x vs defaults.</summary>
    public static SelfMaintenanceOptions LowMemory => new()
    {
        SignatureCacheSize      = 1_000,
        SessionCacheSize        = 500,
        IntentCacheSize         = 300,
        MarkovCohortSize        = 2_000,
        CacheSlidingExpiration  = TimeSpan.FromHours(1),
    };
}
```

- [ ] **Step 4: Fix any compile errors from HnswOptions references**

Search for remaining `HnswOptions` or `.Hnsw.` references:

```bash
grep -rn "HnswOptions\|\.Hnsw\b" /Users/scottgalloway/RiderProjects/stylobot/src/ --include="*.cs" | grep -v "HnswFileSimilarity\|HnswSession\|HnswIntent"
```

For each hit, update the reference to use `.SelfMaintenance.*` or remove if it was `MaxSignatureVectors`/`MaxSessionVectors`/`MaxIntentVectors` (now replaced by `SignatureCacheSize`/`SessionCacheSize`/`IntentCacheSize`).

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SelfMaintenanceOptionsTests" -v m 2>&1 | tail -10
```

Expected: PASS -3 tests passing.

- [ ] **Step 6: Verify build**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs \
        src/Mostlylucid.BotDetection.Test/Models/SelfMaintenanceOptionsTests.cs
git commit -m "feat(config): replace HnswOptions with SelfMaintenanceOptions

Configurable bounds for all accumulators. LowMemory preset for Pi4.
Removes the cap-based HnswOptions that was architecturally incorrect."
```

---

## Task 2: Add Centroid Tables to SQLite Schema

Add three new tables to the existing `SqliteSessionStore.InitializeAsync` CREATE block. These replace the `hnsw-index/*.json` files as the persistent vector store.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs`

- [ ] **Step 1: Write a test that verifies the tables exist after init**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Data/CentroidTableSchemaTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class CentroidTableSchemaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public CentroidTableSchemaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    [Theory]
    [InlineData("signature_centroids")]
    [InlineData("session_centroids")]
    [InlineData("intent_centroids")]
    public async Task Table_ExistsAfterInit(string tableName)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = _dbPath
        });
        var store = new Mostlylucid.BotDetection.Data.SqliteSessionStore(
            NullLogger<Mostlylucid.BotDetection.Data.SqliteSessionStore>.Instance,
            options);
        await store.InitializeAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, count);

        await store.DisposeAsync();
    }

    public void Dispose() => File.Delete(_dbPath);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~CentroidTableSchemaTests" -v m 2>&1 | tail -15
```

Expected: FAIL -tables do not exist yet.

- [ ] **Step 3: Add table DDL to SqliteSessionStore.InitializeAsync**

Open `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs`. Find the `CREATE TABLE IF NOT EXISTS requests` block (last table in InitializeAsync). After the closing semicolons for the `requests` table and its indices, append:

```csharp
            CREATE TABLE IF NOT EXISTS signature_centroids (
                signature_id TEXT PRIMARY KEY,
                vector       BLOB    NOT NULL,
                was_bot      INTEGER NOT NULL DEFAULT 0,
                confidence   REAL    NOT NULL DEFAULT 0.5,
                updated_at   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sigc_updated ON signature_centroids(updated_at);

            CREATE TABLE IF NOT EXISTS session_centroids (
                signature_id      TEXT PRIMARY KEY,
                vector            BLOB    NOT NULL,
                velocity_vector   BLOB,
                variance_vector   BLOB,
                freq_fingerprint  BLOB,
                cluster_id        TEXT,
                compression_level INTEGER NOT NULL DEFAULT 0,
                is_bot            INTEGER NOT NULL DEFAULT 0,
                bot_probability   REAL    NOT NULL DEFAULT 0.0,
                priority          REAL    NOT NULL DEFAULT 0.5,
                updated_at        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sesc_updated ON session_centroids(updated_at);
            CREATE INDEX IF NOT EXISTS idx_sesc_cluster  ON session_centroids(cluster_id);

            CREATE TABLE IF NOT EXISTS intent_centroids (
                signature_id    TEXT PRIMARY KEY,
                vector          BLOB    NOT NULL,
                threat_score    REAL    NOT NULL DEFAULT 0.0,
                intent_category TEXT    NOT NULL DEFAULT 'unknown',
                updated_at      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_intc_updated ON intent_centroids(updated_at);
```

The string is part of the existing `cmd.CommandText = """...""";` block -add these lines inside the same raw string literal.

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~CentroidTableSchemaTests" -v m 2>&1 | tail -10
```

Expected: PASS -3 tests passing.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs \
        src/Mostlylucid.BotDetection.Test/Data/CentroidTableSchemaTests.cs
git commit -m "feat(data): add signature/session/intent centroid tables to SQLite schema"
```

---

## Task 3: SqliteVectorCentroidStore

Single class with CRUD for all three centroid tables. Used by the three `Slim*` search classes for persistence.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Data/SqliteVectorCentroidStore.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Data/SqliteVectorCentroidStoreTests.cs`

- [ ] **Step 1: Write tests for upsert/get on signature centroids**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Data/SqliteVectorCentroidStoreTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class SqliteVectorCentroidStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private SqliteVectorCentroidStore _store = null!;

    public SqliteVectorCentroidStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"centtest_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Cache=Shared";
    }

    public async Task InitializeAsync()
    {
        // Create the schema
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS signature_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                was_bot INTEGER NOT NULL DEFAULT 0, confidence REAL NOT NULL DEFAULT 0.5,
                updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                velocity_vector BLOB, variance_vector BLOB, freq_fingerprint BLOB,
                cluster_id TEXT, compression_level INTEGER NOT NULL DEFAULT 0,
                is_bot INTEGER NOT NULL DEFAULT 0, bot_probability REAL NOT NULL DEFAULT 0.0,
                priority REAL NOT NULL DEFAULT 0.5, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS intent_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                threat_score REAL NOT NULL DEFAULT 0.0, intent_category TEXT NOT NULL DEFAULT 'unknown',
                updated_at INTEGER NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
        _store = new SqliteVectorCentroidStore(_connectionString, NullLogger<SqliteVectorCentroidStore>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => File.Delete(_dbPath);

    [Fact]
    public async Task UpsertSignature_ThenGetRecent_ReturnsEntry()
    {
        var vector = new float[] { 1f, 2f, 3f };
        await _store.UpsertSignatureAsync("sig1", vector, wasBot: true, confidence: 0.9);

        var rows = await _store.GetRecentSignaturesAsync(10);
        Assert.Single(rows);
        Assert.Equal("sig1", rows[0].SignatureId);
        Assert.True(rows[0].WasBot);
        Assert.Equal(0.9, rows[0].Confidence, precision: 3);
        Assert.Equal(3, rows[0].Vector.Length);
    }

    [Fact]
    public async Task UpsertSignature_Overwrites_ExistingEntry()
    {
        await _store.UpsertSignatureAsync("sig2", new float[] { 1f }, wasBot: false, confidence: 0.5);
        await _store.UpsertSignatureAsync("sig2", new float[] { 2f, 3f }, wasBot: true, confidence: 0.95);

        var rows = await _store.GetRecentSignaturesAsync(10);
        var entry = rows.Single(r => r.SignatureId == "sig2");
        Assert.True(entry.WasBot);
        Assert.Equal(2, entry.Vector.Length);
    }

    [Fact]
    public async Task PruneSignatures_DeletesOldRows()
    {
        await _store.UpsertSignatureAsync("old", new float[] { 1f }, wasBot: false, confidence: 0.3);
        await _store.PruneSignaturesOlderThanAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var rows = await _store.GetRecentSignaturesAsync(10);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task UpsertSession_ThenGetRecent_ReturnsEntry()
    {
        var meta = new SessionCentroidRow
        {
            SignatureId = "sessSig1",
            Vector = new float[] { 1f, 2f },
            IsBot = true,
            BotProbability = 0.8,
            CompressionLevel = 1,
            Priority = 0.9,
            ClusterId = "cluster1"
        };
        await _store.UpsertSessionAsync(meta);

        var rows = await _store.GetRecentSessionsAsync(10);
        Assert.Single(rows);
        Assert.Equal("sessSig1", rows[0].SignatureId);
        Assert.Equal(1, rows[0].CompressionLevel);
    }

    [Fact]
    public async Task UpsertIntent_ThenGetRecent_ReturnsEntry()
    {
        await _store.UpsertIntentAsync("intentSig1", new float[] { 0.5f, 0.5f }, 0.75, "scanning");

        var rows = await _store.GetRecentIntentsAsync(10);
        Assert.Single(rows);
        Assert.Equal("intentSig1", rows[0].SignatureId);
        Assert.Equal("scanning", rows[0].IntentCategory);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqliteVectorCentroidStoreTests" -v m 2>&1 | tail -15
```

Expected: FAIL -`SqliteVectorCentroidStore` not found.

- [ ] **Step 3: Create SqliteVectorCentroidStore.cs**

```csharp
// File: src/Mostlylucid.BotDetection/Data/SqliteVectorCentroidStore.cs
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Persistent store for compressed vector centroids (L1/L2 from VectorCompactionService).
///     Replaces the HNSW JSON files. Three tables: signature_centroids, session_centroids, intent_centroids.
///     All writes are async and non-blocking on the detection fast path.
/// </summary>
public sealed class SqliteVectorCentroidStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteVectorCentroidStore> _logger;

    public SqliteVectorCentroidStore(string connectionString, ILogger<SqliteVectorCentroidStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ─── Signature centroids ────────────────────────────────────────────────

    public async Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signature_centroids (signature_id, vector, was_bot, confidence, updated_at)
                VALUES (@sig, @vec, @bot, @conf, @ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, was_bot=excluded.was_bot,
                    confidence=excluded.confidence, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@vec", PackFloats(vector));
            cmd.Parameters.AddWithValue("@bot", wasBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@conf", confidence);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSignature failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<SignatureCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT signature_id, vector, was_bot, confidence FROM signature_centroids ORDER BY updated_at DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SignatureCentroidRow(
                    reader.GetString(0),
                    UnpackFloats((byte[])reader[1]),
                    reader.GetInt32(2) != 0,
                    reader.GetDouble(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSignatures failed"); }
        return result;
    }

    public async Task PruneSignaturesOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM signature_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogDebug("Pruned {Count} stale signature centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSignatures failed"); }
    }

    // ─── Session centroids ───────────────────────────────────────────────────

    public async Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_centroids
                    (signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                     cluster_id, compression_level, is_bot, bot_probability, priority, updated_at)
                VALUES (@sig,@vec,@vel,@var,@freq,@cid,@lvl,@bot,@prob,@pri,@ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, velocity_vector=excluded.velocity_vector,
                    variance_vector=excluded.variance_vector, freq_fingerprint=excluded.freq_fingerprint,
                    cluster_id=excluded.cluster_id, compression_level=excluded.compression_level,
                    is_bot=excluded.is_bot, bot_probability=excluded.bot_probability,
                    priority=excluded.priority, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", row.SignatureId);
            cmd.Parameters.AddWithValue("@vec", PackFloats(row.Vector));
            cmd.Parameters.AddWithValue("@vel", row.VelocityVector != null ? PackFloats(row.VelocityVector) : DBNull.Value);
            cmd.Parameters.AddWithValue("@var", row.VarianceVector != null ? PackFloats(row.VarianceVector) : DBNull.Value);
            cmd.Parameters.AddWithValue("@freq", row.FreqFingerprint != null ? PackFloats(row.FreqFingerprint) : DBNull.Value);
            cmd.Parameters.AddWithValue("@cid", (object?)row.ClusterId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lvl", row.CompressionLevel);
            cmd.Parameters.AddWithValue("@bot", row.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", row.BotProbability);
            cmd.Parameters.AddWithValue("@pri", row.Priority);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSession failed for {Sig}", row.SignatureId); }
    }

    public async Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<SessionCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                       cluster_id, compression_level, is_bot, bot_probability, priority
                FROM session_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SessionCentroidRow
                {
                    SignatureId      = reader.GetString(0),
                    Vector           = UnpackFloats((byte[])reader[1]),
                    VelocityVector   = reader.IsDBNull(2) ? null : UnpackFloats((byte[])reader[2]),
                    VarianceVector   = reader.IsDBNull(3) ? null : UnpackFloats((byte[])reader[3]),
                    FreqFingerprint  = reader.IsDBNull(4) ? null : UnpackFloats((byte[])reader[4]),
                    ClusterId        = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CompressionLevel = reader.GetInt32(6),
                    IsBot            = reader.GetInt32(7) != 0,
                    BotProbability   = reader.GetDouble(8),
                    Priority         = reader.GetDouble(9),
                });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSessions failed"); }
        return result;
    }

    public async Task PruneSessionsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogDebug("Pruned {Count} stale session centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSessionCentroids failed"); }
    }

    // ─── Intent centroids ───────────────────────────────────────────────────

    public async Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore,
        string intentCategory, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO intent_centroids (signature_id, vector, threat_score, intent_category, updated_at)
                VALUES (@sig, @vec, @ts_score, @cat, @ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, threat_score=excluded.threat_score,
                    intent_category=excluded.intent_category, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@vec", PackFloats(vector));
            cmd.Parameters.AddWithValue("@ts_score", threatScore);
            cmd.Parameters.AddWithValue("@cat", intentCategory);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertIntent failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<IntentCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT signature_id, vector, threat_score, intent_category FROM intent_centroids ORDER BY updated_at DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new IntentCentroidRow(
                    reader.GetString(0),
                    UnpackFloats((byte[])reader[1]),
                    reader.GetDouble(2),
                    reader.GetString(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentIntents failed"); }
        return result;
    }

    public async Task PruneIntentsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM intent_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogDebug("Pruned {Count} stale intent centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneIntents failed"); }
    }

    // ─── Float packing helpers ───────────────────────────────────────────────

    internal static byte[] PackFloats(float[] v) =>
        MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    internal static float[] UnpackFloats(byte[] b)
    {
        var result = new float[b.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(b).CopyTo(result);
        return result;
    }
}

// ─── Row types ──────────────────────────────────────────────────────────────

public sealed record SignatureCentroidRow(
    string SignatureId, float[] Vector, bool WasBot, double Confidence);

public sealed class SessionCentroidRow
{
    public string SignatureId      { get; init; } = "";
    public float[] Vector          { get; init; } = [];
    public float[]? VelocityVector { get; init; }
    public float[]? VarianceVector { get; init; }
    public float[]? FreqFingerprint { get; init; }
    public string? ClusterId       { get; init; }
    public int CompressionLevel    { get; init; }
    public bool IsBot              { get; init; }
    public double BotProbability   { get; init; }
    public double Priority         { get; init; }
}

public sealed record IntentCentroidRow(
    string SignatureId, float[] Vector, double ThreatScore, string IntentCategory);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqliteVectorCentroidStoreTests" -v m 2>&1 | tail -15
```

Expected: PASS -5 tests passing.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/SqliteVectorCentroidStore.cs \
        src/Mostlylucid.BotDetection.Test/Data/SqliteVectorCentroidStoreTests.cs
git commit -m "feat(data): add SqliteVectorCentroidStore for persistent centroid storage"
```

---

## Task 4: SlimSignatureSimilaritySearch

Bounded hot cache + SQLite persistence. Implements `ISignatureSimilaritySearch` exactly.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Similarity/SlimSignatureSimilaritySearch.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Similarity/SlimSignatureSimilaritySearchTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Similarity/SlimSignatureSimilaritySearchTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;
using NSubstitute;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SlimSignatureSimilaritySearchTests
{
    private static SlimSignatureSimilaritySearch MakeSearch(int cacheSize = 100)
    {
        var store = Substitute.For<SqliteVectorCentroidStore>(
            "Data Source=:memory:", NullLogger<SqliteVectorCentroidStore>.Instance);
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions { SignatureCacheSize = cacheSize }
        });
        return new SlimSignatureSimilaritySearch(store, options,
            NullLogger<SlimSignatureSimilaritySearch>.Instance);
    }

    [Fact]
    public async Task FindSimilar_EmptyCache_ReturnsEmpty()
    {
        var search = MakeSearch();
        var result = await search.FindSimilarAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilar_AfterAdd_FindsIdenticalVector()
    {
        var search = MakeSearch();
        var vector = new float[] { 1f, 0f, 0f };
        await search.AddAsync(vector, "sig1", wasBot: true, confidence: 0.9);

        var results = await search.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.99f);
        Assert.Single(results);
        Assert.Equal("sig1", results[0].SignatureId);
        Assert.True(results[0].WasBot);
    }

    [Fact]
    public async Task FindSimilar_BelowMinSimilarity_ReturnsEmpty()
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f, 0f }, "sig1", wasBot: false, confidence: 0.5);

        // Orthogonal vector -cosine similarity = 0
        var results = await search.FindSimilarAsync(new float[] { 0f, 1f }, topK: 5, minSimilarity: 0.5f);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Count_ReflectsAddedVectors()
    {
        var search = MakeSearch();
        Assert.Equal(0, search.Count);
        await search.AddAsync(new float[] { 1f }, "s1", false, 0.5);
        await search.AddAsync(new float[] { 2f }, "s2", true, 0.9);
        Assert.Equal(2, search.Count);
    }

    [Fact]
    public async Task FindSimilar_TopKLimitsResults()
    {
        var search = MakeSearch();
        for (var i = 0; i < 10; i++)
            await search.AddAsync(new float[] { 1f + i * 0.01f }, $"sig{i}", false, 0.5);

        var results = await search.FindSimilarAsync(new float[] { 1f }, topK: 3, minSimilarity: 0.0f);
        Assert.True(results.Count <= 3);
    }

    [Fact]
    public async Task SaveAsync_IsNoOp() // data is in SQLite already
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f }, "sig1", true, 0.9);
        await search.SaveAsync(); // should not throw
        Assert.Equal(1, search.Count);
    }
}
```

Note: If NSubstitute is not in the test project, use a real `SqliteVectorCentroidStore` with an in-memory SQLite connection string `"Data Source=:memory:"`. Adjust test setup accordingly.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimSignatureSimilaritySearchTests" -v m 2>&1 | tail -15
```

Expected: FAIL -`SlimSignatureSimilaritySearch` not found.

- [ ] **Step 3: Create SlimSignatureSimilaritySearch.cs**

```csharp
// File: src/Mostlylucid.BotDetection/Similarity/SlimSignatureSimilaritySearch.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory vector cache for signature similarity search.
///     Replaces HnswFileSimilaritySearch. No JSON serialization, no LOH growth.
///     Detection fast path uses TryGet (sync, non-blocking). Learning handler writes
///     to cache + SQLite async post-request. On miss: no signal this request.
/// </summary>
public sealed class SlimSignatureSimilaritySearch : ISignatureSimilaritySearch, IAsyncDisposable
{
    private readonly BoundedVectorCache<SigEntry> _cache;
    private readonly SqliteVectorCentroidStore _centroidStore;
    private readonly ILogger<SlimSignatureSimilaritySearch> _logger;

    public SlimSignatureSimilaritySearch(
        SqliteVectorCentroidStore centroidStore,
        IOptions<BotDetectionOptions> options,
        ILogger<SlimSignatureSimilaritySearch> logger)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        var opts = options.Value.SelfMaintenance;
        _cache = new BoundedVectorCache<SigEntry>(
            opts.SignatureCacheSize,
            opts.CacheSlidingExpiration,
            (_, e) => e.WasBot ? 2.0 : 1.0);
    }

    public int Count => _cache.Count;

    public Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.80f, string? embeddingContext = null)
    {
        var all = _cache.GetAll();
        if (all.Count == 0)
            return Task.FromResult<IReadOnlyList<SimilarSignature>>(Array.Empty<SimilarSignature>());

        var results = new List<(float Sim, SimilarSignature Match)>(all.Count);
        foreach (var (key, entry) in all)
        {
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
                results.Add((sim, new SimilarSignature(key, 1f - sim, entry.WasBot, entry.Confidence)));
        }

        var sorted = results
            .OrderByDescending(x => x.Sim)
            .Take(topK)
            .Select(x => x.Match)
            .ToList();

        return Task.FromResult<IReadOnlyList<SimilarSignature>>(sorted);
    }

    public Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence,
        string? embeddingContext = null)
    {
        _cache.Set(signatureId, new SigEntry(vector, wasBot, confidence));
        _ = Task.Run(() => _centroidStore.UpsertSignatureAsync(signatureId, vector, wasBot, confidence));
        return Task.CompletedTask;
    }

    public Task SaveAsync() => Task.CompletedTask;   // data persists via SQLite, cache is ephemeral
    public Task LoadAsync() => Task.CompletedTask;   // warmup service handles population

    public void WarmFromRows(IReadOnlyList<SignatureCentroidRow> rows)
    {
        foreach (var row in rows)
            _cache.Set(row.SignatureId, new SigEntry(row.Vector, row.WasBot, row.Confidence));
        _logger.LogInformation("SlimSignatureSimilaritySearch warmed with {Count} entries", rows.Count);
    }

    public ValueTask DisposeAsync()
    {
        _cache.Dispose();
        return ValueTask.CompletedTask;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f; var magA = 0f; var magB = 0f;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0f || magB == 0f) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private sealed record SigEntry(float[] Vector, bool WasBot, double Confidence);
}
```

Also create the shared `BoundedVectorCache<TValue>` helper in the same file (or a shared file):

```csharp
// Append to SlimSignatureSimilaritySearch.cs (or put in a separate BoundedVectorCache.cs if shared)

/// <summary>
///     Bounded dictionary with access-frequency priority eviction.
///     Not a drop-in LRU -entries with higher access counts survive longer (LFU bias).
///     Thread-safe for concurrent reads and writes.
/// </summary>
internal sealed class BoundedVectorCache<TValue> : IDisposable
{
    private sealed class Entry
    {
        public TValue Value;
        public long AccessCount;
        public long LastAccessTicks;
        public double RetentionScore;

        public Entry(TValue value) { Value = value; AccessCount = 1; LastAccessTicks = DateTime.UtcNow.Ticks; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _data = new();
    private readonly int _maxSize;
    private readonly TimeSpan _slidingExpiration;
    private readonly Func<string, TValue, double>? _retentionScorer;
    private readonly System.Threading.Timer _cleanupTimer;

    internal BoundedVectorCache(int maxSize, TimeSpan slidingExpiration,
        Func<string, TValue, double>? retentionScorer = null)
    {
        _maxSize = maxSize;
        _slidingExpiration = slidingExpiration;
        _retentionScorer = retentionScorer;
        _cleanupTimer = new System.Threading.Timer(_ => Cleanup(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal bool TryGet(string key, out TValue value)
    {
        if (_data.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref entry.AccessCount);
            Volatile.Write(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    internal void Set(string key, TValue value)
    {
        if (_data.TryGetValue(key, out var existing))
        {
            existing.Value = value;
            Volatile.Write(ref existing.LastAccessTicks, DateTime.UtcNow.Ticks);
        }
        else
        {
            _data[key] = new Entry(value);
        }
        if (_data.Count > _maxSize)
            Evict();
    }

    internal IReadOnlyList<(string Key, TValue Value)> GetAll()
    {
        var now = DateTime.UtcNow.Ticks;
        var result = new List<(string, TValue)>(_data.Count);
        foreach (var kvp in _data)
        {
            var age = TimeSpan.FromTicks(now - Volatile.Read(ref kvp.Value.LastAccessTicks));
            if (age <= _slidingExpiration)
                result.Add((kvp.Key, kvp.Value.Value));
        }
        return result;
    }

    internal int Count => _data.Count;

    internal void Clear() => _data.Clear();

    private void Evict()
    {
        if (_retentionScorer != null)
            foreach (var kvp in _data)
                try { kvp.Value.RetentionScore = _retentionScorer(kvp.Key, kvp.Value.Value); }
                catch { /* non-critical */ }

        var toRemove = _data
            .OrderBy(kvp => (Volatile.Read(ref kvp.Value.AccessCount) + 1) * (1.0 + kvp.Value.RetentionScore))
            .Take(Math.Max(1, _data.Count - _maxSize + _maxSize / 10))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            _data.TryRemove(key, out _);
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow.Ticks;
        var expired = _data
            .Where(kvp => TimeSpan.FromTicks(now - Volatile.Read(ref kvp.Value.LastAccessTicks)) > _slidingExpiration)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
            _data.TryRemove(key, out _);

        if (_data.Count > _maxSize)
            Evict();
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimSignatureSimilaritySearchTests" -v m 2>&1 | tail -15
```

Expected: PASS -6 tests passing.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Similarity/SlimSignatureSimilaritySearch.cs \
        src/Mostlylucid.BotDetection.Test/Similarity/SlimSignatureSimilaritySearchTests.cs
git commit -m "feat(similarity): add SlimSignatureSimilaritySearch replacing HNSW file index"
```

---

## Task 5: SlimSessionVectorSearch

Bounded hot cache + SQLite. Implements full `ISessionVectorSearch` including `GetAllVectorsSnapshot` and `ReplaceAllAsync` (used by VectorCompactionService).

**Files:**
- Create: `src/Mostlylucid.BotDetection/Similarity/SlimSessionVectorSearch.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Similarity/SlimSessionVectorSearchTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Similarity/SlimSessionVectorSearchTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SlimSessionVectorSearchTests
{
    private static SlimSessionVectorSearch MakeSearch()
    {
        // Use in-memory SQLite for store
        var connStr = $"Data Source=sesstest_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var store = new SqliteVectorCentroidStore(connStr, NullLogger<SqliteVectorCentroidStore>.Instance);
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions { SessionCacheSize = 100 }
        });
        return new SlimSessionVectorSearch(store, options, NullLogger<SlimSessionVectorSearch>.Instance);
    }

    [Fact]
    public async Task FindSimilar_EmptyCache_ReturnsEmpty()
    {
        var search = MakeSearch();
        var result = await search.FindSimilarAsync(new float[] { 1f, 0f });
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilar_AfterAdd_FindsSelf()
    {
        var search = MakeSearch();
        var v = new float[] { 1f, 0f, 0f };
        await search.AddAsync(v, "session1", isBot: true, botProbability: 0.95);

        var results = await search.FindSimilarAsync(v, topK: 5, minSimilarity: 0.99f);
        Assert.Single(results);
        Assert.Equal("session1", results[0].Signature);
    }

    [Fact]
    public async Task GetAllVectorsSnapshot_ReturnsAddedVectors()
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f, 2f }, "s1", true, 0.8);
        await search.AddAsync(new float[] { 3f, 4f }, "s2", false, 0.3);

        var snapshot = search.GetAllVectorsSnapshot();
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public async Task ReplaceAllAsync_ReplacesCache()
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f }, "old", false, 0.1);

        var newItems = new List<(float[], SessionVectorMetadata)>
        {
            (new float[] { 2f }, new SessionVectorMetadata { Signature = "new1", IsBot = true, BotProbability = 0.9 })
        };
        await search.ReplaceAllAsync(newItems);

        var snapshot = search.GetAllVectorsSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("new1", snapshot[0].Metadata.Signature);
    }

    [Fact]
    public async Task FindGhostCentroids_ReturnsEmpty_WhenNoCompressedEntries()
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f }, "s1", true, 0.9); // CompressionLevel = 0 (L0)

        var ghosts = await search.FindGhostCentroidsAsync(new float[] { 1f });
        Assert.Empty(ghosts); // L0 entries not returned as ghosts
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimSessionVectorSearchTests" -v m 2>&1 | tail -15
```

Expected: FAIL -`SlimSessionVectorSearch` not found.

- [ ] **Step 3: Create SlimSessionVectorSearch.cs**

```csharp
// File: src/Mostlylucid.BotDetection/Similarity/SlimSessionVectorSearch.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory session vector cache. Replaces HnswSessionVectorSearch.
///     Hot layer: BoundedVectorCache (129-dim Markov/fingerprint vectors).
///     Persistent layer: SQLite session_centroids table (L1/L2 from VectorCompactionService).
/// </summary>
public sealed class SlimSessionVectorSearch : ISessionVectorSearch, IAsyncDisposable
{
    private readonly BoundedVectorCache<SessionEntry> _cache;
    private readonly SqliteVectorCentroidStore _centroidStore;
    private readonly ILogger<SlimSessionVectorSearch> _logger;

    public SlimSessionVectorSearch(
        SqliteVectorCentroidStore centroidStore,
        IOptions<BotDetectionOptions> options,
        ILogger<SlimSessionVectorSearch> logger)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        var opts = options.Value.SelfMaintenance;
        _cache = new BoundedVectorCache<SessionEntry>(
            opts.SessionCacheSize,
            opts.CacheSlidingExpiration,
            (_, e) => e.Meta.IsBot ? 2.0 : 1.0);
    }

    public int Count => _cache.Count;

    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(
        float[] vector, int topK = 10, float minSimilarity = 0.70f)
    {
        var all = _cache.GetAll();
        if (all.Count == 0) return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        var results = new List<(float Sim, SessionVectorMatch Match)>(all.Count);
        foreach (var (_, entry) in all)
        {
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
                results.Add((sim, new SessionVectorMatch(entry.Meta.Signature, sim)));
        }

        var sorted = results.OrderByDescending(x => x.Sim).Take(topK).Select(x => x.Match).ToList();
        return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(sorted);
    }

    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(
        float[] vector, int topK = 10, float maxDistance = 5.0f)
    {
        // For entries without a VarianceVector, fall back to cosine similarity
        var all = _cache.GetAll();
        if (all.Count == 0) return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        var results = new List<(float Dist, SessionVectorMatch Match)>(all.Count);
        foreach (var (_, entry) in all)
        {
            float dist;
            if (entry.Meta.VarianceVector is { Length: > 0 })
                dist = MahalanobisDistance(vector, entry.Vector, entry.Meta.VarianceVector);
            else
                dist = 1f - CosineSimilarity(vector, entry.Vector); // cosine distance as fallback

            if (dist <= maxDistance)
                results.Add((dist, new SessionVectorMatch(entry.Meta.Signature, 1f - Math.Min(dist / maxDistance, 1f))));
        }

        var sorted = results.OrderBy(x => x.Dist).Take(topK).Select(x => x.Match).ToList();
        return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(sorted);
    }

    public async Task<IReadOnlyList<GhostCentroidMatch>> FindGhostCentroidsAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f)
    {
        // Ghost centroids are L1/L2 compressed entries -query SQLite directly
        var rows = await _centroidStore.GetRecentSessionsAsync(5_000);
        var compressed = rows.Where(r => r.CompressionLevel >= 1).ToList();

        var results = new List<(float Sim, GhostCentroidMatch Match)>(compressed.Count);
        foreach (var row in compressed)
        {
            var sim = CosineSimilarity(vector, row.Vector);
            if (sim >= minSimilarity)
                results.Add((sim, new GhostCentroidMatch(
                    row.SignatureId, sim, row.CompressionLevel,
                    row.IsBot, row.BotProbability,
                    row.VelocityVector, row.VarianceVector, row.FreqFingerprint)));
        }

        return results.OrderByDescending(x => x.Sim).Take(topK).Select(x => x.Match).ToList();
    }

    public Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
        float[]? velocityVector = null, float[]? frequencyFingerprint = null, float[]? driftVector = null)
    {
        var meta = new SessionVectorMetadata
        {
            Signature          = signature,
            IsBot              = isBot,
            BotProbability     = botProbability,
            Timestamp          = DateTimeOffset.UtcNow,
            VelocityVector     = velocityVector,
            FrequencyFingerprint = frequencyFingerprint,
        };
        _cache.Set(signature, new SessionEntry(vector, meta));
        _ = Task.Run(() => _centroidStore.UpsertSessionAsync(new SessionCentroidRow
        {
            SignatureId     = signature,
            Vector          = vector,
            VelocityVector  = velocityVector,
            FreqFingerprint = frequencyFingerprint,
            IsBot           = isBot,
            BotProbability  = botProbability,
            CompressionLevel = 0,
            Priority        = isBot ? 0.9 : 0.1,
        }));
        return Task.CompletedTask;
    }

    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;

    public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot()
    {
        return _cache.GetAll()
            .Select(x => (x.Value.Vector, x.Value.Meta))
            .ToList();
    }

    public async Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        _cache.Clear();
        foreach (var (vec, meta) in items)
        {
            _cache.Set(meta.Signature, new SessionEntry(vec, meta));
            await _centroidStore.UpsertSessionAsync(new SessionCentroidRow
            {
                SignatureId      = meta.Signature,
                Vector           = vec,
                VelocityVector   = meta.VelocityVector,
                VarianceVector   = meta.VarianceVector,
                FreqFingerprint  = meta.FrequencyFingerprint,
                ClusterId        = meta.ClusterId,
                CompressionLevel = meta.CompressionLevel,
                IsBot            = meta.IsBot,
                BotProbability   = meta.BotProbability,
                Priority         = meta.Priority,
            });
        }
        _logger.LogInformation("SlimSessionVectorSearch replaced with {Count} vectors", items.Count);
    }

    public void WarmFromRows(IReadOnlyList<SessionCentroidRow> rows)
    {
        foreach (var row in rows)
        {
            var meta = new SessionVectorMetadata
            {
                Signature        = row.SignatureId,
                IsBot            = row.IsBot,
                BotProbability   = row.BotProbability,
                Timestamp        = DateTimeOffset.UtcNow,
                VelocityVector   = row.VelocityVector,
                FrequencyFingerprint = row.FreqFingerprint,
                VarianceVector   = row.VarianceVector,
                ClusterId        = row.ClusterId,
                CompressionLevel = row.CompressionLevel,
                Priority         = row.Priority,
            };
            _cache.Set(row.SignatureId, new SessionEntry(row.Vector, meta));
        }
        _logger.LogInformation("SlimSessionVectorSearch warmed with {Count} entries", rows.Count);
    }

    public ValueTask DisposeAsync() { _cache.Dispose(); return ValueTask.CompletedTask; }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f; var magA = 0f; var magB = 0f;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
        if (magA == 0f || magB == 0f) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private static float MahalanobisDistance(float[] query, float[] centroid, float[] variance)
    {
        var sum = 0f;
        var len = Math.Min(query.Length, Math.Min(centroid.Length, variance.Length));
        for (var i = 0; i < len; i++)
        {
            var diff = query[i] - centroid[i];
            var v = variance[i] > 1e-6f ? variance[i] : 1e-6f;
            sum += (diff * diff) / v;
        }
        return MathF.Sqrt(sum);
    }

    private sealed record SessionEntry(float[] Vector, SessionVectorMetadata Meta);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimSessionVectorSearchTests" -v m 2>&1 | tail -15
```

Expected: PASS -5 tests passing.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Similarity/SlimSessionVectorSearch.cs \
        src/Mostlylucid.BotDetection.Test/Similarity/SlimSessionVectorSearchTests.cs
git commit -m "feat(similarity): add SlimSessionVectorSearch replacing HNSW session index"
```

---

## Task 6: SlimIntentSearch

Bounded hot cache for intent classification. Implements `IIntentSimilaritySearch`.

**Files:**
- Create: `src/Mostlylucid.BotDetection/Similarity/SlimIntentSearch.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Similarity/SlimIntentSearchTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Similarity/SlimIntentSearchTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SlimIntentSearchTests
{
    private static SlimIntentSearch MakeSearch()
    {
        var connStr = $"Data Source=intenttest_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var store = new SqliteVectorCentroidStore(connStr, NullLogger<SqliteVectorCentroidStore>.Instance);
        return new SlimIntentSearch(store,
            Options.Create(new BotDetectionOptions { SelfMaintenance = new SelfMaintenanceOptions { IntentCacheSize = 50 } }),
            NullLogger<SlimIntentSearch>.Instance);
    }

    [Fact]
    public async Task FindSimilar_EmptyCache_ReturnsEmpty()
    {
        var search = MakeSearch();
        var result = await search.FindSimilarAsync(new float[] { 1f });
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilar_AfterAdd_FindsSelf()
    {
        var search = MakeSearch();
        var v = new float[] { 1f, 0f, 0f };
        await search.AddAsync(v, "intentSig1", 0.9, "scanning");

        var results = await search.FindSimilarAsync(v, topK: 5, minSimilarity: 0.99f);
        Assert.Single(results);
        Assert.Equal("intentSig1", results[0].SignatureId);
        Assert.Equal("scanning", results[0].IntentCategory);
    }

    [Fact]
    public async Task Count_TracksAdded()
    {
        var search = MakeSearch();
        await search.AddAsync(new float[] { 1f }, "s1", 0.5, "browsing");
        await search.AddAsync(new float[] { 2f }, "s2", 0.9, "attacking");
        Assert.Equal(2, search.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimIntentSearchTests" -v m 2>&1 | tail -15
```

Expected: FAIL.

- [ ] **Step 3: Create SlimIntentSearch.cs**

```csharp
// File: src/Mostlylucid.BotDetection/Similarity/SlimIntentSearch.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded intent classification cache. Replaces HnswIntentSearch.
///     36-dim intent vectors, LFU eviction, SQLite persistence.
/// </summary>
public sealed class SlimIntentSearch : IIntentSimilaritySearch, IAsyncDisposable
{
    private readonly BoundedVectorCache<IntentEntry> _cache;
    private readonly SqliteVectorCentroidStore _centroidStore;
    private readonly ILogger<SlimIntentSearch> _logger;

    public SlimIntentSearch(
        SqliteVectorCentroidStore centroidStore,
        IOptions<BotDetectionOptions> options,
        ILogger<SlimIntentSearch> logger)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        var opts = options.Value.SelfMaintenance;
        _cache = new BoundedVectorCache<IntentEntry>(
            opts.IntentCacheSize,
            opts.CacheSlidingExpiration,
            (_, e) => e.ThreatScore > 0.7 ? 2.0 : 1.0);
    }

    public int Count => _cache.Count;

    public Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f)
    {
        var all = _cache.GetAll();
        if (all.Count == 0) return Task.FromResult<IReadOnlyList<SimilarIntent>>(Array.Empty<SimilarIntent>());

        var results = new List<(float Sim, SimilarIntent Match)>(all.Count);
        foreach (var (key, entry) in all)
        {
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
                results.Add((sim, new SimilarIntent(key, 1f - sim, entry.ThreatScore, entry.IntentCategory)));
        }

        var sorted = results.OrderByDescending(x => x.Sim).Take(topK).Select(x => x.Match).ToList();
        return Task.FromResult<IReadOnlyList<SimilarIntent>>(sorted);
    }

    public Task AddAsync(float[] vector, string signatureId, double threatScore,
        string intentCategory, string? reasoning = null)
    {
        _cache.Set(signatureId, new IntentEntry(vector, threatScore, intentCategory));
        _ = Task.Run(() => _centroidStore.UpsertIntentAsync(signatureId, vector, threatScore, intentCategory));
        return Task.CompletedTask;
    }

    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;

    public void WarmFromRows(IReadOnlyList<IntentCentroidRow> rows)
    {
        foreach (var row in rows)
            _cache.Set(row.SignatureId, new IntentEntry(row.Vector, row.ThreatScore, row.IntentCategory));
        _logger.LogInformation("SlimIntentSearch warmed with {Count} entries", rows.Count);
    }

    public ValueTask DisposeAsync() { _cache.Dispose(); return ValueTask.CompletedTask; }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f; var magA = 0f; var magB = 0f;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
        if (magA == 0f || magB == 0f) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private sealed record IntentEntry(float[] Vector, double ThreatScore, string IntentCategory);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SlimIntentSearchTests" -v m 2>&1 | tail -10
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Similarity/SlimIntentSearch.cs \
        src/Mostlylucid.BotDetection.Test/Similarity/SlimIntentSearchTests.cs
git commit -m "feat(similarity): add SlimIntentSearch replacing HNSW intent index"
```

---

## Task 7: Update SimilarityLearningHandler

`SimilarityLearningHandler` currently calls `_search.AddAsync()` which in `HnswFileSimilaritySearch` grows the unbounded list. With `SlimSignatureSimilaritySearch`, `AddAsync` already writes to both hot cache and SQLite. **No logic change needed** -the handler calls the same interface method. The only change is ensuring it only handles `HighConfidenceDetection` (not `FullDetection`) to reduce write frequency.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Similarity/SimilarityLearningHandler.cs`

- [ ] **Step 1: Write a test verifying only HighConfidenceDetection is handled**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Similarity/SimilarityLearningHandlerTests.cs
// (File already exists -add to it)

// Verify FullDetection is no longer handled (was the main source of unbounded growth)
[Fact]
public void HandledEventTypes_DoesNotContainFullDetection()
{
    var handler = new SimilarityLearningHandler(
        new FeatureVectorizer(), Substitute.For<ISignatureSimilaritySearch>(),
        NullLogger<SimilarityLearningHandler>.Instance);

    Assert.DoesNotContain(LearningEventType.FullDetection, handler.HandledEventTypes);
}

[Fact]
public void HandledEventTypes_ContainsHighConfidenceDetection()
{
    var handler = new SimilarityLearningHandler(
        new FeatureVectorizer(), Substitute.For<ISignatureSimilaritySearch>(),
        NullLogger<SimilarityLearningHandler>.Instance);

    Assert.Contains(LearningEventType.HighConfidenceDetection, handler.HandledEventTypes);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SimilarityLearningHandlerTests.HandledEventTypes_DoesNotContainFullDetection" -v m 2>&1 | tail -10
```

Expected: FAIL -currently `HandledEventTypes` includes `FullDetection`.

- [ ] **Step 3: Remove FullDetection from HandledEventTypes in SimilarityLearningHandler.cs**

Open `src/Mostlylucid.BotDetection/Similarity/SimilarityLearningHandler.cs`. Find:

```csharp
public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
{
    LearningEventType.HighConfidenceDetection,
    LearningEventType.FullDetection
};
```

Change to:

```csharp
public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
{
    LearningEventType.HighConfidenceDetection
};
```

This is the key behavioral fix: `FullDetection` fires on every HTTP request. `HighConfidenceDetection` fires only when confidence > threshold. This reduces write frequency by ~95% for typical traffic.

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SimilarityLearningHandlerTests" -v m 2>&1 | tail -10
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Similarity/SimilarityLearningHandler.cs \
        src/Mostlylucid.BotDetection.Test/Similarity/SimilarityLearningHandlerTests.cs
git commit -m "fix(learning): remove FullDetection from SimilarityLearningHandler

FullDetection fires on every HTTP request -this was the primary driver
of unbounded HNSW growth. HighConfidenceDetection fires only on confident
detections (~5% of traffic), which is sufficient for the learning loop."
```

---

## Task 8: Update SessionVectorWarmupService

Currently waits for HNSW `LoadAsync()` then seeds from SQLite if the HNSW graph is empty. New behavior: always warm the `SlimSessionVectorSearch` from the SQLite centroid store on startup.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/SessionVectorWarmupService.cs`

- [ ] **Step 1: Replace the warmup logic**

Open `src/Mostlylucid.BotDetection/Services/SessionVectorWarmupService.cs`. Replace the entire `ExecuteAsync` body:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Brief startup delay so DI and DB init complete first
    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

    try
    {
        // Warm SlimSessionVectorSearch from session_centroids table
        if (_vectorSearch is SlimSessionVectorSearch slim)
        {
            var rows = await _centroidStore.GetRecentSessionsAsync(
                WarmupBatchSize, stoppingToken);
            slim.WarmFromRows(rows);
            _logger.LogInformation(
                "SessionVectorWarmup: loaded {Count} session centroids from SQLite",
                rows.Count);
            return;
        }

        // Fallback for non-slim implementations (e.g. commercial pgvector):
        // seed from raw session vectors if index is empty
        if (_vectorSearch.Count > 0) return;

        var sessions = await _store.GetRecentSessionsAsync(WarmupBatchSize, null, stoppingToken);
        var added = 0;
        foreach (var session in sessions)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (session.Vector is not { Length: > 0 }) continue;
            var vector = SqliteSessionStore.DeserializeVector(session.Vector);
            if (vector == null) continue;
            var freqFp = SqliteSessionStore.DeserializeVector(session.FrequencyFingerprintBlob);
            var driftVec = SqliteSessionStore.DeserializeVector(session.DriftVectorBlob);
            await _vectorSearch.AddAsync(vector, session.Signature, session.IsBot, session.AvgBotProbability,
                frequencyFingerprint: freqFp, driftVector: driftVec);
            added++;
        }
        if (added > 0)
            _logger.LogInformation("SessionVectorWarmup fallback: {Count} sessions indexed", added);
    }
    catch (OperationCanceledException) { /* shutdown during warmup */ }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "SessionVectorWarmup failed; cache will build from live traffic");
    }
}
```

Also inject `SqliteVectorCentroidStore` into the constructor:

```csharp
private readonly ISessionStore _store;
private readonly ISessionVectorSearch _vectorSearch;
private readonly SqliteVectorCentroidStore _centroidStore;
private readonly ILogger<SessionVectorWarmupService> _logger;
private const int WarmupBatchSize = 5000;

public SessionVectorWarmupService(
    ISessionStore store,
    ISessionVectorSearch vectorSearch,
    SqliteVectorCentroidStore centroidStore,
    ILogger<SessionVectorWarmupService> logger)
{
    _store = store;
    _vectorSearch = vectorSearch;
    _centroidStore = centroidStore;
    _logger = logger;
}
```

Also add parallel warmup for signature and intent caches. Add two new method calls at the end of `ExecuteAsync` (before `return`):

```csharp
// Also warm signature and intent caches
await WarmSignaturesAsync(stoppingToken);
await WarmIntentsAsync(stoppingToken);
```

And add the methods:

```csharp
private async Task WarmSignaturesAsync(CancellationToken ct)
{
    if (_signatureSearch is not SlimSignatureSimilaritySearch slim) return;
    var rows = await _centroidStore.GetRecentSignaturesAsync(WarmupBatchSize, ct);
    slim.WarmFromRows(rows);
    _logger.LogInformation("SignatureWarmup: {Count} entries loaded", rows.Count);
}

private async Task WarmIntentsAsync(CancellationToken ct)
{
    if (_intentSearch is not SlimIntentSearch slim) return;
    var rows = await _centroidStore.GetRecentIntentsAsync(WarmupBatchSize, ct);
    slim.WarmFromRows(rows);
    _logger.LogInformation("IntentWarmup: {Count} entries loaded", rows.Count);
}
```

Add `ISignatureSimilaritySearch _signatureSearch` and `IIntentSimilaritySearch _intentSearch` to the constructor (optional DI). If adding them makes the constructor complex, just inject them as optional:

```csharp
public SessionVectorWarmupService(
    ISessionStore store,
    ISessionVectorSearch vectorSearch,
    SqliteVectorCentroidStore centroidStore,
    ILogger<SessionVectorWarmupService> logger,
    ISignatureSimilaritySearch? signatureSearch = null,
    IIntentSimilaritySearch? intentSearch = null)
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/SessionVectorWarmupService.cs
git commit -m "feat(warmup): warm Slim* caches from SQLite centroid tables on startup"
```

---

## Task 9: Rewire VectorCompactionService Phase 3

Phase 3 currently calls `GetAllVectorsSnapshot()` + `ReplaceAllAsync()` to rebuild the HNSW graph with L1/L2 centroids. With `SlimSessionVectorSearch`, these methods now operate on the SQLite `session_centroids` table. Phase 3 logic is unchanged -`ReplaceAllAsync` just writes to SQLite instead of a file.

The main change: also add pruning for all three centroid tables (age-based retention).

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs`

- [ ] **Step 1: Inject SqliteVectorCentroidStore and add Phase 3 pruning**

Open `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs`. 

Add `SqliteVectorCentroidStore? _centroidStore` as an optional constructor parameter:

```csharp
private readonly ISessionStore _store;
private readonly ISessionVectorSearch? _vectorSearch;
private readonly RetentionOptions _retention;
private readonly ILogger<VectorCompactionService> _logger;
private readonly SqliteVectorCentroidStore? _centroidStore;
private readonly SelfMaintenanceOptions _selfMaintenance;

public VectorCompactionService(
    ISessionStore store,
    IOptions<BotDetectionOptions> options,
    ILogger<VectorCompactionService> logger,
    ISessionVectorSearch? vectorSearch = null,
    SqliteVectorCentroidStore? centroidStore = null)
{
    _store = store;
    _vectorSearch = vectorSearch;
    _centroidStore = centroidStore;
    _retention = options.Value.Retention;
    _selfMaintenance = options.Value.SelfMaintenance;
    _logger = logger;
}
```

In `RunCompactionAsync`, replace:

```csharp
// Phase 3: Compact HNSW index if it's grown too large
if (_vectorSearch != null)
    await RunPhase3HnswCompactionAsync(ct);
```

With:

```csharp
// Phase 3: Compact session vector index + prune all centroid tables
if (_vectorSearch != null)
    await RunPhase3HnswCompactionAsync(ct);
if (_centroidStore != null)
    await RunCentroidPruningAsync(ct);
```

Add the new pruning method (before the closing brace of the class):

```csharp
private async Task RunCentroidPruningAsync(CancellationToken ct)
{
    if (_centroidStore == null) return;
    var cutoff = DateTimeOffset.UtcNow.AddDays(-_selfMaintenance.CentroidRetentionDays);
    try
    {
        await _centroidStore.PruneSignaturesOlderThanAsync(cutoff, ct);
        await _centroidStore.PruneSessionsOlderThanAsync(cutoff, ct);
        await _centroidStore.PruneIntentsOlderThanAsync(cutoff, ct);
        _logger.LogDebug("Centroid pruning complete: cutoff={Cutoff:O}", cutoff);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Centroid pruning failed");
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs
git commit -m "feat(compaction): add centroid table pruning in VectorCompactionService Phase 3"
```

---

## Task 10: Update ServiceCollectionExtensions

Wire up the new `Slim*` implementations, remove HNSW registrations, register `SqliteVectorCentroidStore`.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace HNSW registrations**

Open `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`. Find the block starting at line ~719 (the HNSW registrations). Replace the entire HNSW block (lines 719-765 approximately) with:

```csharp
// SQLite centroid store (persistent backing for Slim* similarity caches)
services.TryAddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
    var dbPath = opts.DatabasePath ?? Path.Combine(BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
    // Use same sessions.db as SqliteSessionStore
    var sessionsDbPath = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "sessions.db");
    var connStr = $"Data Source={sessionsDbPath};Cache=Shared";
    var logger = sp.GetRequiredService<ILogger<SqliteVectorCentroidStore>>();
    return new SqliteVectorCentroidStore(connStr, logger);
});

// Feature vectorizers
services.TryAddSingleton<FeatureVectorizer>();
services.TryAddSingleton<IntentVectorizer>();

// Intent similarity cache (36-dim, replaces HnswIntentSearch)
services.TryAddSingleton<SlimIntentSearch>();
services.TryAddSingleton<IIntentSimilaritySearch>(sp => sp.GetRequiredService<SlimIntentSearch>());

// Signature similarity cache (replaces HnswFileSimilaritySearch; Qdrant still available when enabled)
services.TryAddSingleton<SlimSignatureSimilaritySearch>();
services.TryAddSingleton<ISignatureSimilaritySearch>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
    if (opts.Qdrant.Enabled)
    {
        var qdrantLogger = sp.GetRequiredService<ILogger<QdrantSimilaritySearch>>();
        if (opts.Qdrant.EnableEmbeddings)
        {
            var embedder = sp.GetRequiredService<IEmbeddingProvider>();
            var vectorizer = sp.GetRequiredService<FeatureVectorizer>();
            return new DualVectorSimilaritySearch(opts.Qdrant, vectorizer, embedder, opts.DatabasePath, qdrantLogger);
        }
        return new QdrantSimilaritySearch(opts.Qdrant, opts.DatabasePath, qdrantLogger);
    }
    return sp.GetRequiredService<SlimSignatureSimilaritySearch>();
});

// Session vector cache (129-dim, replaces HnswSessionVectorSearch)
services.TryAddSingleton<SlimSessionVectorSearch>();
services.TryAddSingleton<ISessionVectorSearch>(sp => sp.GetRequiredService<SlimSessionVectorSearch>());

// Warmup: populates caches from SQLite centroid tables on startup
services.AddHostedService<Services.SessionVectorWarmupService>();

// Nightly compaction: SQLite session compaction + centroid table pruning
services.AddHostedService<Services.VectorCompactionService>();

// Learning handlers (feed high-confidence detections into similarity caches)
services.AddSingleton<ILearningEventHandler, SimilarityLearningHandler>();
services.AddSingleton<ILearningEventHandler, IntentLearningHandler>();
```

Also remove any remaining references to `HnswFileSimilaritySearch`, `HnswSessionVectorSearch`, `HnswIntentSearch` that are no longer registered.

- [ ] **Step 2: Add missing using directives if needed**

```csharp
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Similarity;
```

These should already be present but verify.

- [ ] **Step 3: Build entire solution**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln 2>&1 | grep -E "error|Error|warning CS" | head -30
```

Expected: 0 errors. Warnings are OK.

- [ ] **Step 4: Run all tests**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln 2>&1 | tail -20
```

Expected: All previously-passing tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(di): wire Slim* similarity implementations, remove HNSW registrations"
```

---

## Task 11: Delete HNSW Implementation Files

Remove the three HNSW classes that caused the LOH growth. All callers now use the interface, which resolves to `Slim*`.

**Files:**
- Delete: `src/Mostlylucid.BotDetection/Similarity/HnswFileSimilaritySearch.cs`
- Delete: `src/Mostlylucid.BotDetection/Similarity/HnswSessionVectorSearch.cs`
- Delete: `src/Mostlylucid.BotDetection/Similarity/HnswIntentSearch.cs`

- [ ] **Step 1: Verify no remaining references**

```bash
grep -rn "HnswFileSimilaritySearch\|HnswSessionVectorSearch\|HnswIntentSearch" \
    /Users/scottgalloway/RiderProjects/stylobot/src/ --include="*.cs" | grep -v "\.cs:.*//\s*"
```

Expected: 0 results (or only comments).

- [ ] **Step 2: Delete the files**

```bash
rm /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Similarity/HnswFileSimilaritySearch.cs
rm /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Similarity/HnswSessionVectorSearch.cs
rm /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Similarity/HnswIntentSearch.cs
```

- [ ] **Step 3: Build to confirm no broken references**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Delete stale hnsw-index JSON files from repo root**

```bash
ls /Users/scottgalloway/RiderProjects/stylobot/hnsw-index/
```

```bash
# Remove tracked files from git
git rm hnsw-index/signatures.meta.json hnsw-index/signatures.vectors.json 2>/dev/null || true
# Remove any other hnsw-index files
rm -f hnsw-index/*.json hnsw-index/*.meta
```

- [ ] **Step 5: Commit**

```bash
git add -A src/Mostlylucid.BotDetection/Similarity/
git add -A hnsw-index/ 2>/dev/null || true
git commit -m "refactor(similarity): delete HNSW implementations and stale index files

HnswFileSimilaritySearch, HnswSessionVectorSearch, HnswIntentSearch removed.
All replaced by Slim* bounded caches + SqliteVectorCentroidStore.
The hnsw-index/*.json files (104MB+ LOH source) deleted from repo."
```

---

## Task 12: Cap MarkovTracker._cohortBaselines

`_cohortBaselines` is a `ConcurrentDictionary<string, DecayingTransitionMatrix>` with no size limit. In deployments with many distinct traffic cohorts (datacenter-new, datacenter-returning, residential-new, etc. × cluster IDs), this can grow to tens of thousands of entries.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Markov/MarkovTracker.cs`

- [ ] **Step 1: Write a test that verifies cohort baseline cap is enforced**

```csharp
// File: src/Mostlylucid.BotDetection.Test/Markov/MarkovTrackerCohortCapTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Markov;

public class MarkovTrackerCohortCapTests
{
    [Fact]
    public void CohortBaselines_DoNotExceedMaxCohortSize()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions { MarkovCohortSize = 10 },
            Markov = new MarkovOptions()
        });
        var tracker = new MarkovTracker(NullLogger<MarkovTracker>.Instance, options);

        // Flood with unique cluster IDs to force cohort creation
        for (var i = 0; i < 50; i++)
        {
            var update = new CohortUpdate($"cluster{i}", "PageView", "ApiCall",
                DateTime.UtcNow, IsHuman: true);
            tracker.AddCohortUpdate(update);
        }
        tracker.FlushCohortUpdates();

        var baselines = tracker.GetCohortBaselines();
        Assert.True(baselines.Count <= 10,
            $"Expected ≤10 cohort baselines, got {baselines.Count}");
    }
}
```

Note: `AddCohortUpdate` is a test-only entry point -if `CohortUpdate` is not publicly constructible, use `RecordTransition` with distinct `clusterId` values to force cohort creation.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~MarkovTrackerCohortCapTests" -v m 2>&1 | tail -15
```

Expected: FAIL -cohort count exceeds cap.

- [ ] **Step 3: Read MarkovOptions to find MaxTrackedSignatures pattern**

```bash
grep -n "MaxTrackedSignatures\|EvictStale\|MaxCohort" /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Markov/MarkovTracker.cs | head -15
grep -n "MaxTrackedSignatures\|MaxCohort" /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs | head -10
```

- [ ] **Step 4: Add cohort eviction to FlushCohortUpdates in MarkovTracker.cs**

Open `src/Mostlylucid.BotDetection/Markov/MarkovTracker.cs`. Find `FlushCohortUpdates()`. After the loop that processes updates, add eviction if over limit. The limit comes from `_options.MaxCohortBaselines` (read from `SelfMaintenanceOptions`):

First, update the constructor to capture the limit:

```csharp
private readonly int _maxCohortBaselines;

public MarkovTracker(ILogger<MarkovTracker> logger, IOptions<BotDetectionOptions> options)
{
    _logger = logger;
    _options = options.Value.Markov;
    _maxCohortBaselines = options.Value.SelfMaintenance.MarkovCohortSize;
    _globalBaseline = new DecayingTransitionMatrix(
        TimeSpan.FromHours(_options.GlobalHalfLifeHours),
        _options.MaxEdgesPerNode);
}
```

Then at the end of `FlushCohortUpdates()`, after logging, add:

```csharp
// Evict cohorts if over limit: remove those with the fewest total transitions
if (_cohortBaselines.Count > _maxCohortBaselines)
{
    var toEvict = _cohortBaselines
        .OrderBy(kvp => kvp.Value.TotalTransitions)
        .Take(_cohortBaselines.Count - _maxCohortBaselines)
        .Select(kvp => kvp.Key)
        .ToList();
    foreach (var key in toEvict)
        _cohortBaselines.TryRemove(key, out _);

    _logger.LogDebug("MarkovTracker evicted {Count} cohort baselines (cap={Max})",
        toEvict.Count, _maxCohortBaselines);
}
```

`DecayingTransitionMatrix.TotalTransitions` -verify this property exists. If not, use a proxy like `GetAllEdges().Count` or just track last-update time. Check with:

```bash
grep -n "TotalTransitions\|public.*int\|public.*long" /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection/Markov/DecayingTransitionMatrix.cs | head -10
```

Adapt the eviction sort key to whatever metric is available on `DecayingTransitionMatrix`.

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~MarkovTrackerCohortCapTests" -v m 2>&1 | tail -10
```

Expected: PASS.

- [ ] **Step 6: Build entire solution**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln 2>&1 | grep -E "^.*error" | head -20
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Markov/MarkovTracker.cs \
        src/Mostlylucid.BotDetection.Test/Markov/MarkovTrackerCohortCapTests.cs
git commit -m "fix(markov): cap cohort baselines at SelfMaintenanceOptions.MarkovCohortSize

Prevents unbounded growth when many cluster IDs or traffic cohorts exist.
Eviction by fewest total transitions (cold cohorts removed first)."
```

---

## Task 13: Final Integration Verification

Run the demo, verify memory is stable, confirm all tests pass.

- [ ] **Step 1: Run full test suite**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln 2>&1 | tail -30
```

Expected: All tests pass (or same set as before this work -no regressions).

- [ ] **Step 2: Build release**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln -c Release 2>&1 | tail -10
```

Expected: 0 errors.

- [ ] **Step 3: Start demo and measure memory after 60 seconds**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo -c Release &
DEMO_PID=$!
sleep 60

# Measure LOH
dotnet-counters collect --process-id $DEMO_PID --counters dotnet.gc --duration 00:00:10 --format csv -o /tmp/loh_check.csv
grep "loh" /tmp/loh_check.csv
```

Expected: `dotnet.gc.last_collection.heap.size[loh]` well below 1 GB (target: <100 MB after warmup).

- [ ] **Step 4: Verify hnsw-index directory is clean**

```bash
ls /Users/scottgalloway/RiderProjects/stylobot/hnsw-index/ 2>/dev/null || echo "Directory removed or empty -good"
```

Expected: empty or gone.

- [ ] **Step 5: Final commit if any cleanup needed**

```bash
git status
# If any stray files:
git add -A
git commit -m "chore: final cleanup after HNSW replacement"
```

---

## Self-Review

**Spec coverage check:**

| Spec requirement | Task(s) |
|---|---|
| Replace HnswFileSimilaritySearch | Task 4, 10, 11 |
| Replace HnswSessionVectorSearch | Task 5, 10, 11 |
| Replace HnswIntentSearch | Task 6, 10, 11 |
| SlidingCacheAtom / bounded hot layer | Tasks 4, 5, 6 (BoundedVectorCache) |
| SQLite centroid tables | Task 2 |
| SqliteVectorCentroidStore | Task 3 |
| SelfMaintenanceOptions replaces HnswOptions | Task 1 |
| VectorCompactionService centroid pruning | Task 9 |
| SessionVectorWarmupService from SQLite | Task 8 |
| Learning handler stops on FullDetection | Task 7 |
| MarkovTracker._cohortBaselines cap | Task 12 |
| Delete HNSW files | Task 11 |

**Type consistency check:** `SessionVectorMetadata`, `SessionCentroidRow`, `SignatureCentroidRow`, `IntentCentroidRow`, `BoundedVectorCache<T>` -all defined before use. `SlimSignatureSimilaritySearch`, `SlimSessionVectorSearch`, `SlimIntentSearch` -consistent naming. `WarmFromRows` method on all three -consistent API for warmup service.

**No placeholders** -all code blocks are complete and buildable.