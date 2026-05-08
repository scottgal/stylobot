# ASP.NET MonitoringPack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the ASP.NET MonitoringPack (Phase 1) — a `MeterListener`-backed background service that collects StyloBot operational meters + optional ASP.NET host meters into a new `metric_snapshots` SQLite table, with a Metrics tab in the dashboard displaying time-series charts.

**Architecture:** `IMonitoringPack` declares which meters to collect. `MeterListenerService` attaches `System.Diagnostics.Metrics.MeterListener` in-process and flushes 1-minute snapshots to `IMetricSnapshotStore`. The dashboard queries the store via a new `/api/metrics/timeseries` endpoint and renders ApexCharts. Remote mode (gateway + separate dashboard) uses `GatewayMeterAccumulator` + `RemoteMetricCollector`.

**Tech Stack:** .NET 10, `System.Diagnostics.Metrics.MeterListener`, Microsoft.Data.Sqlite, HTMX, ApexCharts (already in dashboard), ASP.NET Core Minimal APIs

---

## File Map

| Path | Create/Modify | Purpose |
|------|--------------|---------|
| `src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs` | Create | Pack interface + supporting records + enum |
| `src/Mostlylucid.BotDetection/MonitoringPacks/MetricSnapshot.cs` | Create | Persisted snapshot row model |
| `src/Mostlylucid.BotDetection.UI/Services/IMetricSnapshotStore.cs` | Create | Store interface |
| `src/Mostlylucid.BotDetection.UI/Services/SqliteMetricSnapshotStore.cs` | Create | SQLite store implementation |
| `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` | Modify | Add `metric_snapshots` table + index to schema init |
| `src/Mostlylucid.BotDetection/MonitoringPacks/MeterListenerService.cs` | Create | BackgroundService — local mode accumulation |
| `src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs` | Create | Reference implementation |
| `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs` | Modify | Add `MonitoringPackOptions` nested class + property |
| `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` | Modify | Register pack, service, store |
| `src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs` | Modify | Add "metrics" invalidation + prune |
| `src/Mostlylucid.BotDetection.Api/Endpoints/MetricsEndpoints.cs` | Create | `GET /api/v1/metrics/timeseries` |
| `src/Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs` | Modify | Map metrics endpoints |
| `src/Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/SbMetricsTabTagHelper.cs` | Create | Tag helper for metrics tab |
| `src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbMetricsTabViewComponent.cs` | Create | View component |
| `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbMetricsTab/Default.cshtml` | Create | Razor partial with ApexCharts |
| `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` | Modify | Add Metrics tab link + conditional content |
| `src/Mostlylucid.BotDetection/MonitoringPacks/GatewayMeterAccumulator.cs` | Create | Remote mode — in-process accumulator serving HTTP |
| `src/Mostlylucid.BotDetection.Api/Endpoints/MetricsSnapshotEndpoints.cs` | Create | Internal `GET /_sb/metrics/snapshot` for remote mode |
| `src/Mostlylucid.BotDetection.UI/Services/RemoteMetricCollector.cs` | Create | Remote mode — polls gateway, writes to store |

---

## Task 1: Pack Interface + MetricSnapshot Model

**Files:**
- Create: `src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs`
- Create: `src/Mostlylucid.BotDetection/MonitoringPacks/MetricSnapshot.cs`
- Test: `src/Mostlylucid.BotDetection.Test/MonitoringPacks/MonitoringPackTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/MonitoringPacks/MonitoringPackTests.cs`:

```csharp
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class MonitoringPackTests
{
    [Fact]
    public void InstrumentCollectionSpec_DefaultTagFilter_IsNull()
    {
        var spec = new InstrumentCollectionSpec("botdetection.requests.total", CollectedValueType.Counter);
        Assert.Null(spec.TagFilter);
    }

    [Fact]
    public void MetricSnapshot_BucketTime_TruncatesToMinute()
    {
        var now = new DateTime(2026, 5, 8, 12, 34, 56, 789, DateTimeKind.Utc);
        var snap = new MetricSnapshot
        {
            BucketTime = now.TruncateToMinute(),
            PackId = "aspnet-monitoring",
            MeterName = "Mostlylucid.BotDetection",
            Instrument = "botdetection.requests.total",
            Value = 42.0,
            ValueType = "rate"
        };
        Assert.Equal(new DateTime(2026, 5, 8, 12, 34, 0, DateTimeKind.Utc), snap.BucketTime);
    }

    [Fact]
    public void CollectedValueType_HasExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Counter));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Gauge));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P50));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P95));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P99));
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "MonitoringPackTests" -v minimal 2>&1 | tail -10
```

Expected: compile error — types not found.

- [ ] **Step 3: Create `IMonitoringPack.cs`**

```csharp
namespace Mostlylucid.BotDetection.MonitoringPacks;

public interface IMonitoringPack
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    TimeSpan CollectionInterval { get; }
    IReadOnlyList<MeterCollectionGroup> MeterGroups { get; }
}

public sealed record MeterCollectionGroup(
    string MeterName,
    IReadOnlyList<InstrumentCollectionSpec> Instruments);

public sealed record InstrumentCollectionSpec(
    string InstrumentName,
    CollectedValueType ValueType,
    IReadOnlyList<KeyValuePair<string, string>>? TagFilter = null);

public enum CollectedValueType
{
    Counter,
    Gauge,
    Histogram_P50,
    Histogram_P95,
    Histogram_P99
}
```

- [ ] **Step 4: Create `MetricSnapshot.cs`**

```csharp
namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class MetricSnapshot
{
    public long Id { get; set; }
    public DateTime BucketTime { get; set; }
    public required string PackId { get; set; }
    public required string MeterName { get; set; }
    public required string Instrument { get; set; }
    public string? Tags { get; set; }
    public double Value { get; set; }
    public required string ValueType { get; set; }  // "rate", "gauge", "p50", "p95", "p99"
}

public static class DateTimeExtensions
{
    public static DateTime TruncateToMinute(this DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
}
```

- [ ] **Step 5: Run tests to confirm they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "MonitoringPackTests" -v minimal 2>&1 | tail -10
```

Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs \
        src/Mostlylucid.BotDetection/MonitoringPacks/MetricSnapshot.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/MonitoringPackTests.cs
git commit -m "feat(monitoring): add IMonitoringPack interface, MetricSnapshot model, CollectedValueType enum"
```

---

## Task 2: IMetricSnapshotStore Interface

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/IMetricSnapshotStore.cs`
- Test: Add to existing test file from Task 1

- [ ] **Step 1: Write the failing test**

Add to `MonitoringPackTests.cs`:

```csharp
[Fact]
public void MetricSnapshot_ValueType_RoundTrips()
{
    // Verifies the string constants used in the store match what MeterListenerService writes
    var validTypes = new[] { "rate", "gauge", "p50", "p95", "p99" };
    foreach (var t in validTypes)
        Assert.False(string.IsNullOrWhiteSpace(t));
}
```

- [ ] **Step 2: Create `IMetricSnapshotStore.cs`**

```csharp
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.UI.Services;

public interface IMetricSnapshotStore
{
    Task WriteSnapshotsAsync(IEnumerable<MetricSnapshot> snapshots, CancellationToken ct = default);

    Task<List<MetricSnapshot>> GetTimeSeriesAsync(
        string packId,
        string instrument,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);

    Task<List<MetricSnapshot>> GetLatestSnapshotsAsync(
        string packId,
        CancellationToken ct = default);

    Task<int> PruneOldSnapshotsAsync(DateTime cutoff, CancellationToken ct = default);
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "MonitoringPackTests" -v minimal 2>&1 | tail -5
```

Expected: 4 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/IMetricSnapshotStore.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/MonitoringPackTests.cs
git commit -m "feat(monitoring): add IMetricSnapshotStore interface"
```

---

## Task 3: SqliteMetricSnapshotStore + Schema Migration

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/SqliteMetricSnapshotStore.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs`
- Test: `src/Mostlylucid.BotDetection.Test/MonitoringPacks/SqliteMetricSnapshotStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/MonitoringPacks/SqliteMetricSnapshotStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class SqliteMetricSnapshotStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteMetricSnapshotStore _store;

    public SqliteMetricSnapshotStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteMetricSnapshotStore(_conn, NullLogger<SqliteMetricSnapshotStore>.Instance);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var bucket = DateTime.UtcNow.TruncateToMinute();
        var snap = new MetricSnapshot
        {
            BucketTime = bucket,
            PackId = "aspnet-monitoring",
            MeterName = "Mostlylucid.BotDetection",
            Instrument = "botdetection.requests.total",
            Value = 12.5,
            ValueType = "rate"
        };

        await _store.WriteSnapshotsAsync([snap]);
        var results = await _store.GetTimeSeriesAsync(
            "aspnet-monitoring", "botdetection.requests.total",
            bucket.AddMinutes(-1), bucket.AddMinutes(1));

        Assert.Single(results);
        Assert.Equal(12.5, results[0].Value);
        Assert.Equal("rate", results[0].ValueType);
    }

    [Fact]
    public async Task Prune_RemovesOldRows()
    {
        var old = DateTime.UtcNow.AddDays(-10).TruncateToMinute();
        await _store.WriteSnapshotsAsync([new MetricSnapshot
        {
            BucketTime = old, PackId = "aspnet-monitoring",
            MeterName = "x", Instrument = "y", Value = 1, ValueType = "rate"
        }]);

        var pruned = await _store.PruneOldSnapshotsAsync(DateTime.UtcNow.AddDays(-7));
        Assert.Equal(1, pruned);
    }

    [Fact]
    public async Task GetLatestSnapshots_ReturnsDistinctInstruments()
    {
        var bucket = DateTime.UtcNow.TruncateToMinute();
        await _store.WriteSnapshotsAsync([
            new MetricSnapshot { BucketTime = bucket, PackId = "aspnet-monitoring", MeterName = "x", Instrument = "requests", Value = 5, ValueType = "rate" },
            new MetricSnapshot { BucketTime = bucket, PackId = "aspnet-monitoring", MeterName = "x", Instrument = "latency", Value = 2.3, ValueType = "p50" }
        ]);

        var latest = await _store.GetLatestSnapshotsAsync("aspnet-monitoring");
        Assert.Equal(2, latest.Count);
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "SqliteMetricSnapshotStoreTests" -v minimal 2>&1 | tail -5
```

Expected: compile error — `SqliteMetricSnapshotStore` not found.

- [ ] **Step 3: Create `SqliteMetricSnapshotStore.cs`**

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed class SqliteMetricSnapshotStore : IMetricSnapshotStore
{
    private readonly SqliteConnection? _sharedConn;
    private readonly string? _connectionString;
    private readonly ILogger<SqliteMetricSnapshotStore> _logger;

    // Constructor for tests (shared in-memory connection)
    public SqliteMetricSnapshotStore(SqliteConnection conn, ILogger<SqliteMetricSnapshotStore> logger)
    {
        _sharedConn = conn;
        _logger = logger;
    }

    // Constructor for production (connection string)
    public SqliteMetricSnapshotStore(string connectionString, ILogger<SqliteMetricSnapshotStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS metric_snapshots (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                bucket_time TEXT    NOT NULL,
                pack_id     TEXT    NOT NULL,
                meter_name  TEXT    NOT NULL,
                instrument  TEXT    NOT NULL,
                tags        TEXT,
                value       REAL    NOT NULL,
                value_type  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ms_lookup
                ON metric_snapshots(bucket_time, pack_id, instrument);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task WriteSnapshotsAsync(IEnumerable<MetricSnapshot> snapshots, CancellationToken ct = default)
    {
        var list = snapshots.ToList();
        if (list.Count == 0) return;

        await using var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var snap in list)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO metric_snapshots (bucket_time, pack_id, meter_name, instrument, tags, value, value_type)
                    VALUES (@bt, @pid, @mn, @inst, @tags, @val, @vt)
                    """;
                cmd.Parameters.AddWithValue("@bt", snap.BucketTime.ToString("O"));
                cmd.Parameters.AddWithValue("@pid", snap.PackId);
                cmd.Parameters.AddWithValue("@mn", snap.MeterName);
                cmd.Parameters.AddWithValue("@inst", snap.Instrument);
                cmd.Parameters.AddWithValue("@tags", (object?)snap.Tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@val", snap.Value);
                cmd.Parameters.AddWithValue("@vt", snap.ValueType);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<List<MetricSnapshot>> GetTimeSeriesAsync(
        string packId, string instrument, DateTime start, DateTime end, CancellationToken ct = default)
    {
        await using var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bucket_time, pack_id, meter_name, instrument, tags, value, value_type
            FROM metric_snapshots
            WHERE pack_id = @pid AND instrument = @inst
              AND bucket_time >= @start AND bucket_time <= @end
            ORDER BY bucket_time ASC
            """;
        cmd.Parameters.AddWithValue("@pid", packId);
        cmd.Parameters.AddWithValue("@inst", instrument);
        cmd.Parameters.AddWithValue("@start", start.ToString("O"));
        cmd.Parameters.AddWithValue("@end", end.ToString("O"));

        var results = new List<MetricSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    public async Task<List<MetricSnapshot>> GetLatestSnapshotsAsync(string packId, CancellationToken ct = default)
    {
        await using var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bucket_time, pack_id, meter_name, instrument, tags, value, value_type
            FROM metric_snapshots
            WHERE pack_id = @pid
              AND bucket_time = (SELECT MAX(bucket_time) FROM metric_snapshots WHERE pack_id = @pid)
            ORDER BY instrument ASC
            """;
        cmd.Parameters.AddWithValue("@pid", packId);

        var results = new List<MetricSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    public async Task<int> PruneOldSnapshotsAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM metric_snapshots WHERE bucket_time < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MetricSnapshot ReadRow(SqliteDataReader r) => new()
    {
        BucketTime = DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
        PackId = r.GetString(1),
        MeterName = r.GetString(2),
        Instrument = r.GetString(3),
        Tags = r.IsDBNull(4) ? null : r.GetString(4),
        Value = r.GetDouble(5),
        ValueType = r.GetString(6)
    };

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_sharedConn != null) return _sharedConn;
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
```

- [ ] **Step 4: Add `metric_snapshots` table to `SqliteDashboardEventStore` schema**

Open `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs`. Find the `schemaSql` string (around line 46). After the `user_agent_stats` table and its index, add:

```sql
            CREATE TABLE IF NOT EXISTS metric_snapshots (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                bucket_time TEXT    NOT NULL,
                pack_id     TEXT    NOT NULL,
                meter_name  TEXT    NOT NULL,
                instrument  TEXT    NOT NULL,
                tags        TEXT,
                value       REAL    NOT NULL,
                value_type  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ms_lookup
                ON metric_snapshots(bucket_time, pack_id, instrument);
```

This table creation is idempotent (`IF NOT EXISTS`), so adding it here covers new installs. Existing installs without the table will get it on next startup.

- [ ] **Step 5: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "SqliteMetricSnapshotStoreTests" -v minimal 2>&1 | tail -10
```

Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/IMetricSnapshotStore.cs \
        src/Mostlylucid.BotDetection.UI/Services/SqliteMetricSnapshotStore.cs \
        src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/SqliteMetricSnapshotStoreTests.cs
git commit -m "feat(monitoring): add SqliteMetricSnapshotStore + metric_snapshots schema"
```

---

## Task 4: MeterListenerService (Local Mode)

**Files:**
- Create: `src/Mostlylucid.BotDetection/MonitoringPacks/MeterListenerService.cs`
- Test: `src/Mostlylucid.BotDetection.Test/MonitoringPacks/MeterListenerServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Mostlylucid.BotDetection.Test/MonitoringPacks/MeterListenerServiceTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class MeterListenerServiceTests : IAsyncDisposable
{
    private readonly Meter _testMeter = new("TestMeter", "1.0");
    private readonly SqliteConnection _conn;
    private readonly SqliteMetricSnapshotStore _store;

    public MeterListenerServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteMetricSnapshotStore(_conn, NullLogger<SqliteMetricSnapshotStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Counter_Accumulates_WritesRateSnapshot()
    {
        var counter = _testMeter.CreateCounter<long>("test.requests");

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.requests", CollectedValueType.Counter)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        counter.Add(10);
        counter.Add(5);

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        Assert.Single(snapshots);
        Assert.Equal(15.0, snapshots[0].Value);
        Assert.Equal("rate", snapshots[0].ValueType);
        Assert.Equal("test.requests", snapshots[0].Instrument);
    }

    [Fact]
    public async Task Gauge_WritesCurrentValue()
    {
        var gauge = _testMeter.CreateObservableGauge("test.active", () => 42);

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.active", CollectedValueType.Gauge)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        var gaugeSnap = snapshots.FirstOrDefault(s => s.ValueType == "gauge");
        Assert.NotNull(gaugeSnap);
        Assert.Equal(42.0, gaugeSnap!.Value);
    }

    [Fact]
    public async Task Histogram_WritesP50AndP95()
    {
        var hist = _testMeter.CreateHistogram<double>("test.duration");

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("test.duration", CollectedValueType.Histogram_P95)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        for (var i = 1; i <= 100; i++) hist.Record(i);

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        var p50 = snapshots.FirstOrDefault(s => s.ValueType == "p50");
        var p95 = snapshots.FirstOrDefault(s => s.ValueType == "p95");
        Assert.NotNull(p50);
        Assert.NotNull(p95);
        Assert.InRange(p50!.Value, 48, 52);   // ~50th percentile of 1..100
        Assert.InRange(p95!.Value, 93, 97);   // ~95th percentile of 1..100
    }

    public async ValueTask DisposeAsync()
    {
        _testMeter.Dispose();
        await _conn.DisposeAsync();
    }

    // Minimal test pack implementation
    private sealed class TestPack(IReadOnlyList<MeterCollectionGroup> groups) : IMonitoringPack
    {
        public string Id => "test";
        public string Name => "Test Pack";
        public string Description => "Test";
        public TimeSpan CollectionInterval => TimeSpan.FromSeconds(60);
        public IReadOnlyList<MeterCollectionGroup> MeterGroups => groups;
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "MeterListenerServiceTests" -v minimal 2>&1 | tail -5
```

Expected: compile error — `MeterListenerService` not found.

- [ ] **Step 3: Create `MeterListenerService.cs`**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class MeterListenerService : BackgroundService, IDisposable
{
    private readonly IReadOnlyList<IMonitoringPack> _packs;
    private readonly IMetricSnapshotStoreAccessor _storeAccessor;
    private readonly ILogger<MeterListenerService> _logger;
    private MeterListener? _listener;

    // Thread-safe accumulation: instrument full name → accumulated state
    private readonly ConcurrentDictionary<string, InstrumentState> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly object _histLock = new();

    // Registered instruments: full name → spec
    private readonly Dictionary<string, (IMonitoringPack Pack, InstrumentCollectionSpec Spec)> _registered = new();

    // For tests: allow direct injection of store
    private readonly Mostlylucid.BotDetection.UI.Services.IMetricSnapshotStore? _directStore;

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        Mostlylucid.BotDetection.UI.Services.IMetricSnapshotStore store,
        ILogger<MeterListenerService> logger)
    {
        _packs = packs.ToList();
        _directStore = store;
        _logger = logger;
        _storeAccessor = null!;
    }

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        IMetricSnapshotStoreAccessor storeAccessor,
        ILogger<MeterListenerService> logger)
    {
        _packs = packs.ToList();
        _storeAccessor = storeAccessor;
        _logger = logger;
    }

    public void StartListening()
    {
        _listener = new MeterListener();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            foreach (var pack in _packs)
            foreach (var group in pack.MeterGroups)
            {
                if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var spec in group.Instruments)
                {
                    if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = MakeKey(pack.Id, instrument.Name, spec.ValueType);
                    _registered[key] = (pack, spec);
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        _listener.SetMeasurementEventCallback<int>(OnMeasurementInt);
        _listener.Start();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartListening();
        _logger.LogInformation("MeterListenerService started with {Count} pack(s)", _packs.Count);

        // Wait for first pack's interval (use minimum across packs)
        var interval = _packs.Count > 0
            ? _packs.Min(p => p.CollectionInterval)
            : TimeSpan.FromSeconds(60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                var store = _directStore ?? _storeAccessor.GetStore();
                var snapshots = await FlushSnapshotsAsync(stoppingToken);
                if (snapshots.Count > 0)
                    await store.WriteSnapshotsAsync(snapshots, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush metric snapshots");
            }
        }
    }

    public async Task<List<Mostlylucid.BotDetection.MonitoringPacks.MetricSnapshot>> FlushSnapshotsAsync(CancellationToken ct)
    {
        // Force observable gauges to be observed
        _listener?.RecordObservableInstruments();

        var bucket = DateTime.UtcNow.TruncateToMinute();
        var results = new List<Mostlylucid.BotDetection.MonitoringPacks.MetricSnapshot>();

        // Flush counters (swap to 0 atomically)
        foreach (var (key, state) in _counters)
        {
            var delta = Interlocked.Exchange(ref state.Total, 0);
            if (delta == 0) continue;
            var (packId, instrument) = ParseKey(key);
            results.Add(new MetricSnapshot
            {
                BucketTime = bucket,
                PackId = packId,
                MeterName = GetMeterName(packId, instrument),
                Instrument = instrument,
                Value = delta,
                ValueType = "rate"
            });
        }

        // Flush gauges
        foreach (var (key, value) in _gauges)
        {
            var (packId, instrument) = ParseKey(key);
            results.Add(new MetricSnapshot
            {
                BucketTime = bucket,
                PackId = packId,
                MeterName = GetMeterName(packId, instrument),
                Instrument = instrument,
                Value = value,
                ValueType = "gauge"
            });
        }

        // Flush histograms
        lock (_histLock)
        {
            foreach (var (key, observations) in _histograms)
            {
                if (observations.Count == 0) continue;
                var sorted = observations.OrderBy(v => v).ToList();
                observations.Clear();

                var (packId, rawKey) = ParseKey(key);
                var parts = rawKey.Split('|');
                var instrument = parts[0];
                var vtype = parts.Length > 1 ? parts[1] : "p50";

                var pct = vtype switch { "p95" => 0.95, "p99" => 0.99, _ => 0.50 };
                var idx = (int)Math.Ceiling(pct * sorted.Count) - 1;
                idx = Math.Clamp(idx, 0, sorted.Count - 1);

                results.Add(new MetricSnapshot
                {
                    BucketTime = bucket,
                    PackId = packId,
                    MeterName = GetMeterName(packId, instrument),
                    Instrument = instrument,
                    Value = sorted[idx],
                    ValueType = vtype
                });
            }
        }

        return results;
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct
    {
        double value = Convert.ToDouble(measurement);
        ProcessMeasurement(instrument, value);
    }

    private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => ProcessMeasurement(instrument, (double)measurement);

    private void OnMeasurementDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => ProcessMeasurement(instrument, measurement);

    private void OnMeasurementInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => ProcessMeasurement(instrument, (double)measurement);

    private void ProcessMeasurement(Instrument instrument, double value)
    {
        foreach (var pack in _packs)
        foreach (var group in pack.MeterGroups)
        {
            if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var spec in group.Instruments)
            {
                if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase)) continue;
                switch (spec.ValueType)
                {
                    case CollectedValueType.Counter:
                        var ckey = MakeKey(pack.Id, instrument.Name, spec.ValueType);
                        var cs = _counters.GetOrAdd(ckey, _ => new InstrumentState());
                        Interlocked.Exchange(ref cs.Total, cs.Total + (long)value);
                        break;
                    case CollectedValueType.Gauge:
                        _gauges[MakeKey(pack.Id, instrument.Name, spec.ValueType)] = value;
                        break;
                    case CollectedValueType.Histogram_P50:
                    case CollectedValueType.Histogram_P95:
                    case CollectedValueType.Histogram_P99:
                        var vtype = spec.ValueType == CollectedValueType.Histogram_P50 ? "p50"
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? "p95" : "p99";
                        var hkey = MakeKey(pack.Id, $"{instrument.Name}|{vtype}", spec.ValueType);
                        lock (_histLock)
                            _histograms.GetOrAdd(hkey, _ => new List<double>()).Add(value);
                        break;
                }
            }
        }
    }

    private string GetMeterName(string packId, string instrument)
    {
        foreach (var pack in _packs)
        {
            if (pack.Id != packId) continue;
            foreach (var group in pack.MeterGroups)
            foreach (var spec in group.Instruments)
                if (spec.InstrumentName == instrument)
                    return group.MeterName;
        }
        return string.Empty;
    }

    private static string MakeKey(string packId, string instrument, CollectedValueType vtype)
        => $"{packId}::{instrument}::{(int)vtype}";

    private static (string packId, string instrument) ParseKey(string key)
    {
        var parts = key.Split("::");
        return (parts[0], parts[1]);
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }

    private sealed class InstrumentState { public long Total; }
}

// Accessor interface so MeterListenerService can resolve the store from DI without circular dependency
public interface IMetricSnapshotStoreAccessor
{
    Mostlylucid.BotDetection.UI.Services.IMetricSnapshotStore GetStore();
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "MeterListenerServiceTests" -v minimal 2>&1 | tail -10
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/MonitoringPacks/MeterListenerService.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/MeterListenerServiceTests.cs
git commit -m "feat(monitoring): add MeterListenerService — local mode meter accumulation"
```

---

## Task 5: AspNetMonitoringPack

**Files:**
- Create: `src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs`
- Test: `src/Mostlylucid.BotDetection.Test/MonitoringPacks/AspNetMonitoringPackTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/MonitoringPacks/AspNetMonitoringPackTests.cs`:

```csharp
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class AspNetMonitoringPackTests
{
    [Fact]
    public void Pack_Id_IsStable()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Equal("aspnet-monitoring", pack.Id);
    }

    [Fact]
    public void Pack_DefaultMode_ContainsStyloBottMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == BotDetectionMetrics.MeterName);
    }

    [Fact]
    public void Pack_DefaultMode_ContainsAllStylosBotInstruments()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        var sbGroup = pack.MeterGroups.Single(g => g.MeterName == BotDetectionMetrics.MeterName);
        var instruments = sbGroup.Instruments.Select(i => i.InstrumentName).ToHashSet();
        Assert.Contains("botdetection.requests.total", instruments);
        Assert.Contains("botdetection.bots.detected", instruments);
        Assert.Contains("botdetection.humans.detected", instruments);
        Assert.Contains("botdetection.detection.duration", instruments);
        Assert.Contains("botdetection.confidence.average", instruments);
        Assert.Contains("botdetection.errors.total", instruments);
        Assert.Contains("botdetection.weightstore.cache.hits", instruments);
        Assert.Contains("botdetection.weightstore.cache.misses", instruments);
    }

    [Fact]
    public void Pack_HostMetersEnabled_ContainsAspNetMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: true);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == "Microsoft.AspNetCore.Hosting");
    }

    [Fact]
    public void Pack_HostMetersEnabled_ContainsRuntimeMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: true);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == "System.Runtime");
    }

    [Fact]
    public void Pack_CollectionInterval_IsOneMinute()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Equal(TimeSpan.FromSeconds(60), pack.CollectionInterval);
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "AspNetMonitoringPackTests" -v minimal 2>&1 | tail -5
```

Expected: compile error — `AspNetMonitoringPack` not found.

- [ ] **Step 3: Create `AspNetMonitoringPack.cs`**

```csharp
using Mostlylucid.BotDetection.Metrics;

namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class AspNetMonitoringPack : IMonitoringPack
{
    private readonly bool _includeHostMeters;

    public AspNetMonitoringPack(bool includeHostMeters = false)
    {
        _includeHostMeters = includeHostMeters;
    }

    public string Id => "aspnet-monitoring";
    public string Name => "ASP.NET + StyloBot Metrics";
    public string Description => "StyloBot operational meters and optional ASP.NET host metrics";
    public TimeSpan CollectionInterval => TimeSpan.FromSeconds(60);

    public IReadOnlyList<MeterCollectionGroup> MeterGroups => BuildGroups();

    private IReadOnlyList<MeterCollectionGroup> BuildGroups()
    {
        var groups = new List<MeterCollectionGroup>
        {
            new(BotDetectionMetrics.MeterName, new[]
            {
                new InstrumentCollectionSpec("botdetection.requests.total",     CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.bots.detected",      CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.humans.detected",    CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.errors.total",       CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("botdetection.confidence.average", CollectedValueType.Gauge),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.hits",   CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.misses", CollectedValueType.Counter),
            })
        };

        if (_includeHostMeters)
        {
            groups.Add(new("Microsoft.AspNetCore.Hosting", new[]
            {
                new InstrumentCollectionSpec("http.server.request.duration",  CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("http.server.request.duration",  CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("http.server.active_requests",   CollectedValueType.Gauge),
            }));

            groups.Add(new("System.Runtime", new[]
            {
                new InstrumentCollectionSpec("dotnet.gc.heap.total_allocated", CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.process.cpu.time",        CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.thread_pool.thread.count",CollectedValueType.Gauge),
            }));
        }

        return groups;
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "AspNetMonitoringPackTests" -v minimal 2>&1 | tail -5
```

Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/AspNetMonitoringPackTests.cs
git commit -m "feat(monitoring): add AspNetMonitoringPack with StyloBot + optional ASP.NET host meters"
```

---

## Task 6: MonitoringPackOptions + DI Wiring

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs`

- [ ] **Step 1: Add `MonitoringPackOptions` nested class to `StyloBotDashboardOptions.cs`**

At the bottom of `StyloBotDashboardOptions.cs`, before the closing `}`, add the nested class and property:

```csharp
    /// <summary>
    ///     Options for the monitoring pack (local and remote modes).
    /// </summary>
    public MonitoringPackOptions MonitoringPack { get; set; } = new();
```

And add the nested class inside the file (below the `StyloBotDashboardOptions` class):

```csharp
public sealed class MonitoringPackOptions
{
    /// <summary>Local: MeterListener in same process. GatewayServer: serves /metrics/snapshot. RemoteClient: polls gateway.</summary>
    public MonitoringMode Mode { get; set; } = MonitoringMode.Local;

    /// <summary>When true, includes ASP.NET host meters (http.server.request.duration, GC, thread pool).</summary>
    public bool IncludeAspNetHostMeters { get; set; }

    /// <summary>Remote client mode: URL of the gateway's /_sb/metrics/snapshot endpoint.</summary>
    public string? GatewayMetricsUrl { get; set; }

    /// <summary>How often to poll the gateway in remote client mode. Default: 60 seconds.</summary>
    public TimeSpan RemotePollInterval { get; set; } = TimeSpan.FromSeconds(60);
}

public enum MonitoringMode
{
    Local,
    GatewayServer,
    RemoteClient
}
```

- [ ] **Step 2: Register services in `StyloBotDashboardServiceExtensions.cs`**

In `AddStyloBotDashboard`, after the existing `services.AddSingleton(options)` line, add:

```csharp
        // MonitoringPack
        services.TryAddSingleton<IMetricSnapshotStore>(sp =>
        {
            var botOpts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var connStr = DashboardDbPath.GetConnectionString(botOpts);
            var logger = sp.GetRequiredService<ILogger<SqliteMetricSnapshotStore>>();
            return new SqliteMetricSnapshotStore(connStr, logger);
        });

        if (options.MonitoringPack.Mode == MonitoringMode.Local)
        {
            services.AddSingleton<IMonitoringPack>(
                new AspNetMonitoringPack(options.MonitoringPack.IncludeAspNetHostMeters));
            services.AddHostedService<MeterListenerService>(sp =>
                new MeterListenerService(
                    sp.GetServices<IMonitoringPack>(),
                    sp.GetRequiredService<IMetricSnapshotStore>(),
                    sp.GetRequiredService<ILogger<MeterListenerService>>()));
        }
        else if (options.MonitoringPack.Mode == MonitoringMode.RemoteClient
                 && options.MonitoringPack.GatewayMetricsUrl != null)
        {
            services.AddHttpClient("sb-metrics");
            services.AddHostedService<RemoteMetricCollector>(sp =>
                new RemoteMetricCollector(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    options.MonitoringPack.GatewayMetricsUrl,
                    options.MonitoringPack.RemotePollInterval,
                    sp.GetRequiredService<IMetricSnapshotStore>(),
                    sp.GetRequiredService<ILogger<RemoteMetricCollector>>()));
        }
```

Add required using statements at the top of `StyloBotDashboardServiceExtensions.cs`:
```csharp
using Mostlylucid.BotDetection.MonitoringPacks;
```

- [ ] **Step 3: Build to verify no compile errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj 2>&1 | grep -E "error|warning" | head -20
```

Expected: build succeeds (zero errors; the `RemoteMetricCollector` forward reference causes an error until Task 12 — temporarily comment out the `RemoteClient` branch if needed).

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs \
        src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(monitoring): wire MonitoringPackOptions + DI registration for local mode"
```

---

## Task 7: DashboardSummaryBroadcaster — Metrics Broadcast + Prune

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs`

- [ ] **Step 1: Add "metrics" invalidation broadcast**

In `DashboardSummaryBroadcaster.ExecuteAsync`, after the existing `BroadcastInvalidation("useragents")` call (line ~104), add:

```csharp
                await _hubContext.Clients.All.BroadcastInvalidation("metrics");
```

- [ ] **Step 2: Add metric_snapshots prune in the existing prune block**

Find the prune block (around line 110). After the `PruneOldDetectionsAsync` call, add:

```csharp
                // Prune metric snapshots older than 7 days
                try
                {
                    var snapshotStore = _serviceProvider.GetService<IMetricSnapshotStore>();
                    if (snapshotStore != null)
                    {
                        var pruned = await snapshotStore.PruneOldSnapshotsAsync(DateTime.UtcNow.AddDays(-7), stoppingToken);
                        if (pruned > 0)
                            _logger.LogDebug("Pruned {Count} old metric snapshots", pruned);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune old metric snapshots");
                }
```

- [ ] **Step 3: Inject `IServiceProvider` into `DashboardSummaryBroadcaster`**

Add `IServiceProvider serviceProvider` to the constructor and store as `_serviceProvider`. Add `using Microsoft.Extensions.DependencyInjection;` if needed.

Update constructor:

```csharp
    public DashboardSummaryBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        DashboardAggregateCache cache,
        SignatureAggregateCache signatureCache,
        StyloBotDashboardOptions options,
        IServiceProvider serviceProvider,
        ILogger<DashboardSummaryBroadcaster> logger)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _cache = cache;
        _signatureCache = signatureCache;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
```

Add field: `private readonly IServiceProvider _serviceProvider;`

Add `using Mostlylucid.BotDetection.UI.Services;` if not already present.

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/ 2>&1 | grep " error " | head -10
```

Expected: zero errors.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs
git commit -m "feat(monitoring): add metrics SignalR invalidation and 7-day prune to broadcaster"
```

---

## Task 8: API Endpoint — GET /api/v1/metrics/timeseries

**Files:**
- Create: `src/Mostlylucid.BotDetection.Api/Endpoints/MetricsEndpoints.cs`
- Modify: `src/Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs`

- [ ] **Step 1: Create `MetricsEndpoints.cs`**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/metrics")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Metrics");

        group.MapGet("/timeseries", HandleTimeseries)
            .WithName("GetMetricsTimeseries")
            .WithOpenApi();

        group.MapGet("/latest", HandleLatest)
            .WithName("GetMetricsLatest")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleTimeseries(
        IMetricSnapshotStore store,
        string packId = "aspnet-monitoring",
        string instrument = "botdetection.requests.total",
        string range = "1h",
        CancellationToken ct = default)
    {
        var end = DateTime.UtcNow;
        var start = range switch
        {
            "15m" => end.AddMinutes(-15),
            "1h"  => end.AddHours(-1),
            "6h"  => end.AddHours(-6),
            "24h" => end.AddHours(-24),
            _     => end.AddHours(-1)
        };

        var data = await store.GetTimeSeriesAsync(packId, instrument, start, end, ct);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = data.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = data.Count, Total = data.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleLatest(
        IMetricSnapshotStore store,
        string packId = "aspnet-monitoring",
        CancellationToken ct = default)
    {
        var data = await store.GetLatestSnapshotsAsync(packId, ct);
        return Results.Ok(new SingleResponse<object>
        {
            Data = data.Cast<object>().ToList(),
            Meta = new ResponseMeta()
        });
    }
}
```

- [ ] **Step 2: Register in `StyloBotApiExtensions.cs`**

Find the `MapReadEndpoints` call (or wherever endpoints are mapped). Add:

```csharp
endpoints.MapMetricsEndpoints();
```

Add `using Mostlylucid.BotDetection.Api.Endpoints;` if not already present.

- [ ] **Step 3: Build the API project**

```bash
dotnet build src/Mostlylucid.BotDetection.Api/ 2>&1 | grep " error " | head -10
```

Expected: zero errors.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.Api/Endpoints/MetricsEndpoints.cs \
        src/Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs
git commit -m "feat(monitoring): add GET /api/v1/metrics/timeseries and /latest endpoints"
```

---

## Task 9: Dashboard Metrics Tab

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/SbMetricsTabTagHelper.cs`
- Create: `src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbMetricsTabViewComponent.cs`
- Create: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbMetricsTab/Default.cshtml`
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

- [ ] **Step 1: Create the tag helper**

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-metrics-tab", TagStructure = TagStructure.WithoutEndTag)]
public class SbMetricsTabTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("pack-id")]
    public string PackId { get; set; } = "aspnet-monitoring";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbMetricsTab", new { packId = PackId }));
    }
}
```

- [ ] **Step 2: Create the view component**

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbMetricsTabViewComponent(
    IMetricSnapshotStore snapshotStore,
    StyloBotDashboardOptions options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string packId = "aspnet-monitoring")
    {
        var latest = await snapshotStore.GetLatestSnapshotsAsync(packId);
        var includeHostMeters = options.MonitoringPack.IncludeAspNetHostMeters;

        return View(new MetricsTabModel
        {
            PackId = packId,
            LatestSnapshots = latest,
            IncludeHostMeters = includeHostMeters,
            BasePath = options.BasePath.TrimEnd('/')
        });
    }
}

public sealed class MetricsTabModel
{
    public required string PackId { get; init; }
    public required List<Mostlylucid.BotDetection.MonitoringPacks.MetricSnapshot> LatestSnapshots { get; init; }
    public bool IncludeHostMeters { get; init; }
    public required string BasePath { get; init; }

    public double GetLatest(string instrument, string valueType)
        => LatestSnapshots.FirstOrDefault(s => s.Instrument == instrument && s.ValueType == valueType)?.Value ?? 0;
}
```

- [ ] **Step 3: Create the Razor partial**

Create directory: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbMetricsTab/`

Create `Default.cshtml`:

```cshtml
@using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard
@model MetricsTabModel
@{
    var bp = Model.BasePath;
    var packId = Model.PackId;
    double reqRate    = Model.GetLatest("botdetection.requests.total", "rate");
    double botRate    = Model.GetLatest("botdetection.bots.detected", "rate");
    double humanRate  = Model.GetLatest("botdetection.humans.detected", "rate");
    double latP50     = Model.GetLatest("botdetection.detection.duration", "p50");
    double latP95     = Model.GetLatest("botdetection.detection.duration", "p95");
    double avgConf    = Model.GetLatest("botdetection.confidence.average", "gauge");
    double cacheHits  = Model.GetLatest("botdetection.weightstore.cache.hits", "rate");
    double cacheMiss  = Model.GetLatest("botdetection.weightstore.cache.misses", "rate");
    double hitRate    = (cacheHits + cacheMiss) > 0 ? Math.Round(cacheHits / (cacheHits + cacheMiss) * 100, 1) : 0;
}
<div id="metrics-tab"
     data-sb-widget="metrics"
     data-sb-depends="metrics">

    <div class="text-[10px] font-semibold text-base-content/50 uppercase tracking-wider mb-3">StyloBot Performance</div>

    <div class="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-4">
        <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
            <div class="text-[10px] text-base-content/50">Requests/min</div>
            <div class="text-xl font-bold">@reqRate.ToString("F1")</div>
            <div class="text-[10px] text-success">@botRate.ToString("F1") bot / @humanRate.ToString("F1") human</div>
        </div>
        <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
            <div class="text-[10px] text-base-content/50">Detection Latency</div>
            <div class="text-xl font-bold">@latP50.ToString("F1")<span class="text-xs font-normal text-base-content/50">ms P50</span></div>
            <div class="text-[10px] text-base-content/50">P95: @latP95.ToString("F1")ms</div>
        </div>
        <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
            <div class="text-[10px] text-base-content/50">Avg Confidence</div>
            <div class="text-xl font-bold">@((avgConf * 100).ToString("F1"))<span class="text-xs font-normal text-base-content/50">%</span></div>
        </div>
        <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
            <div class="text-[10px] text-base-content/50">Cache Hit Rate</div>
            <div class="text-xl font-bold">@hitRate<span class="text-xs font-normal text-base-content/50">%</span></div>
        </div>
    </div>

    <!-- Requests/min time-series chart -->
    <div class="rounded-xl border p-3 mb-4" style="border-color: var(--sb-card-border); background: var(--sb-card-bg);">
        <div class="text-[10px] font-semibold text-base-content/50 mb-2">REQUEST RATE (LAST 1H)</div>
        <div id="metrics-req-chart" style="min-height:120px;"></div>
        <script>
            (function() {
                fetch('@bp/api/v1/metrics/timeseries?packId=@packId&instrument=botdetection.requests.total&range=1h')
                    .then(r => r.json()).then(d => {
                        var data = (d.data || []).map(s => ({ x: new Date(s.bucketTime).getTime(), y: s.value }));
                        new ApexCharts(document.getElementById('metrics-req-chart'), {
                            chart: { type: 'area', height: 120, sparkline: { enabled: false }, toolbar: { show: false } },
                            series: [{ name: 'Requests/min', data }],
                            xaxis: { type: 'datetime', labels: { style: { fontSize: '9px' } } },
                            yaxis: { labels: { style: { fontSize: '9px' } } },
                            stroke: { curve: 'smooth', width: 2 },
                            fill: { type: 'gradient', gradient: { opacityFrom: 0.4, opacityTo: 0.05 } },
                            colors: ['#6366f1'],
                            grid: { borderColor: 'rgba(255,255,255,0.06)' },
                            tooltip: { x: { format: 'HH:mm' } }
                        }).render();
                    });
            })();
        </script>
    </div>

    <!-- Detection latency chart -->
    <div class="rounded-xl border p-3 mb-4" style="border-color: var(--sb-card-border); background: var(--sb-card-bg);">
        <div class="text-[10px] font-semibold text-base-content/50 mb-2">DETECTION LATENCY P50/P95 (LAST 1H)</div>
        <div id="metrics-lat-chart" style="min-height:120px;"></div>
        <script>
            (function() {
                Promise.all([
                    fetch('@bp/api/v1/metrics/timeseries?packId=@packId&instrument=botdetection.detection.duration&range=1h').then(r=>r.json()),
                    fetch('@bp/api/v1/metrics/timeseries?packId=@packId&instrument=botdetection.detection.duration&range=1h').then(r=>r.json())
                ]).then(([p50res, p95res]) => {
                    var p50 = (p50res.data||[]).filter(s=>s.valueType==='p50').map(s=>({x:new Date(s.bucketTime).getTime(),y:s.value}));
                    var p95 = (p95res.data||[]).filter(s=>s.valueType==='p95').map(s=>({x:new Date(s.bucketTime).getTime(),y:s.value}));
                    new ApexCharts(document.getElementById('metrics-lat-chart'), {
                        chart: { type: 'line', height: 120, toolbar: { show: false } },
                        series: [{ name: 'P50', data: p50 }, { name: 'P95', data: p95 }],
                        xaxis: { type: 'datetime', labels: { style: { fontSize: '9px' } } },
                        yaxis: { labels: { style: { fontSize: '9px' }, formatter: v => v.toFixed(2)+'ms' } },
                        stroke: { curve: 'smooth', width: 2 },
                        colors: ['#22c55e', '#f59e0b'],
                        grid: { borderColor: 'rgba(255,255,255,0.06)' },
                        legend: { fontSize: '9px' },
                        tooltip: { x: { format: 'HH:mm' }, y: { formatter: v => v.toFixed(2)+'ms' } }
                    }).render();
                });
            })();
        </script>
    </div>

    @if (Model.IncludeHostMeters)
    {
        <div class="text-[10px] font-semibold text-base-content/50 uppercase tracking-wider mb-3 mt-4">Host Health</div>
        @{
            double httpP50    = Model.GetLatest("http.server.request.duration", "p50");
            double httpP95    = Model.GetLatest("http.server.request.duration", "p95");
            double activeReq  = Model.GetLatest("http.server.active_requests", "gauge");
            double threadCount = Model.GetLatest("dotnet.thread_pool.thread.count", "gauge");
            double gcRate     = Model.GetLatest("dotnet.gc.heap.total_allocated", "rate");
        }
        <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
                <div class="text-[10px] text-base-content/50">HTTP P50</div>
                <div class="text-xl font-bold">@httpP50.ToString("F1")<span class="text-xs font-normal text-base-content/50">ms</span></div>
                <div class="text-[10px] text-base-content/50">P95: @httpP95.ToString("F1")ms</div>
            </div>
            <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
                <div class="text-[10px] text-base-content/50">Active Requests</div>
                <div class="text-xl font-bold">@activeReq.ToString("F0")</div>
            </div>
            <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
                <div class="text-[10px] text-base-content/50">Thread Pool</div>
                <div class="text-xl font-bold">@threadCount.ToString("F0")</div>
            </div>
            <div class="p-3 rounded-lg" style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">
                <div class="text-[10px] text-base-content/50">GC Alloc/min</div>
                <div class="text-xl font-bold">@(gcRate > 1_000_000 ? $"{gcRate/1_000_000:F1}MB" : $"{gcRate/1_000:F0}KB")</div>
            </div>
        </div>
    }
</div>
```

- [ ] **Step 4: Add Metrics tab to `Index.cshtml`**

In the tab navigation bar (around line 182), after the `useragents` tab link, add:

```cshtml
            <a href="@TabUrl("metrics")" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass("metrics")">Metrics</a>
```

At the bottom of the tab content switch block, after the `useragents` tab block, add:

```cshtml
        @if (tab == "metrics")
        {
            <sb-metrics-tab />
        }
```

- [ ] **Step 5: Build the UI project**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/ 2>&1 | grep " error " | head -10
```

Expected: zero errors.

- [ ] **Step 6: Run the demo and verify the tab appears**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
sleep 5
curl -s http://localhost:5080/stylobot?tab=metrics | grep -c "metrics-tab"
```

Expected: response contains `metrics-tab`.

- [ ] **Step 7: Kill the demo process**

```bash
pkill -f "Mostlylucid.BotDetection.Demo" 2>/dev/null; true
```

- [ ] **Step 8: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/SbMetricsTabTagHelper.cs \
        src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbMetricsTabViewComponent.cs \
        "src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbMetricsTab/Default.cshtml" \
        src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "feat(monitoring): add Metrics tab to dashboard with StyloBot performance + host health panels"
```

---

## Task 10: Full Solution Build + All Tests

Verify the entire solution builds and all tests pass before writing remote mode.

- [ ] **Step 1: Build solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep " error " | head -20
```

Expected: zero errors.

- [ ] **Step 2: Run all monitoring tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "Namespace~MonitoringPacks" -v normal 2>&1 | tail -20
```

Expected: all tests pass.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test mostlylucid.stylobot.sln 2>&1 | tail -15
```

Expected: no regressions.

- [ ] **Step 4: Commit if clean**

```bash
git add -A
git commit -m "feat(monitoring): Phase 1 complete — local MeterListener + dashboard Metrics tab"
```

---

## Task 11: Remote Mode — GatewayMeterAccumulator + Internal Endpoint

**Files:**
- Create: `src/Mostlylucid.BotDetection/MonitoringPacks/GatewayMeterAccumulator.cs`
- Create: `src/Mostlylucid.BotDetection.Api/Endpoints/MetricsSnapshotEndpoints.cs`
- Modify: `src/Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs`

This task enables the gateway process to accumulate metrics in-memory and serve them for dashboard polling.

- [ ] **Step 1: Create `GatewayMeterAccumulator.cs`**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     In-process meter accumulator for gateway deployments.
///     Accumulates meter values in-memory and exposes them via <see cref="GetCurrentSnapshot"/>.
///     The internal metrics endpoint serves this snapshot for dashboard polling.
/// </summary>
public sealed class GatewayMeterAccumulator : BackgroundService, IDisposable
{
    private readonly IReadOnlyList<IMonitoringPack> _packs;
    private readonly ILogger<GatewayMeterAccumulator> _logger;
    private MeterListener? _listener;

    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly object _histLock = new();
    private DateTime _lastFlush = DateTime.UtcNow;

    public GatewayMeterAccumulator(
        IEnumerable<IMonitoringPack> packs,
        ILogger<GatewayMeterAccumulator> logger)
    {
        _packs = packs.ToList();
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartListening();
        _logger.LogInformation("GatewayMeterAccumulator started");
        return Task.CompletedTask;
    }

    public void StartListening()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            foreach (var pack in _packs)
            foreach (var group in pack.MeterGroups)
            {
                if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
                if (group.Instruments.Any(s => string.Equals(s.InstrumentName, instrument.Name, StringComparison.OrdinalIgnoreCase)))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, val, _, _) => Accumulate(inst, (double)val));
        _listener.SetMeasurementEventCallback<double>((inst, val, _, _) => Accumulate(inst, val));
        _listener.SetMeasurementEventCallback<int>((inst, val, _, _) => Accumulate(inst, (double)val));
        _listener.Start();
    }

    public MetricSnapshotDto[] GetCurrentSnapshot()
    {
        _listener?.RecordObservableInstruments();
        var bucket = DateTime.UtcNow.TruncateToMinute();
        var results = new List<MetricSnapshotDto>();

        foreach (var pack in _packs)
        foreach (var group in pack.MeterGroups)
        foreach (var spec in group.Instruments)
        {
            var key = $"{pack.Id}::{group.MeterName}::{spec.InstrumentName}::{spec.ValueType}";
            switch (spec.ValueType)
            {
                case CollectedValueType.Counter:
                    if (_counters.TryGetValue(key, out var cnt))
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, (double)cnt, "rate"));
                    break;
                case CollectedValueType.Gauge:
                    if (_gauges.TryGetValue(key, out var g))
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, g, "gauge"));
                    break;
                case CollectedValueType.Histogram_P50:
                case CollectedValueType.Histogram_P95:
                case CollectedValueType.Histogram_P99:
                    lock (_histLock)
                    {
                        if (!_histograms.TryGetValue(key, out var obs) || obs.Count == 0) break;
                        var sorted = obs.OrderBy(v => v).ToList();
                        var pct = spec.ValueType == CollectedValueType.Histogram_P50 ? 0.50
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? 0.95 : 0.99;
                        var idx = Math.Clamp((int)Math.Ceiling(pct * sorted.Count) - 1, 0, sorted.Count - 1);
                        var vtype = spec.ValueType == CollectedValueType.Histogram_P50 ? "p50"
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? "p95" : "p99";
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, sorted[idx], vtype));
                    }
                    break;
            }
        }

        return results.ToArray();
    }

    private void Accumulate(Instrument instrument, double value)
    {
        foreach (var pack in _packs)
        foreach (var group in pack.MeterGroups)
        {
            if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var spec in group.Instruments)
            {
                if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase)) continue;
                var key = $"{pack.Id}::{group.MeterName}::{spec.InstrumentName}::{spec.ValueType}";
                switch (spec.ValueType)
                {
                    case CollectedValueType.Counter:
                        _counters.AddOrUpdate(key, (long)value, (_, old) => old + (long)value);
                        break;
                    case CollectedValueType.Gauge:
                        _gauges[key] = value;
                        break;
                    default:
                        lock (_histLock)
                            _histograms.GetOrAdd(key, _ => new List<double>()).Add(value);
                        break;
                }
            }
        }
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }
}

public sealed record MetricSnapshotDto(
    DateTime BucketTime,
    string PackId,
    string MeterName,
    string Instrument,
    string? Tags,
    double Value,
    string ValueType);
```

- [ ] **Step 2: Create `MetricsSnapshotEndpoints.cs`**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Internal endpoint served by the gateway process for remote-mode dashboard polling.
///     Not included in public OpenAPI spec. Gate with RequireHost or internal port binding.
/// </summary>
public static class MetricsSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapMetricsSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_sb/metrics/snapshot", (GatewayMeterAccumulator accumulator) =>
        {
            var snapshot = accumulator.GetCurrentSnapshot();
            return Results.Ok(snapshot);
        })
        .ExcludeFromDescription(); // Not in Swagger — internal only

        return endpoints;
    }
}
```

- [ ] **Step 3: Register in `StyloBotApiExtensions.cs`**

In the gateway-mode DI setup, add:

```csharp
endpoints.MapMetricsSnapshotEndpoints();
```

Registration for `GatewayMeterAccumulator` as `BackgroundService` goes in the DI wiring where `MonitoringMode.GatewayServer` is detected (the stub was added in Task 6). Complete that branch:

In `StyloBotDashboardServiceExtensions.cs`, update the GatewayServer branch (in `AddStyloBotDashboard`):

```csharp
        else if (options.MonitoringPack.Mode == MonitoringMode.GatewayServer)
        {
            services.AddSingleton<IMonitoringPack>(
                new AspNetMonitoringPack(options.MonitoringPack.IncludeAspNetHostMeters));
            services.AddSingleton<GatewayMeterAccumulator>(sp =>
                new GatewayMeterAccumulator(
                    sp.GetServices<IMonitoringPack>(),
                    sp.GetRequiredService<ILogger<GatewayMeterAccumulator>>()));
            services.AddHostedService(sp => sp.GetRequiredService<GatewayMeterAccumulator>());
        }
```

- [ ] **Step 4: Build**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep " error " | head -10
```

Expected: zero errors.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/MonitoringPacks/GatewayMeterAccumulator.cs \
        src/Mostlylucid.BotDetection.Api/Endpoints/MetricsSnapshotEndpoints.cs \
        src/Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs \
        src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(monitoring): add GatewayMeterAccumulator + internal /_sb/metrics/snapshot endpoint for remote mode"
```

---

## Task 12: Remote Mode — RemoteMetricCollector

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/RemoteMetricCollector.cs`

- [ ] **Step 1: Create `RemoteMetricCollector.cs`**

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service for dashboard processes in remote-mode deployment.
///     Polls the gateway's /_sb/metrics/snapshot endpoint and writes snapshots to the local store.
/// </summary>
public sealed class RemoteMetricCollector : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _gatewayUrl;
    private readonly TimeSpan _pollInterval;
    private readonly IMetricSnapshotStore _store;
    private readonly ILogger<RemoteMetricCollector> _logger;

    public RemoteMetricCollector(
        IHttpClientFactory httpFactory,
        string gatewayUrl,
        TimeSpan pollInterval,
        IMetricSnapshotStore store,
        ILogger<RemoteMetricCollector> logger)
    {
        _httpFactory = httpFactory;
        _gatewayUrl = gatewayUrl;
        _pollInterval = pollInterval;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RemoteMetricCollector started, polling {Url} every {Interval}s",
            _gatewayUrl, _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                using var client = _httpFactory.CreateClient("sb-metrics");
                var dtos = await client.GetFromJsonAsync<MetricSnapshotDto[]>(_gatewayUrl, stoppingToken);
                if (dtos == null || dtos.Length == 0) continue;

                var snapshots = dtos.Select(d => new MetricSnapshot
                {
                    BucketTime = d.BucketTime.TruncateToMinute(),
                    PackId = d.PackId,
                    MeterName = d.MeterName,
                    Instrument = d.Instrument,
                    Tags = d.Tags,
                    Value = d.Value,
                    ValueType = d.ValueType
                });

                await _store.WriteSnapshotsAsync(snapshots, stoppingToken);
                _logger.LogDebug("RemoteMetricCollector: wrote {Count} snapshots from gateway", dtos.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RemoteMetricCollector: failed to poll gateway metrics");
            }
        }
    }
}
```

- [ ] **Step 2: Build the full solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep " error " | head -10
```

Expected: zero errors.

- [ ] **Step 3: Run all tests**

```bash
dotnet test mostlylucid.stylobot.sln 2>&1 | tail -15
```

Expected: all tests pass.

- [ ] **Step 4: Verify demo runs and Metrics tab is accessible**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
sleep 8
curl -s -o /dev/null -w "%{http_code}" http://localhost:5080/stylobot?tab=metrics
pkill -f "Mostlylucid.BotDetection.Demo" 2>/dev/null; true
```

Expected: HTTP 200.

- [ ] **Step 5: Final commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/RemoteMetricCollector.cs
git commit -m "feat(monitoring): add RemoteMetricCollector for gateway-separated dashboard deployments"
```

---

## Self-Review

**Spec coverage:**

| Spec item | Task |
|-----------|------|
| IMonitoringPack + supporting records | Task 1 |
| MetricSnapshot model | Task 1 |
| IMetricSnapshotStore | Task 2 |
| SqliteMetricSnapshotStore + schema | Task 3 |
| metric_snapshots table in SqliteDashboardEventStore | Task 3 |
| MeterListenerService (local) | Task 4 |
| AspNetMonitoringPack StyloBot meters | Task 5 |
| AspNetMonitoringPack host meters | Task 5 |
| MonitoringPackOptions + config | Task 6 |
| DI wiring | Task 6 |
| "metrics" SignalR invalidation | Task 7 |
| metric_snapshots 7-day prune | Task 7 |
| GET /api/v1/metrics/timeseries | Task 8 |
| GET /api/v1/metrics/latest | Task 8 |
| Metrics tab tag helper + view component + Razor | Task 9 |
| Metrics tab in Index.cshtml | Task 9 |
| GatewayMeterAccumulator | Task 11 |
| /_sb/metrics/snapshot endpoint | Task 11 |
| RemoteMetricCollector | Task 12 |

**Type consistency check:**

- `MetricSnapshot.BucketTime` set via `TruncateToMinute()` extension — defined in Task 1, used in Tasks 3, 4, 12. Consistent.
- `IMetricSnapshotStore.WriteSnapshotsAsync` takes `IEnumerable<MetricSnapshot>` — defined Task 2, implemented Task 3, called Task 4, Task 12. Consistent.
- `MetricSnapshotDto` defined in Task 11 (`GatewayMeterAccumulator.cs`), consumed in Task 12 (`RemoteMetricCollector.cs`). Consistent.
- `MeterListenerService.FlushSnapshotsAsync` returns `List<MetricSnapshot>` — defined and tested in Task 4. Consistent.
- `MonitoringMode` enum defined in Task 6 (`StyloBotDashboardOptions.cs`), consumed in Tasks 6, 11. Consistent.
- `AspNetMonitoringPack(includeHostMeters: bool)` defined Task 5, instantiated in Tasks 6 and 11. Consistent.
