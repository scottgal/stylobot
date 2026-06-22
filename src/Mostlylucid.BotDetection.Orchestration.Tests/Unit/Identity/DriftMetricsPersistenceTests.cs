using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Round-trip coverage for the SQLite drift-metrics persistence: the table
///     is created by the existing identity_core.sql, the insert path is
///     idempotent on <c>(archetype_id, ua_family, calibrated_at)</c>, and
///     <see cref="SqliteFingerprintStore.ListLatestDriftMetricsAsync"/> returns
///     rows from the most-recent cycle only. See spec §2.B.
/// </summary>
public sealed class DriftMetricsPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public DriftMetricsPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-drift-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> BuildStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions { Enabled = true },
        });
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    [Fact]
    public async Task Insert_then_list_returns_the_persisted_rows()
    {
        var store = await BuildStoreAsync();
        var cycle = new DateTime(2026, 6, 21, 13, 0, 0, DateTimeKind.Utc);
        var batch = new[]
        {
            Metric("chrome-desktop", "Chrome",    matches: true,  count: 5, mean: 0.2, var_: 0.01, p90: 0.3, at: cycle),
            Metric("chrome-desktop", "Googlebot", matches: false, count: 2, mean: 1.5, var_: 0.10, p90: 1.8, at: cycle),
            Metric("googlebot",      "Googlebot", matches: true,  count: 3, mean: 0.1, var_: 0.00, p90: 0.1, at: cycle),
        };
        await store.InsertDriftMetricsAsync(batch, CancellationToken.None);

        var listed = await store.ListLatestDriftMetricsAsync(CancellationToken.None);
        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, m => m.ArchetypeId == "chrome-desktop" && m.UaFamily == "Chrome" && m.MatchesAssertedUa);
        Assert.Contains(listed, m => m.ArchetypeId == "chrome-desktop" && m.UaFamily == "Googlebot" && !m.MatchesAssertedUa);
        Assert.Contains(listed, m => m.ArchetypeId == "googlebot" && m.UaFamily == "Googlebot" && m.MatchesAssertedUa);
    }

    [Fact]
    public async Task Latest_only_filter_excludes_older_calibration_rows()
    {
        var store = await BuildStoreAsync();

        var olderCycle = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var newerCycle = new DateTime(2026, 6, 21, 13, 0, 0, DateTimeKind.Utc);

        await store.InsertDriftMetricsAsync(new[]
        {
            Metric("a", "X", matches: false, count: 1, mean: 0.5, var_: 0, p90: 0.5, at: olderCycle),
            Metric("a", "Y", matches: false, count: 1, mean: 0.5, var_: 0, p90: 0.5, at: olderCycle),
        }, CancellationToken.None);
        await store.InsertDriftMetricsAsync(new[]
        {
            Metric("a", "X", matches: false, count: 3, mean: 0.2, var_: 0, p90: 0.2, at: newerCycle),
        }, CancellationToken.None);

        var listed = await store.ListLatestDriftMetricsAsync(CancellationToken.None);

        // Endpoint shows the newest cycle only -- one row.
        var single = Assert.Single(listed);
        Assert.Equal(newerCycle, single.CalibratedAt);
        Assert.Equal(3, single.DescendantCount);
    }

    [Fact]
    public async Task Same_key_with_same_calibrated_at_is_upserted()
    {
        // Pin: the (archetype_id, ua_family, calibrated_at) PK plus the ON
        // CONFLICT DO UPDATE clause means a re-run of the same cycle replaces
        // the row in place. Guards against duplicate-key crashes if the
        // calibration loop is interrupted and rerun on the same heartbeat.
        var store = await BuildStoreAsync();
        var cycle = new DateTime(2026, 6, 21, 13, 0, 0, DateTimeKind.Utc);

        await store.InsertDriftMetricsAsync(new[]
        {
            Metric("a", "X", matches: false, count: 1, mean: 1.0, var_: 0, p90: 1.0, at: cycle),
        }, CancellationToken.None);

        // Same key, updated values.
        await store.InsertDriftMetricsAsync(new[]
        {
            Metric("a", "X", matches: true, count: 7, mean: 0.2, var_: 0.01, p90: 0.3, at: cycle),
        }, CancellationToken.None);

        var listed = await store.ListLatestDriftMetricsAsync(CancellationToken.None);
        var only = Assert.Single(listed);
        Assert.True(only.MatchesAssertedUa);
        Assert.Equal(7, only.DescendantCount);
        Assert.Equal(0.2, only.MeanL2ToCentroid, precision: 6);
    }

    [Fact]
    public async Task Empty_batch_is_a_noop()
    {
        var store = await BuildStoreAsync();
        await store.InsertDriftMetricsAsync(Array.Empty<ArchetypeDriftMetric>(), CancellationToken.None);
        var listed = await store.ListLatestDriftMetricsAsync(CancellationToken.None);
        Assert.Empty(listed);
    }

    private static ArchetypeDriftMetric Metric(
        string archetype, string ua, bool matches, int count,
        double mean, double var_, double p90, DateTime at) => new()
    {
        ArchetypeId = archetype,
        UaFamily = ua,
        MatchesAssertedUa = matches,
        DescendantCount = count,
        MeanL2ToCentroid = mean,
        VarianceL2ToCentroid = var_,
        P90L2ToCentroid = p90,
        CalibratedAt = at,
    };
}
