using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Task 3 of the Identity Async Un-Drift plan: pin the contract that
///     <see cref="IFingerprintStore.ObservationAppended"/> fires after a
///     successful <see cref="IFingerprintStore.RecordObservationAsync"/> durable
///     write commits, and that the event carries the correct fingerprint id.
/// </summary>
public class ObservationAppendedEventTests : IDisposable
{
    private readonly string _tempDir;

    public ObservationAppendedEventTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-obs-event-{Guid.NewGuid():N}");
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
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(string id, int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task RecordObservation_RaisesObservationAppendedWithFingerprintId()
    {
        var store = await NewStoreAsync();
        const string fpId = "fp-1";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), "sig-1", CancellationToken.None);

        string? capturedId = null;
        store.ObservationAppended += id => capturedId = id;

        await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);

        Assert.Equal(fpId, capturedId);
    }

    [Fact]
    public async Task Observation_is_memory_only_no_rows_no_scope_persistence()
    {
        // Phase B (write-path grain redesign): the observation feed is MEMORY-ONLY — no
        // durable row, no scope persistence (the row was the scope's home). The
        // fingerprint's evolution folds in memory on the request thread.
        var store = await NewStoreAsync();
        const string fpId = "fp-scope";
        var dim = store.Layout.Dimension;
        var fp = NewFingerprint(fpId, dim) with { ObservationCount = 100 };
        await store.InsertFingerprintAsync(fp, "sig-scope", CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None); // warm LFU (the fold needs a resident entry)

        var scope = new RequestScope("acme.com", "www.acme.com");
        await store.RecordObservationAsync(scope, fpId, new float[dim], ct: CancellationToken.None);

        // The absorb picker finds nothing — there are no rows to absorb.
        var rows = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 30, activeWindowDays: 365, maxFingerprints: 0, CancellationToken.None);
        Assert.Empty(rows);

        // But the observation still folded: the in-memory count advanced.
        var evolved = await store.GetFingerprintAsync(fpId, CancellationToken.None);
        Assert.True(evolved!.ObservationCount >= 101, "the memory fold advances the observation count");
    }

    [Fact]
    public async Task ObservationAppended_FiresAfterTheMemoryFoldCompletes()
    {
        // Phase B: the event fires AFTER the in-memory fold — inside the handler the
        // fingerprint's evolution (observation count) must already be visible in the LFU.
        var store = await NewStoreAsync();
        const string fpId = "fp-2";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), "sig-2", CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None); // warm LFU

        long? countInsideHandler = null;
        store.ObservationAppended += id =>
        {
            // The LFU read is synchronous; block on it because the handler contract
            // requires sync invocation on the call-site thread.
            countInsideHandler = store
                .GetFingerprintAsync(id, CancellationToken.None)
                .GetAwaiter()
                .GetResult()?.ObservationCount;
        };

        await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);

        // The fold (count advance) must already be visible when the handler ran.
        Assert.True(countInsideHandler.HasValue, "handler was never invoked");
        Assert.True(countInsideHandler!.Value >= 2,
            "the in-memory fold must complete before ObservationAppended fires");
    }
}
