using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Overview-ratified 4th site fix: absorb writes in
///     <see cref="SqliteFingerprintStore.AbsorbObservationAsync"/> must be
///     serialized through the existing single-writer name-drainer channel rather
///     than each opening its own concurrent SQLite write connection.
///
///     Two tests:
///     1. <see cref="AbsorbObservationAsync_ReturnsSynchronously_ProveingFireAndForget"/>
///        -- the method must return a synchronously-completed task (channel
///        enqueue, not a direct DB write). Fails RED before the fix because the
///        current implementation does an async SQLite transaction.
///     2. <see cref="ConcurrentAbsorbObservationCalls_AllPersistedWithoutError"/>
///        -- N concurrent enqueues all land durably via the single drainer;
///        no SqliteException thrown; all observation rows marked absorbed.
/// </summary>
public sealed class SqliteFingerprintStoreAbsorbDrainerTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteFingerprintStoreAbsorbDrainerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-absorb-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, $"botdetection-{Guid.NewGuid():N}.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90
                }
            }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static async Task<string> SeedFingerprintAsync(
        SqliteFingerprintStore store, string fpId)
    {
        var dim = store.Layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 100, // high enough to meet maturity threshold
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
        return fpId;
    }

    /// <summary>
    ///     RED test: before the fix, <see cref="SqliteFingerprintStore.AbsorbObservationAsync"/>
    ///     performs an async SQLite transaction and returns a non-completed task.
    ///     After the fix it enqueues to the drainer channel and returns
    ///     <see cref="Task.CompletedTask"/> synchronously.
    /// </summary>
    [Fact]
    public async Task AbsorbObservationAsync_ReturnsSynchronously_ProveingFireAndForget()
    {
        var store = await NewStoreAsync();
        var dim = store.Layout.Dimension;
        const string fpId = "fp-drain-1";
        await SeedFingerprintAsync(store, fpId);

        // Record one observation and get its id via ListAbsorbableObservations.
        await store.RecordObservationAsync(
            RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);
        var absorbable = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 30, activeWindowDays: 365, CancellationToken.None);
        var obs = Assert.Single(absorbable, o => o.FingerprintId == fpId);

        var centroid = new float[dim];
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);

        // Capture the task BEFORE awaiting it. If the implementation is fire-and-forget
        // (channel enqueue), the task will be synchronously completed. If it is a direct
        // DB write it will be an incomplete async task.
        var task = store.AbsorbObservationAsync(
            obs.ObservationId, fpId, centroid, obs.CentroidMaturity + 1, weights);

        // FAILS RED before fix: direct async DB write returns non-completed task.
        // PASSES GREEN after fix: channel enqueue returns Task.CompletedTask.
        task.IsCompleted.Should().BeTrue(
            "AbsorbObservationAsync must enqueue to the single-writer drainer channel " +
            "and return synchronously, not perform a direct SQLite write");
    }

    /// <summary>
    ///     Correctness: N concurrent <see cref="SqliteFingerprintStore.AbsorbObservationAsync"/>
    ///     calls all complete without a SqliteException and the single drainer
    ///     persists every one durably (all observation rows marked absorbed).
    /// </summary>
    [Fact]
    public async Task ConcurrentAbsorbObservationCalls_AllPersistedWithoutError()
    {
        var store = await NewStoreAsync();
        var dim = store.Layout.Dimension;
        const int n = 20;

        // Seed N fingerprints each with one observation.
        for (var i = 0; i < n; i++)
            await SeedFingerprintAsync(store, $"fp-concurrent-{i}");

        for (var i = 0; i < n; i++)
            await store.RecordObservationAsync(
                RequestScope.Unknown, $"fp-concurrent-{i}", new float[dim], ct: CancellationToken.None);

        // Retrieve all absorbable observations.
        var absorbable = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 30, activeWindowDays: 365, CancellationToken.None);
        absorbable.Count.Should().BeGreaterThanOrEqualTo(n);

        var centroid = new float[dim];
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);

        // Fire all AbsorbObservationAsync calls concurrently -- no exceptions must escape.
        var tasks = absorbable
            .Where(o => o.FingerprintId.StartsWith("fp-concurrent-"))
            .Select(obs => store.AbsorbObservationAsync(
                obs.ObservationId, obs.FingerprintId,
                centroid, obs.CentroidMaturity + 1, weights));

        Func<Task> allAbsorbs = () => Task.WhenAll(tasks);
        await allAbsorbs.Should().NotThrowAsync("single-writer drainer serializes writes; no SQLite BUSY");

        // Wait for the drainer to process all enqueued writes. Poll until all N
        // absorbWriteCount increments have fired, with a 10-second hard ceiling.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.AbsorbWriteCount < n && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        store.AbsorbWriteCount.Should().BeGreaterThanOrEqualTo(n,
            "drainer must process all enqueued absorb writes before deadline");

        // Verify all observation rows are durably marked absorbed.
        for (var i = 0; i < n; i++)
        {
            var pending = await store.GetUnabsorbedObservationCountAsync(
                $"fp-concurrent-{i}", CancellationToken.None);
            pending.Should().Be(0, $"fp-concurrent-{i} must have 0 pending observations after drainer flush");
        }
    }
}
