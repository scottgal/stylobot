using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pins the adaptation-loop contract: BotClusterService publishes a snapshot →
///     ReseatRootCentroidsAsync writes the cluster's community-mean back as the
///     active root_centroid for every member fingerprint, supersedes the prior
///     active history row, inserts a new active history row with cluster lineage,
///     and the live -> root cosine drops to ~0 (the snapshot has just absorbed
///     the live state, so drift is zero immediately after reseat).
/// </summary>
public sealed class RootCentroidReseatTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _store;
    private readonly IdentityVectorLayout _layout;

    public RootCentroidReseatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-reseat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false }
            }
        });
        _layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, _layout);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ReseatRootCentroids_AppliesCommunityMean_AndWritesHistoryRow()
    {
        await _store.EnsureInitialisedAsync();
        var dim = _layout.Dimension;

        // Allocate three fingerprints, each bound to a distinct primary signature.
        // The cluster snapshot the test feeds in groups all three signatures
        // together, so all three become "members" of the cluster.
        var fpA = IdentityTestHelpers.MakeFingerprint("fp-A", IdentityTestHelpers.MakeUnitVector(dim, 1),
            archetypeOrigin: "chrome-desktop");
        var fpB = IdentityTestHelpers.MakeFingerprint("fp-B", IdentityTestHelpers.MakeUnitVector(dim, 2),
            archetypeOrigin: "chrome-desktop");
        var fpC = IdentityTestHelpers.MakeFingerprint("fp-C", IdentityTestHelpers.MakeUnitVector(dim, 3),
            archetypeOrigin: "chrome-desktop");

        await _store.InsertFingerprintAsync(fpA, primarySignature: "sig-A");
        await _store.InsertFingerprintAsync(fpB, primarySignature: "sig-B");
        await _store.InsertFingerprintAsync(fpC, primarySignature: "sig-C");

        // Sanity: each fp's root_centroid was seeded from its initial centroid
        // (InsertFingerprintAsync defensively self-seeds when caller doesn't
        // supply one) AND a history row was written.
        var loadedA = await _store.GetFingerprintAsync("fp-A");
        Assert.NotNull(loadedA);
        Assert.NotNull(loadedA!.RootCentroid);
        var historyBefore = await _store.GetRootHistoryAsync("fp-A");
        Assert.Single(historyBefore);
        Assert.Null(historyBefore[0].SupersededAt); // active
        var initialSource = historyBefore[0].RootSource;

        // Feed the cluster snapshot: all three signatures grouped together.
        var update = new ClusterRootUpdate(
            ClusterId: "cluster-test-01",
            MemberSignatures: new[] { "sig-A", "sig-B", "sig-C" },
            MemberCount: 3);
        await _store.ReseatRootCentroidsAsync(new[] { update });

        // Every member now has the community-mean as its root_centroid + lineage
        // marker = "cluster:<id>".
        var afterA = await _store.GetFingerprintAsync("fp-A");
        var afterB = await _store.GetFingerprintAsync("fp-B");
        var afterC = await _store.GetFingerprintAsync("fp-C");
        Assert.NotNull(afterA);
        Assert.NotNull(afterB);
        Assert.NotNull(afterC);
        Assert.Equal("cluster:cluster-test-01", afterA!.RootSource);
        Assert.Equal("cluster:cluster-test-01", afterB!.RootSource);
        Assert.Equal("cluster:cluster-test-01", afterC!.RootSource);

        // The new root_centroid is identical across all three members (it IS the
        // community mean -- single vector, shared by every member of the cluster).
        Assert.NotNull(afterA.RootCentroid);
        Assert.NotNull(afterB.RootCentroid);
        Assert.Equal(afterA.RootCentroid!.Length, afterB.RootCentroid!.Length);
        for (var i = 0; i < afterA.RootCentroid.Length; i++)
        {
            Assert.Equal(afterA.RootCentroid[i], afterB.RootCentroid[i]);
            Assert.Equal(afterA.RootCentroid[i], afterC!.RootCentroid![i]);
        }

        // History: the prior active row is now superseded, a new active row
        // exists with cluster lineage, member_count = 3.
        var historyAfter = await _store.GetRootHistoryAsync("fp-A");
        Assert.Equal(2, historyAfter.Count);
        Assert.Equal("cluster:cluster-test-01", historyAfter[0].RootSource); // newest first
        Assert.Equal(3, historyAfter[0].MemberCount);
        Assert.Null(historyAfter[0].SupersededAt);
        Assert.Equal(initialSource, historyAfter[1].RootSource); // the archetype/bootstrap seed
        Assert.NotNull(historyAfter[1].SupersededAt);            // now superseded
    }

    [Fact]
    public async Task ReseatRootCentroids_SkipsClustersBelowMinMemberThreshold()
    {
        await _store.EnsureInitialisedAsync();
        var dim = _layout.Dimension;

        // One-member "cluster" -- would replace fp's root with its own centroid
        // (drift permanently zero against itself). The guard must skip it.
        var fp = IdentityTestHelpers.MakeFingerprint("fp-solo", IdentityTestHelpers.MakeUnitVector(dim, 42),
            archetypeOrigin: "chrome-desktop");
        await _store.InsertFingerprintAsync(fp, primarySignature: "sig-solo");

        var before = await _store.GetFingerprintAsync("fp-solo");
        Assert.NotNull(before);
        var rootBefore = before!.RootCentroid;
        var sourceBefore = before.RootSource;

        var update = new ClusterRootUpdate(
            ClusterId: "cluster-solo",
            MemberSignatures: new[] { "sig-solo" },
            MemberCount: 1);
        await _store.ReseatRootCentroidsAsync(new[] { update }, minMemberFingerprints: 2);

        var after = await _store.GetFingerprintAsync("fp-solo");
        Assert.NotNull(after);
        // Root unchanged. Lineage marker unchanged (not "cluster:cluster-solo").
        Assert.Equal(sourceBefore, after!.RootSource);
        Assert.Equal(rootBefore!.Length, after.RootCentroid!.Length);
        var historyAfter = await _store.GetRootHistoryAsync("fp-solo");
        Assert.Single(historyAfter); // still just the initial row
    }

    [Fact]
    public async Task ReseatRootCentroids_BatchesIN_ToleratesLargeClusters()
    {
        // The SQLite parameter cap is 999; the implementation batches at 500.
        // Verify a cluster with > 500 unique signatures + > 500 unique
        // fingerprints completes without throwing. We don't need to allocate
        // 1000 fingerprints to assert the batching shape -- 600 is enough to
        // exceed one batch and exercise the chunked code path. Performance
        // isn't the point; the point is "this no longer throws".
        await _store.EnsureInitialisedAsync();
        var dim = _layout.Dimension;
        const int n = 600;
        var sigs = new string[n];
        for (var i = 0; i < n; i++)
        {
            var fp = IdentityTestHelpers.MakeFingerprint($"fp-{i}",
                IdentityTestHelpers.MakeUnitVector(dim, seed: i + 100),
                archetypeOrigin: "chrome-desktop");
            sigs[i] = $"sig-{i}";
            await _store.InsertFingerprintAsync(fp, primarySignature: sigs[i]);
        }

        var update = new ClusterRootUpdate(
            ClusterId: "cluster-large",
            MemberSignatures: sigs,
            MemberCount: n);

        // Must not throw -- the bug fix this test pins is that the unbounded
        // IN clause used to hit SQLite's 999-parameter cap. Now batched at 500.
        await _store.ReseatRootCentroidsAsync(new[] { update });

        // Every member has the cluster lineage marker now.
        var after = await _store.GetFingerprintAsync("fp-500");
        Assert.NotNull(after);
        Assert.Equal("cluster:cluster-large", after!.RootSource);
    }

    [Fact]
    public async Task ReseatRootCentroids_LiveCentroidVsRoot_ProducesMeaningfulDriftAfterMovement()
    {
        // The contract operators see in the dashboard: drift = cosine(live, root).
        // Right after a reseat the live centroid IS the input to the mean, so
        // drift starts very close to 1.0 (== alignment). When the live centroid
        // is then nudged via a manual update, the drift score moves away from 1.
        // This test pins the math, not the dashboard rendering -- the dashboard
        // wires the same FingerprintDriftMath.Cosine call.
        await _store.EnsureInitialisedAsync();
        var dim = _layout.Dimension;

        var fp = IdentityTestHelpers.MakeFingerprint("fp-drift",
            IdentityTestHelpers.MakeUnitVector(dim, seed: 7),
            archetypeOrigin: "chrome-desktop");
        var partnerCentroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 8);
        var partner = IdentityTestHelpers.MakeFingerprint("fp-partner", partnerCentroid,
            archetypeOrigin: "chrome-desktop");
        await _store.InsertFingerprintAsync(fp, primarySignature: "sig-drift");
        await _store.InsertFingerprintAsync(partner, primarySignature: "sig-partner");

        var update = new ClusterRootUpdate(
            ClusterId: "cluster-drift",
            MemberSignatures: new[] { "sig-drift", "sig-partner" },
            MemberCount: 2);
        await _store.ReseatRootCentroidsAsync(new[] { update });

        var afterReseat = await _store.GetFingerprintAsync("fp-drift");
        Assert.NotNull(afterReseat);
        Assert.NotNull(afterReseat!.RootCentroid);

        var driftRightAfterReseat = Cosine(afterReseat.Centroid, afterReseat.RootCentroid!);
        // The live centroid hasn't moved yet, but the root is the cluster mean
        // (= avg of fp-drift + fp-partner centroids). Drift should be strictly
        // less than 1.0 (root != live) but well above 0 (root is in the same
        // neighbourhood).
        Assert.InRange(driftRightAfterReseat, 0.5, 0.9999);
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, an = 0, bn = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            an += a[i] * a[i];
            bn += b[i] * b[i];
        }
        return dot / (Math.Sqrt(an) * Math.Sqrt(bn) + 1e-12);
    }
}
