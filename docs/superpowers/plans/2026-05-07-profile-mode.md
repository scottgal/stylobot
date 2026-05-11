# Profile Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add profile mode to the StyloBot Gateway: fingerprint-only inline detection (near-zero overhead), full analysis queued to a background worker, calibration results in SQLite, threshold simulator exposed at `/admin/calibration`.

**Architecture:** A new `profile` DetectionPolicy runs only `SignatureContributor` inline. A `ProfileCaptureMiddleware` serializes each request into a `ProfileRequestSnapshot` and pushes it to a bounded `ProfileAnalysisChannel`. A hosted `ProfileAnalysisWorker` drains the channel, runs full bot detection on each snapshot (following the `LearningCoordinator` pattern), and writes results to `ProfileCalibrationStore` (SQLite). The `/admin/calibration` admin endpoint returns a score distribution histogram, per-threshold simulation table, and a gap-analysis threshold recommendation.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` (already a transitive dependency), `System.Threading.Channels`, xUnit, `Mostlylucid.BotDetection` (existing policies, middleware item keys, detection services).

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs` | Modify | Add `Profile` static property |
| `src/Stylobot.Gateway/Configuration/ProfileModeOptions.cs` | Create | Config binding from env vars |
| `src/Stylobot.Gateway/Services/ProfileRequestSnapshot.cs` | Create | Serializable request record |
| `src/Stylobot.Gateway/Services/ProfileAnalysisChannel.cs` | Create | Bounded channel with metrics |
| `src/Stylobot.Gateway/Data/ProfileCalibrationStore.cs` | Create | SQLite store + recommendation engine |
| `src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs` | Create | Hosted service: drain channel, run detection, store results |
| `src/Stylobot.Gateway/Middleware/ProfileCaptureMiddleware.cs` | Create | Capture request after bot detection runs, enqueue snapshot |
| `src/Stylobot.Gateway/Endpoints/CalibrationEndpoint.cs` | Create | GET /admin/calibration, POST /admin/calibration/reset |
| `src/Stylobot.Gateway/Configuration/ServiceCollectionExtensions.cs` | Modify | Register profile services when enabled |
| `src/Stylobot.Gateway/Configuration/StartupBanner.cs` | Modify | Show profile mode row in banner |
| `src/Stylobot.Gateway/Program.cs` | Modify | `ConfigureProfileMode`, capture middleware, endpoint wiring |
| `src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs` | Create | xUnit tests for channel, store, and endpoint |

---

## Context for Implementers

**`DetectionPolicy` is a `sealed record`** in `Mostlylucid.BotDetection.Policies`. Its detector lists use `ImmutableList<string>`. The detector name `"Signature"` maps to `SignatureContributor` (Priority 1, Wave 0, no trigger conditions).

**Named policy lookup:** Search `BotDetectionOptions.cs` for `DetectionPolicy.Demo` to find where built-in named policies are registered. Add the `"profile"` entry in the same location.

**HttpContext item keys** set by `BotDetectionMiddleware` after detection (from the middleware summary doc):
- `BotDetectionMiddleware.BotConfidenceKey` -double confidence (0.0–1.0)
- `BotDetectionMiddleware.IsBotKey` -bool
- `BotDetectionMiddleware.BotTypeKey` -BotType? enum
- `BotDetectionMiddleware.BotNameKey` -string?
- `BotDetectionMiddleware.PolicyNameKey` -string

**Background detection pattern:** Read `src/Mostlylucid.BotDetection/Learning/LearningCoordinator.cs` to understand how the learning coordinator runs full detection in background on stored request data. The `ProfileAnalysisWorker` follows the same pattern with a `DefaultHttpContext` constructed from the snapshot.

**`GatewayPaths.Data`** resolves to `/app/data` (or `GATEWAY_DATA_PATH` env var). Use it for the calibration SQLite path.

---

## Task 1: `profile` Detection Policy + ProfileModeOptions

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`
- Create: `src/Stylobot.Gateway/Configuration/ProfileModeOptions.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs`

- [ ] **Step 1: Add project reference for tests**

In `src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj`, add:
```xml
<ProjectReference Include="..\..\Stylobot.Gateway\Stylobot.Gateway.csproj" />
```

- [ ] **Step 2: Write failing tests**

Create `src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Stylobot.Gateway.Configuration;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Gateway;

public class ProfileModeTests
{
    [Fact]
    public void ProfilePolicy_NeverBlocks()
    {
        var policy = DetectionPolicy.Profile;
        Assert.True(policy.ImmediateBlockThreshold > 1.0);
    }

    [Fact]
    public void ProfilePolicy_OnlyRunsSignatureDetector()
    {
        var policy = DetectionPolicy.Profile;
        Assert.Contains("Signature", policy.FastPathDetectors);
        Assert.Empty(policy.SlowPathDetectors);
        Assert.Empty(policy.AiPathDetectors);
        Assert.False(policy.EscalateToAi);
    }

    [Fact]
    public void ProfilePolicy_HasCorrectName()
    {
        Assert.Equal("profile", DetectionPolicy.Profile.Name);
    }

    [Fact]
    public void ProfileModeOptions_DefaultCapacityIs5000()
    {
        var opts = new ProfileModeOptions();
        Assert.Equal(5000, opts.ChannelCapacity);
        Assert.Equal(2, opts.Concurrency);
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void ProfileModeOptions_DatabasePath_DefaultsToNull()
    {
        var opts = new ProfileModeOptions();
        Assert.Null(opts.DatabasePath);
    }
}
```

- [ ] **Step 3: Run tests -expect FAIL**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileModeTests" -v
```

Expected: FAIL -`DetectionPolicy.Profile` does not exist yet.

- [ ] **Step 4: Add `Profile` static property to `DetectionPolicy.cs`**

Open `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`. After the `Monitor` static property, add:

```csharp
/// <summary>
///     Profile mode: fingerprint-only inline detection for calibration.
///     Never blocks. Runs only SignatureContributor inline; full analysis
///     is deferred to the gateway's ProfileAnalysisWorker background service.
/// </summary>
public static DetectionPolicy Profile => new()
{
    Name = "profile",
    Description = "Fingerprint-only detection for calibration -never blocks inline",
    FastPathDetectors = ImmutableList.Create("Signature"),
    SlowPathDetectors = ImmutableList<string>.Empty,
    AiPathDetectors = ImmutableList<string>.Empty,
    ResponsePathDetectors = ImmutableList<string>.Empty,
    UseFastPath = true,
    ForceSlowPath = false,
    EscalateToAi = false,
    EarlyExitThreshold = 1.0,
    ImmediateBlockThreshold = 1.1,
    BypassTriggerConditions = false,
    ExcludedDetectors = ImmutableHashSet<string>.Empty
        .WithComparer(StringComparer.OrdinalIgnoreCase),
};
```

Then search `BotDetectionOptions.cs` for `DetectionPolicy.Demo` and add `["profile"] = DetectionPolicy.Profile` in the same location.

- [ ] **Step 5: Create `ProfileModeOptions.cs`**

Create `src/Stylobot.Gateway/Configuration/ProfileModeOptions.cs`:

```csharp
namespace Stylobot.Gateway.Configuration;

public class ProfileModeOptions
{
    public const string SectionName = "Gateway:ProfileMode";

    /// <summary>Enable profile mode. Set GATEWAY_PROFILE_MODE=true to activate.</summary>
    public bool Enabled { get; set; }

    /// <summary>Max requests queued for background analysis. Oldest dropped when full.</summary>
    public int ChannelCapacity { get; set; } = 5000;

    /// <summary>Background analysis worker concurrency.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>
    ///     SQLite path for calibration data.
    ///     Defaults to GatewayPaths.Data + "/profile_calibration.db".
    /// </summary>
    public string? DatabasePath { get; set; }
}
```

- [ ] **Step 6: Run tests -expect PASS**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileModeTests" -v
```

Expected: all 5 PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs \
        src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs \
        src/Stylobot.Gateway/Configuration/ProfileModeOptions.cs \
        src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj \
        src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs
git commit -m "feat(profile): add profile detection policy and ProfileModeOptions"
```

---

## Task 2: ProfileRequestSnapshot + ProfileAnalysisChannel

**Files:**
- Create: `src/Stylobot.Gateway/Services/ProfileRequestSnapshot.cs`
- Create: `src/Stylobot.Gateway/Services/ProfileAnalysisChannel.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs` (append tests)

- [ ] **Step 1: Write failing tests**

Append to `ProfileModeTests.cs`:

```csharp
public class ProfileAnalysisChannelTests
{
    [Fact]
    public void Channel_EnqueueAndDequeue_SingleItem()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 10 });
        var snapshot = MakeSnapshot("req-1");

        var enqueued = channel.TryEnqueue(snapshot);

        Assert.True(enqueued);
        Assert.Equal(1, channel.QueueDepth);
        Assert.Equal(1, channel.TotalEnqueued);
    }

    [Fact]
    public async Task Channel_ReadAllAsync_ReturnsEnqueuedItem()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 10 });
        var snapshot = MakeSnapshot("req-1");
        channel.TryEnqueue(snapshot);
        channel.Complete();

        var items = new List<ProfileRequestSnapshot>();
        await foreach (var item in channel.ReadAllAsync(CancellationToken.None))
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("req-1", items[0].RequestId);
    }

    [Fact]
    public void Channel_DropOldest_WhenFull()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 2 });
        channel.TryEnqueue(MakeSnapshot("req-1"));
        channel.TryEnqueue(MakeSnapshot("req-2"));
        channel.TryEnqueue(MakeSnapshot("req-3")); // drops req-1

        Assert.Equal(2, channel.QueueDepth);
        Assert.Equal(3, channel.TotalEnqueued);
        Assert.Equal(1, channel.TotalDropped);
    }

    [Fact]
    public void Snapshot_FromHttpContext_CapturesRequiredFields()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/products";
        ctx.Request.Headers["User-Agent"] = "TestBrowser/1.0";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.2.3.4");

        var snapshot = ProfileRequestSnapshot.From(ctx);

        Assert.Equal("GET", snapshot.Method);
        Assert.Equal("/api/products", snapshot.Path);
        Assert.Equal("1.2.3.4", snapshot.ClientIp);
        Assert.NotNull(snapshot.RequestId);
    }

    private static ProfileRequestSnapshot MakeSnapshot(string id) => new()
    {
        RequestId = id,
        ClientIp = "1.2.3.4",
        UserAgent = "TestAgent/1.0",
        Method = "GET",
        Path = "/test",
        Headers = new Dictionary<string, string[]>(),
        CapturedAt = DateTime.UtcNow,
    };
}
```

- [ ] **Step 2: Run tests -expect FAIL**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileAnalysisChannelTests" -v
```

Expected: FAIL -types do not exist yet.

- [ ] **Step 3: Create `ProfileRequestSnapshot.cs`**

Create `src/Stylobot.Gateway/Services/ProfileRequestSnapshot.cs`:

```csharp
namespace Stylobot.Gateway.Services;

public record ProfileRequestSnapshot
{
    public required string RequestId { get; init; }
    public required string ClientIp { get; init; }
    public required string UserAgent { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public string? TlsProtocol { get; init; }
    public string? TlsCipherSuite { get; init; }
    public required DateTime CapturedAt { get; init; }

    public static ProfileRequestSnapshot From(HttpContext ctx)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in ctx.Request.Headers)
            headers[key] = values.ToArray()!;

        return new ProfileRequestSnapshot
        {
            RequestId = ctx.TraceIdentifier,
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = ctx.Request.Headers.UserAgent.ToString(),
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? "/",
            Headers = headers,
            TlsProtocol = ctx.Items.TryGetValue("TLS.Protocol", out var p) ? p?.ToString() : null,
            TlsCipherSuite = ctx.Items.TryGetValue("TLS.CipherSuite", out var c) ? c?.ToString() : null,
            CapturedAt = DateTime.UtcNow,
        };
    }
}
```

- [ ] **Step 4: Create `ProfileAnalysisChannel.cs`**

Create `src/Stylobot.Gateway/Services/ProfileAnalysisChannel.cs`:

```csharp
using System.Threading.Channels;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Services;

public sealed class ProfileAnalysisChannel
{
    private readonly Channel<ProfileRequestSnapshot> _channel;
    private long _totalEnqueued;
    private long _totalDropped;

    public ProfileAnalysisChannel(ProfileModeOptions options)
    {
        _channel = Channel.CreateBounded<ProfileRequestSnapshot>(
            new BoundedChannelOptions(options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public int QueueDepth => _channel.Reader.Count;
    public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    public bool TryEnqueue(ProfileRequestSnapshot snapshot)
    {
        if (!_channel.Writer.TryWrite(snapshot))
        {
            Interlocked.Increment(ref _totalDropped);
            Interlocked.Increment(ref _totalEnqueued);
            return false;
        }
        Interlocked.Increment(ref _totalEnqueued);
        return true;
    }

    public IAsyncEnumerable<ProfileRequestSnapshot> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.Complete();
}
```

- [ ] **Step 5: Run tests -expect PASS**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileAnalysisChannelTests" -v
```

Expected: all 4 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Stylobot.Gateway/Services/ProfileRequestSnapshot.cs \
        src/Stylobot.Gateway/Services/ProfileAnalysisChannel.cs \
        src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs
git commit -m "feat(profile): ProfileRequestSnapshot and ProfileAnalysisChannel"
```

---

## Task 3: ProfileCalibrationStore

**Files:**
- Create: `src/Stylobot.Gateway/Data/ProfileCalibrationStore.cs`
- Test: append to `ProfileModeTests.cs`

The store uses `Microsoft.Data.Sqlite` directly (same pattern as `SqliteSessionStore` in the bot detection library). The calibration DB lives at `GatewayPaths.Data + "/profile_calibration.db"` by default, overridable via `ProfileModeOptions.DatabasePath`.

- [ ] **Step 1: Write failing tests**

Append to `ProfileModeTests.cs`:

```csharp
public class ProfileCalibrationStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ProfileCalibrationStore _store;

    public ProfileCalibrationStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"profile_test_{Guid.NewGuid():N}.db");
        _store = new ProfileCalibrationStore(_dbPath);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Insert_ThenDistribution_CountsCorrectly()
    {
        await _store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = "abc",
            BotProbability = 0.2,
            RiskBand = "Low",
            BotType = null,
            BotName = null,
            TopDetector = null,
            PathPattern = "/home",
        }, CancellationToken.None);

        var dist = await _store.GetScoreDistributionAsync(CancellationToken.None);
        Assert.True(dist.TotalAnalyzed >= 1);
        Assert.True(dist.Buckets.ContainsKey("0.2") || dist.Buckets.Any(b => b.Value > 0));
    }

    [Fact]
    public async Task ThresholdSimulation_IncludesCommonThresholds()
    {
        for (int i = 0; i < 5; i++)
            await _store.InsertAsync(new ProfileCalibrationEntry
            {
                SignatureHash = $"sig{i}", BotProbability = 0.8 + i * 0.01,
                RiskBand = "High", BotType = "Scraper", BotName = null,
                TopDetector = "UserAgent", PathPattern = "/catalog",
            }, CancellationToken.None);

        var sim = await _store.GetThresholdSimulationAsync(CancellationToken.None);
        Assert.NotEmpty(sim);
        Assert.All(sim, row =>
        {
            Assert.True(row.Threshold is >= 0.0 and <= 1.0);
            Assert.True(row.WouldBlock >= 0);
        });
    }

    [Fact]
    public async Task Reset_ClearsAllEntries()
    {
        await _store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = "x", BotProbability = 0.5, RiskBand = "Medium",
            BotType = null, BotName = null, TopDetector = null, PathPattern = "/",
        }, CancellationToken.None);

        await _store.ResetAsync(CancellationToken.None);
        var dist = await _store.GetScoreDistributionAsync(CancellationToken.None);
        Assert.Equal(0, dist.TotalAnalyzed);
    }

    [Fact]
    public async Task RecommendedThreshold_NullWhenNoData()
    {
        var rec = await _store.GetRecommendedThresholdAsync(CancellationToken.None);
        Assert.Null(rec);
    }
}
```

- [ ] **Step 2: Run tests -expect FAIL**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileCalibrationStoreTests" -v
```

Expected: FAIL -type does not exist.

- [ ] **Step 3: Create `ProfileCalibrationStore.cs`**

Create `src/Stylobot.Gateway/Data/ProfileCalibrationStore.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Stylobot.Gateway.Data;

public record ProfileCalibrationEntry
{
    public required string SignatureHash { get; init; }
    public required double BotProbability { get; init; }
    public required string RiskBand { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public string? TopDetector { get; init; }
    public required string PathPattern { get; init; }
}

public record ScoreDistributionResult
{
    public long TotalAnalyzed { get; init; }
    public double CollectionPeriodHours { get; init; }
    public Dictionary<string, long> Buckets { get; init; } = new();
}

public record ThresholdSimRow
{
    public double Threshold { get; init; }
    public long WouldBlock { get; init; }
    public double PercentOfTraffic { get; init; }
    public List<string> TopBotTypes { get; init; } = new();
}

public class ProfileCalibrationStore(string dbPath)
{
    private SqliteConnection CreateConnection() =>
        new($"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate");

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS profile_calibration (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                signature_hash TEXT    NOT NULL,
                bot_probability REAL   NOT NULL,
                risk_band      TEXT    NOT NULL,
                bot_type       TEXT,
                bot_name       TEXT,
                top_detector   TEXT,
                path_pattern   TEXT    NOT NULL,
                analyzed_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_pc_probability ON profile_calibration(bot_probability);
            CREATE INDEX IF NOT EXISTS idx_pc_analyzed_at ON profile_calibration(analyzed_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAsync(ProfileCalibrationEntry entry, CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profile_calibration
                (signature_hash, bot_probability, risk_band, bot_type, bot_name, top_detector, path_pattern)
            VALUES
                ($sig, $prob, $band, $type, $name, $det, $path)
            """;
        cmd.Parameters.AddWithValue("$sig", entry.SignatureHash);
        cmd.Parameters.AddWithValue("$prob", entry.BotProbability);
        cmd.Parameters.AddWithValue("$band", entry.RiskBand);
        cmd.Parameters.AddWithValue("$type", entry.BotType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$name", entry.BotName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$det", entry.TopDetector ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$path", entry.PathPattern);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScoreDistributionResult> GetScoreDistributionAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // Total count and collection window
        await using var statsCmd = conn.CreateCommand();
        statsCmd.CommandText = """
            SELECT COUNT(*),
                   CAST((julianday('now') - julianday(MIN(analyzed_at))) * 24 AS REAL)
            FROM profile_calibration
            """;
        long total = 0;
        double hours = 0;
        await using (var r = await statsCmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                total = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                hours = r.IsDBNull(1) ? 0 : r.GetDouble(1);
            }
        }

        // Score distribution in 0.1 buckets
        await using var distCmd = conn.CreateCommand();
        distCmd.CommandText = """
            SELECT ROUND(bot_probability, 1) AS bucket, COUNT(*) AS cnt
            FROM profile_calibration
            GROUP BY bucket
            ORDER BY bucket
            """;
        var buckets = new Dictionary<string, long>();
        await using (var r = await distCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                buckets[$"{r.GetDouble(0):F1}"] = r.GetInt64(1);
        }

        return new ScoreDistributionResult
        {
            TotalAnalyzed = total,
            CollectionPeriodHours = Math.Round(hours, 1),
            Buckets = buckets,
        };
    }

    public async Task<List<ThresholdSimRow>> GetThresholdSimulationAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var totalCmd = conn.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM profile_calibration";
        var total = (long)(await totalCmd.ExecuteScalarAsync(ct) ?? 0L);
        if (total == 0) return [];

        var thresholds = new[] { 0.50, 0.60, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95 };
        var rows = new List<ThresholdSimRow>();

        foreach (var threshold in thresholds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) AS blocked,
                       COALESCE(GROUP_CONCAT(DISTINCT bot_type), '') AS types
                FROM profile_calibration
                WHERE bot_probability >= $threshold AND bot_type IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$threshold", threshold);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;

            var blocked = r.GetInt64(0);
            var typesRaw = r.GetString(1);
            var topTypes = typesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .Take(3)
                .ToList();

            rows.Add(new ThresholdSimRow
            {
                Threshold = threshold,
                WouldBlock = blocked,
                PercentOfTraffic = total > 0 ? Math.Round(blocked * 100.0 / total, 1) : 0,
                TopBotTypes = topTypes,
            });
        }

        return rows;
    }

    public async Task<(double Threshold, string Reason)?> GetRecommendedThresholdAsync(CancellationToken ct)
    {
        var dist = await GetScoreDistributionAsync(ct);
        if (dist.TotalAnalyzed < 100) return null;

        // Find the largest gap (valley) in score distribution between 0.3 and 0.9
        var candidates = new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };
        var counts = candidates
            .Select(b => (bucket: b, count: dist.Buckets.GetValueOrDefault($"{b:F1}", 0)))
            .ToList();

        var minBucket = counts.OrderBy(x => x.count).First();
        if (minBucket.count > dist.TotalAnalyzed * 0.05)
            return null; // no clear valley -traffic not clearly bimodal

        var reason = $"Score valley at {minBucket.bucket:F1} separates human and bot clusters.";
        return (minBucket.bucket, reason);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profile_calibration";
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
```

- [ ] **Step 4: Run tests -expect PASS**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileCalibrationStoreTests" -v
```

Expected: all 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/Data/ProfileCalibrationStore.cs \
        src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs
git commit -m "feat(profile): ProfileCalibrationStore with SQLite and threshold recommendation"
```

---

## Task 4: ProfileCaptureMiddleware + ProfileAnalysisWorker

**Files:**
- Create: `src/Stylobot.Gateway/Middleware/ProfileCaptureMiddleware.cs`
- Create: `src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs`
- Test: append to `ProfileModeTests.cs`

**Key reference:** Before implementing the worker, read `src/Mostlylucid.BotDetection/Learning/LearningCoordinator.cs` to understand how the learning coordinator constructs a detection context from stored request data and invokes the detection pipeline. The `ProfileAnalysisWorker` follows the same approach with a `DefaultHttpContext` built from the snapshot.

- [ ] **Step 1: Write failing tests**

Append to `ProfileModeTests.cs`:

```csharp
public class ProfileCaptureMiddlewareTests
{
    [Fact]
    public async Task Middleware_Enqueues_WhenProfileModeEnabled()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(
            new ProfileModeOptions { Enabled = true, ChannelCapacity = 10 });
        var channel = new ProfileAnalysisChannel(opts.Value);
        var middleware = new ProfileCaptureMiddleware(
            _ => Task.CompletedTask, opts, channel);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/test";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(1, channel.QueueDepth);
    }

    [Fact]
    public async Task Middleware_DoesNotEnqueue_WhenProfileModeDisabled()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(
            new ProfileModeOptions { Enabled = false, ChannelCapacity = 10 });
        var channel = new ProfileAnalysisChannel(opts.Value);
        var middleware = new ProfileCaptureMiddleware(
            _ => Task.CompletedTask, opts, channel);

        var ctx = new DefaultHttpContext();
        await middleware.InvokeAsync(ctx);

        Assert.Equal(0, channel.QueueDepth);
    }
}
```

- [ ] **Step 2: Run tests -expect FAIL**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileCaptureMiddlewareTests" -v
```

Expected: FAIL.

- [ ] **Step 3: Create `ProfileCaptureMiddleware.cs`**

This middleware runs AFTER `UseBotDetection()` in the pipeline, so bot detection has already run and its results are in `HttpContext.Items` when this middleware captures the snapshot.

Create `src/Stylobot.Gateway/Middleware/ProfileCaptureMiddleware.cs`:

```csharp
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Middleware;

public class ProfileCaptureMiddleware(
    RequestDelegate next,
    IOptions<ProfileModeOptions> options,
    ProfileAnalysisChannel channel)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (options.Value.Enabled)
            channel.TryEnqueue(ProfileRequestSnapshot.From(ctx));

        await next(ctx);
    }
}
```

- [ ] **Step 4: Run capture middleware tests -expect PASS**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "ProfileCaptureMiddlewareTests" -v
```

Expected: both PASS.

- [ ] **Step 5: Create `ProfileAnalysisWorker.cs`**

The worker drains the channel and runs full bot detection on each snapshot. Before writing this file, read `src/Mostlylucid.BotDetection/Learning/LearningCoordinator.cs` to identify the correct injectable service and method signature for running detection on a constructed `HttpContext`.

Create `src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;

namespace Stylobot.Gateway.Services;

/// <summary>
///     Background worker that drains ProfileAnalysisChannel and runs full bot detection
///     on each snapshot using the detection services registered by AddBotDetection().
///     Results are written to ProfileCalibrationStore for threshold calibration.
///
///     Detection approach: constructs DefaultHttpContext from snapshot and invokes
///     the detection pipeline following the same pattern as LearningCoordinator.
///     Uses the "yarp-learning" policy (full stateless detection, no LLM) for
///     consistent calibration without external API dependency.
/// </summary>
public class ProfileAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    ProfileAnalysisChannel channel,
    ProfileCalibrationStore store,
    IOptions<ProfileModeOptions> options,
    ILogger<ProfileAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sem = new SemaphoreSlim(options.Value.Concurrency, options.Value.Concurrency);

        await foreach (var snapshot in channel.ReadAllAsync(stoppingToken))
        {
            await sem.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try { await ProcessSnapshotAsync(snapshot, stoppingToken); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Profile analysis failed for {RequestId}", snapshot.RequestId);
                }
                finally { sem.Release(); }
            }, stoppingToken);
        }
    }

    private async Task ProcessSnapshotAsync(ProfileRequestSnapshot snapshot, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        // Build DefaultHttpContext from snapshot
        var ctx = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        ctx.Request.Method = snapshot.Method;
        ctx.Request.Path = new PathString(snapshot.Path);
        ctx.Connection.RemoteIpAddress =
            IPAddress.TryParse(snapshot.ClientIp, out var ip) ? ip : null;

        foreach (var (key, values) in snapshot.Headers)
            ctx.Request.Headers[key] = values;

        if (snapshot.TlsProtocol != null) ctx.Items["TLS.Protocol"] = snapshot.TlsProtocol;
        if (snapshot.TlsCipherSuite != null) ctx.Items["TLS.CipherSuite"] = snapshot.TlsCipherSuite;

        // Run full detection pipeline.
        // IMPLEMENTER: read LearningCoordinator.cs and use the same approach to run detection
        // on the constructed ctx. The "yarp-learning" policy runs full stateless detection
        // without LLM. Typical pattern:
        //   var orchestrator = scope.ServiceProvider.GetRequiredService<IBlackboardOrchestrator>();
        //   await orchestrator.DetectAsync(ctx, "yarp-learning", ct);
        // Verify the exact interface and method in src/Mostlylucid.BotDetection/Orchestration/
        await RunDetectionAsync(ctx, scope.ServiceProvider, ct);

        // Read results from HttpContext.Items (set by BotDetectionMiddleware/orchestrator)
        var probability = ctx.Items.TryGetValue(BotDetectionMiddleware.BotConfidenceKey, out var p)
            ? Convert.ToDouble(p) : 0.0;
        var isBot = ctx.Items.TryGetValue(BotDetectionMiddleware.IsBotKey, out var b)
            && b is true;
        var botType = ctx.Items.TryGetValue(BotDetectionMiddleware.BotTypeKey, out var t)
            ? t?.ToString() : null;
        var botName = ctx.Items.TryGetValue(BotDetectionMiddleware.BotNameKey, out var n)
            ? n?.ToString() : null;
        var policy = ctx.Items.TryGetValue(BotDetectionMiddleware.PolicyNameKey, out var pol)
            ? pol?.ToString() : null;

        var riskBand = probability switch
        {
            < 0.3 => "Low",
            < 0.5 => "Medium",
            < 0.7 => "High",
            _ => "VeryHigh",
        };

        var pathPattern = NormalizePath(snapshot.Path);

        await store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = snapshot.RequestId, // signature hash would be in ctx.Items["signature.primary"]
            BotProbability = probability,
            RiskBand = riskBand,
            BotType = isBot ? botType : null,
            BotName = botName,
            TopDetector = null, // optional: read from ctx.Items if detector contribution info is available
            PathPattern = pathPattern,
        }, ct);
    }

    // IMPLEMENTER: replace stub with actual detection invocation (see LearningCoordinator.cs)
    private static Task RunDetectionAsync(
        HttpContext ctx, IServiceProvider services, CancellationToken ct)
    {
        // Find the correct injectable orchestrator type from LearningCoordinator.cs pattern
        // and call it here. This stub exists so the worker compiles and tests can verify
        // the surrounding logic. The real implementation replaces this method body.
        return Task.CompletedTask;
    }

    private static string NormalizePath(string path)
    {
        // Replace GUIDs and numeric IDs with placeholders
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            path,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "{id}");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"/\d+(/|$)", "/{id}$1");
        return normalized;
    }
}
```

- [ ] **Step 6: Build to verify compilation**

```bash
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj --no-restore 2>&1 | grep -E "(error|Build)"
```

Expected: `Build succeeded.`

- [ ] **Step 7: Implement `RunDetectionAsync` from LearningCoordinator pattern**

Read `src/Mostlylucid.BotDetection/Learning/LearningCoordinator.cs`. Find the method that runs detection on a constructed `HttpContext` and replaces the stub in `RunDetectionAsync`. The target policy is `"yarp-learning"` (full stateless detection, no LLM).

After implementing, rebuild to verify no errors:

```bash
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj --no-restore 2>&1 | grep -E "(error|Build)"
```

- [ ] **Step 8: Commit**

```bash
git add src/Stylobot.Gateway/Middleware/ProfileCaptureMiddleware.cs \
        src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs \
        src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs
git commit -m "feat(profile): ProfileCaptureMiddleware and ProfileAnalysisWorker"
```

---

## Task 5: CalibrationEndpoint

**Files:**
- Create: `src/Stylobot.Gateway/Endpoints/CalibrationEndpoint.cs`
- Test: append to `ProfileModeTests.cs`

- [ ] **Step 1: Write failing test**

Append to `ProfileModeTests.cs`:

```csharp
public class CalibrationEndpointTests
{
    [Fact]
    public async Task GetCalibration_ReturnsExpectedShape_WhenNoData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"calib_ep_{Guid.NewGuid():N}.db");
        try
        {
            var store = new ProfileCalibrationStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);
            var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 10 });

            var result = await CalibrationEndpoint.GetCalibrationAsync(store, channel, CancellationToken.None);
            var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<CalibrationResponse>>(result);
            Assert.Equal(0, ok.Value!.TotalAnalyzed);
            Assert.Null(ok.Value.RecommendedThreshold);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }
}
```

- [ ] **Step 2: Run test -expect FAIL**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "CalibrationEndpointTests" -v
```

Expected: FAIL.

- [ ] **Step 3: Create `CalibrationEndpoint.cs`**

Create `src/Stylobot.Gateway/Endpoints/CalibrationEndpoint.cs`:

```csharp
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Endpoints;

public record CalibrationResponse
{
    public long TotalAnalyzed { get; init; }
    public double CollectionPeriodHours { get; init; }
    public Dictionary<string, long> ScoreDistribution { get; init; } = new();
    public List<ThresholdSimRow> ThresholdSimulation { get; init; } = new();
    public double? RecommendedThreshold { get; init; }
    public string? RecommendationReason { get; init; }
    public int QueueDepth { get; init; }
    public long TotalDropped { get; init; }
}

public static class CalibrationEndpoint
{
    public static IEndpointRouteBuilder MapCalibrationEndpoints(
        this IEndpointRouteBuilder endpoints,
        string adminPath)
    {
        var group = endpoints.MapGroup(adminPath).WithTags("Calibration");

        group.MapGet("/calibration", GetCalibrationAsync)
            .WithName("GetCalibration")
            .WithSummary("Profile mode calibration data and threshold recommendation");

        group.MapPost("/calibration/reset", ResetCalibrationAsync)
            .WithName("ResetCalibration")
            .WithSummary("Clear all calibration data to start a fresh collection period");

        return endpoints;
    }

    public static async Task<IResult> GetCalibrationAsync(
        ProfileCalibrationStore store,
        ProfileAnalysisChannel channel,
        CancellationToken ct)
    {
        var dist = await store.GetScoreDistributionAsync(ct);
        var sim = await store.GetThresholdSimulationAsync(ct);
        var rec = await store.GetRecommendedThresholdAsync(ct);

        return Results.Ok(new CalibrationResponse
        {
            TotalAnalyzed = dist.TotalAnalyzed,
            CollectionPeriodHours = dist.CollectionPeriodHours,
            ScoreDistribution = dist.Buckets,
            ThresholdSimulation = sim,
            RecommendedThreshold = rec?.Threshold,
            RecommendationReason = rec?.Reason,
            QueueDepth = channel.QueueDepth,
            TotalDropped = channel.TotalDropped,
        });
    }

    private static async Task<IResult> ResetCalibrationAsync(
        ProfileCalibrationStore store,
        CancellationToken ct)
    {
        await store.ResetAsync(ct);
        return Results.Ok(new { message = "Calibration data cleared." });
    }
}
```

- [ ] **Step 4: Run test -expect PASS**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "CalibrationEndpointTests" -v
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/Endpoints/CalibrationEndpoint.cs \
        src/Mostlylucid.BotDetection.Test/Gateway/ProfileModeTests.cs
git commit -m "feat(profile): CalibrationEndpoint with threshold simulator"
```

---

## Task 6: Service Registration + Program.cs + StartupBanner

**Files:**
- Modify: `src/Stylobot.Gateway/Configuration/ServiceCollectionExtensions.cs`
- Modify: `src/Stylobot.Gateway/Configuration/StartupBanner.cs`
- Modify: `src/Stylobot.Gateway/Program.cs`

- [ ] **Step 1: Add profile services to `ServiceCollectionExtensions.cs`**

Add a new extension method after `AddGatewayServices`:

```csharp
public static IServiceCollection AddProfileMode(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // Bind config from env vars first, then config file
    services.Configure<ProfileModeOptions>(opts =>
    {
        var envEnabled = Environment.GetEnvironmentVariable("GATEWAY_PROFILE_MODE");
        if (bool.TryParse(envEnabled, out var enabled))
            opts.Enabled = enabled;

        var capacity = Environment.GetEnvironmentVariable("GATEWAY_PROFILE_CHANNEL_CAPACITY");
        if (int.TryParse(capacity, out var cap))
            opts.ChannelCapacity = cap;

        var concurrency = Environment.GetEnvironmentVariable("GATEWAY_PROFILE_CONCURRENCY");
        if (int.TryParse(concurrency, out var con))
            opts.Concurrency = con;
    });
    services.Configure<ProfileModeOptions>(configuration.GetSection(ProfileModeOptions.SectionName));

    // Always register -active only when Enabled=true
    services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
        return new ProfileAnalysisChannel(opts);
    });

    services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
        var dbPath = opts.DatabasePath
            ?? Path.Combine(GatewayPaths.Data, "profile_calibration.db");
        return new ProfileCalibrationStore(dbPath);
    });

    services.AddHostedService<ProfileAnalysisWorker>();

    return services;
}
```

- [ ] **Step 2: Initialize calibration store on startup**

Add a `InitializeProfileStoreAsync` extension alongside `ApplyMigrationsAsync` in `ServiceCollectionExtensions.cs`:

```csharp
public static async Task InitializeProfileStoreAsync(this WebApplication app)
{
    var opts = app.Services.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
    if (!opts.Enabled) return;

    var store = app.Services.GetRequiredService<ProfileCalibrationStore>();
    await store.InitializeAsync(app.Lifetime.ApplicationStopping);
    Log.Information("Profile mode active -calibration store ready");
}
```

- [ ] **Step 3: Add `ConfigureProfileMode` to `Program.cs`**

Add the following static method at the bottom of `Program.cs` (alongside `ConfigureDemoMode`):

```csharp
static void ConfigureProfileMode(IConfiguration configuration, IServiceCollection services)
{
    var profileModeEnv = Environment.GetEnvironmentVariable("GATEWAY_PROFILE_MODE");
    var profileModeEnabled = bool.TryParse(profileModeEnv, out var profEnabled) && profEnabled;

    if (!profileModeEnabled)
        profileModeEnabled = configuration.GetValue<bool>("Gateway:ProfileMode:Enabled");

    if (!profileModeEnabled) return;

    var demoModeEnv = Environment.GetEnvironmentVariable("GATEWAY_DEMO_MODE");
    var demoEnabled = bool.TryParse(demoModeEnv, out var de) && de;
    if (demoEnabled)
    {
        Log.Warning("Both GATEWAY_PROFILE_MODE and GATEWAY_DEMO_MODE are set -profile mode takes precedence");
        services.PostConfigure<BotDetectionOptions>(opts =>
        {
            opts.PathPolicies.Clear();
            opts.PathPolicies["/*"] = "profile";
        });
        return;
    }

    services.PostConfigure<BotDetectionOptions>(opts =>
    {
        opts.PathPolicies.Clear();
        opts.PathPolicies["/*"] = "profile";
        Log.Information("Profile mode active -fingerprint-only inline detection, background calibration enabled");
    });
}
```

- [ ] **Step 4: Wire everything in `Program.cs`**

In `Program.cs`, make these additions in order:

**After `builder.Services.AddGatewayServices();`**, add:
```csharp
// Profile mode services (channel, store, worker)
builder.Services.AddProfileMode(builder.Configuration);

// Configure profile mode (overrides PathPolicies to "profile" when enabled)
ConfigureProfileMode(builder.Configuration, builder.Services);
```

**After `await app.ApplyMigrationsAsync();`**, add:
```csharp
await app.InitializeProfileStoreAsync();
```

**After `app.UseAdminSecretMiddleware();`** and before `app.UseGeoRouting();`, add:
```csharp
// Profile capture: enqueues snapshot for background analysis (only when profile mode enabled)
app.UseMiddleware<Stylobot.Gateway.Middleware.ProfileCaptureMiddleware>();
```

**In the admin endpoints block** (after `app.MapAdminEndpoints();`), add:
```csharp
var adminPath = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.AdminBasePath;
app.MapCalibrationEndpoints(adminPath);
```

- [ ] **Step 5: Update `StartupBanner.cs` to show profile mode**

In the `Print` method of `StartupBanner.cs`, read profile mode state and update the policy line:

```csharp
// After existing policy/threshold lines, resolve profile mode
var profileMode = config.GetValue("GATEWAY_PROFILE_MODE", false)
    || bool.TryParse(Environment.GetEnvironmentVariable("GATEWAY_PROFILE_MODE"), out var pm) && pm;

var policyLine = profileMode
    ? "collecting (background analysis active)"
    : $"{policy}  |  threshold  {botThreshold:F2}";

// Replace the existing Policy line in the banner:
Console.WriteLine(Pad($"  Policy  {policyLine}", width));
```

- [ ] **Step 6: Build the gateway**

```bash
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj --no-restore 2>&1 | grep -E "(error|warning SYSLIB|Build)"
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Run full test suite**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ -v 2>&1 | tail -5
```

Expected: 0 failures.

- [ ] **Step 8: Commit**

```bash
git add src/Stylobot.Gateway/Configuration/ServiceCollectionExtensions.cs \
        src/Stylobot.Gateway/Configuration/StartupBanner.cs \
        src/Stylobot.Gateway/Program.cs
git commit -m "feat(profile): wire profile mode services, middleware, endpoints, and banner"
```

---

## Task 7: Cookbook Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/profile-mode/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/profile-mode/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/profile-mode/README.md`

- [ ] **Step 1: Create `docker-compose.yml`**

Create `src/Stylobot.Gateway/examples/profile-mode/docker-compose.yml`:

```yaml
services:
  stylobot:
    image: stylobot/gateway:latest
    ports:
      - "8080:8080"
    environment:
      - DEFAULT_UPSTREAM=http://myapp:3000
      - GATEWAY_PROFILE_MODE=true
      - GATEWAY_PROFILE_CHANNEL_CAPACITY=10000
      - GATEWAY_PROFILE_CONCURRENCY=2
      - ADMIN_SECRET=${ADMIN_SECRET}
      - TRUST_ALL_FORWARDED_PROXIES=true
    volumes:
      - ./data:/app/data    # calibration SQLite persists here
      - ./logs:/app/logs
    depends_on:
      - myapp

  myapp:
    image: nginx:alpine   # replace with your actual backend
    expose:
      - "3000"
```

- [ ] **Step 2: Create `config/appsettings.json`**

Create `src/Stylobot.Gateway/examples/profile-mode/config/appsettings.json`:

```json
{
  "Gateway": {
    "ProfileMode": {
      "Enabled": true,
      "ChannelCapacity": 10000,
      "Concurrency": 2
    }
  },
  "BotDetection": {
    "BotThreshold": 0.70,
    "DefaultActionPolicyName": "logonly"
  }
}
```

- [ ] **Step 3: Create `README.md`**

Create `src/Stylobot.Gateway/examples/profile-mode/README.md`:

```markdown
# Profile Mode Example

Use this example to collect calibration data from live traffic before deciding on a blocking threshold.

## What it does

- **Inline (per-request):** fingerprint only. ~300ns overhead, no behavioral analysis.
- **Background:** full detection pipeline runs on every request asynchronously.
- **Calibration store:** results accumulate in `/app/data/profile_calibration.db`.
- **Nothing is blocked.** Traffic flows through unchanged.

## Quick start

```bash
cp .env.example .env
# edit .env: set ADMIN_SECRET
docker compose up -d
```

After collecting 24-48 hours of traffic, query the calibration endpoint:

```bash
curl -H "X-Admin-Secret: $ADMIN_SECRET" http://localhost:8080/admin/calibration | jq .
```

Sample response:
```json
{
  "totalAnalyzed": 14823,
  "collectionPeriodHours": 48.0,
  "thresholdSimulation": [
    { "threshold": 0.50, "wouldBlock": 1875, "percentOfTraffic": 12.6 },
    { "threshold": 0.70, "wouldBlock": 847,  "percentOfTraffic": 5.7  },
    { "threshold": 0.85, "wouldBlock": 203,  "percentOfTraffic": 1.4  }
  ],
  "recommendedThreshold": 0.70,
  "recommendationReason": "Score valley at 0.7 separates human and bot clusters."
}
```

## Switching to active blocking

Once you have a threshold you trust, change `GATEWAY_PROFILE_MODE` to `false` and set your threshold:

```yaml
environment:
  - GATEWAY_PROFILE_MODE=false
  - BotDetection__BotThreshold=0.70        # from calibration recommendation
  - BotDetection__DefaultActionPolicyName=throttle-stealth
```

## Reset calibration data

To start a fresh collection period:

```bash
curl -X POST -H "X-Admin-Secret: $ADMIN_SECRET" http://localhost:8080/admin/calibration/reset
```
```

- [ ] **Step 4: Create `.env.example`**

Create `src/Stylobot.Gateway/examples/profile-mode/.env.example`:

```
ADMIN_SECRET=your-admin-secret-here
```

- [ ] **Step 5: Build and smoke-test**

```bash
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj --no-restore 2>&1 | grep -E "(error|Build)"
dotnet test src/Mostlylucid.BotDetection.Test/ 2>&1 | tail -3
```

Expected: Build succeeded, 0 test failures.

- [ ] **Step 6: Update README.md cookbook table**

In `src/Stylobot.Gateway/README.md`, add a row to the Cookbook table:

```markdown
| Profile Mode | Collect calibration data before enabling blocking | `examples/profile-mode/` |
```

- [ ] **Step 7: Commit**

```bash
git add src/Stylobot.Gateway/examples/profile-mode/ \
        src/Stylobot.Gateway/README.md
git commit -m "feat(profile): profile mode cookbook example and README entry"
```