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
    public async Task Observation_persists_scope_and_read_path_hydrates_it()
    {
        // Multi-domain contract: RecordObservationAsync(RequestScope, ...) tags the
        // row with (domain, host); the ListAbsorbableObservationsAsync read path
        // must hydrate them back onto AbsorbableObservation so downstream
        // per-site aggregators can partition by owner.
        var store = await NewStoreAsync();
        const string fpId = "fp-scope";
        var dim = store.Layout.Dimension;
        // Insert with observation_count meeting the maturity threshold so the
        // absorb picker returns the row on first pass.
        var fp = NewFingerprint(fpId, dim) with { ObservationCount = 100 };
        await store.InsertFingerprintAsync(fp, "sig-scope", CancellationToken.None);

        var scope = new RequestScope("acme.com", "www.acme.com");
        await store.RecordObservationAsync(scope, fpId, new float[dim], ct: CancellationToken.None);

        // Materialise the read path -- absorb picker returns all rows on active
        // fingerprints past the maturity threshold.
        var rows = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 30, activeWindowDays: 365, maxFingerprints: 0, CancellationToken.None);
        var row = Assert.Single(rows, r => r.FingerprintId == fpId);
        Assert.Equal("acme.com", row.Domain);
        Assert.Equal("www.acme.com", row.Host);
    }

    [Fact]
    public async Task ObservationAppended_FiresAfterDurableWriteCommits()
    {
        // Verify the event fires AFTER the SQL commit: inside the handler the row
        // must already be visible via GetUnabsorbedObservationCountAsync.
        var store = await NewStoreAsync();
        const string fpId = "fp-2";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), "sig-2", CancellationToken.None);

        int? countInsideHandler = null;
        store.ObservationAppended += id =>
        {
            // GetUnabsorbedObservationCountAsync is async; block synchronously because
            // the handler contract requires sync invocation on the call-site thread.
            countInsideHandler = store
                .GetUnabsorbedObservationCountAsync(id, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        };

        await store.RecordObservationAsync(RequestScope.Unknown, fpId, new float[dim], ct: CancellationToken.None);

        // The observation row must already exist (count >= 1) when the handler ran.
        Assert.True(countInsideHandler.HasValue, "handler was never invoked");
        Assert.True(countInsideHandler!.Value >= 1,
            "observation row must be durable before ObservationAppended fires");
    }
}
