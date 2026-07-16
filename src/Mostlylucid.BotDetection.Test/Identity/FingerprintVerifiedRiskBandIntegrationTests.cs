using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Integration regression guard for the EXACT failure mode behind #115: a verified
///     good bot displayed "VeryHigh" risk on the dashboard.
///     <para>
///     Root cause: the risk band was STORED (<c>cached_risk_band = BucketRisk(probability)</c>),
///     which bypasses the composer's verified -> Low friendly-pin, so a verified bot at
///     probability 1.0 had <c>BucketRisk(1.0)=VeryHigh</c> persisted and every reader served it.
///     </para>
///     <para>
///     Unlike the unit-level <c>FingerprintRiskProjectionTests</c> (which injects a
///     <see cref="Fingerprint"/> straight into <see cref="FingerprintRiskProjection"/>), this
///     drives the REAL store: a fingerprint is inserted, verified via the real
///     <see cref="SqliteFingerprintStore.UpdateClaimVerificationAsync"/> flow, given a
///     probability-1.0 verdict via the real write-behind hot path, and read back through the
///     REAL dashboard read-through boundary
///     <see cref="SqliteFingerprintStore.GetResolvedVerdictsBySignaturesAsync"/> -- the exact
///     projection <c>SignatureAggregateCache.ApplyResolvedVerdicts</c> consumes to render the
///     dashboard. Injected-value tests give false confidence on projection paths; this exercises
///     the whole path end to end.
///     </para>
/// </summary>
public class FingerprintVerifiedRiskBandIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    public FingerprintVerifiedRiskBandIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-verified-riskband-{Guid.NewGuid():N}");
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
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(string id)
    {
        var now = DateTime.UtcNow;
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[Dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 12,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "bot",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task VerifiedGoodBot_atProbabilityOne_readsLow_throughRealReadThrough_notVeryHigh()
    {
        var store = await NewStoreAsync();
        const string primarySig = "sig-verified-goodbot";
        const string fpId = "fp-verified-goodbot";

        // 1. Allocate the fingerprint (sets the primarySig -> fpId binding).
        await store.InsertFingerprintAsync(NewFingerprint(fpId), primarySig, CancellationToken.None);

        // 2. REAL verification flow: the verifier confirmed the identity (e.g. Googlebot via
        //    forward-DNS), so claim_status becomes 'verified'.
        await store.UpdateClaimVerificationAsync(
            fpId, "verified", "forward_dns", DateTime.UtcNow, CancellationToken.None);

        // 3. Load the verified row into the LFU dict so the write-behind hot path preserves
        //    claim_status while it stamps the probability.
        await store.GetFingerprintAsync(fpId, CancellationToken.None);

        // 4. VerifiedGoodBot early-exit lands probability 1.0 (the case that used to store VeryHigh).
        store.RecordVerdictWriteBehind(fpId, botProbability: 1.0, botType: BotType.GoodBot.ToString());

        // 5. REAL dashboard read-through boundary.
        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { primarySig }, CancellationToken.None);

        Assert.True(verdicts.ContainsKey(primarySig));
        var v = verdicts[primarySig];
        Assert.Equal(1.0, v.BotProbability);
        Assert.True(v.IsVerifiedBot);
        // THE EXACT FAILURE MODE: before #115 this projected "VeryHigh" (BucketRisk(1.0));
        // the verified -> Low friendly-pin must fire at read.
        Assert.Equal("Low", v.RiskBand);
        Assert.NotEqual("VeryHigh", v.RiskBand);
        Assert.Equal("None", v.ThreatBand);
    }

    [Fact]
    public async Task UnverifiedBot_atProbabilityOne_readsVeryHigh_throughRealReadThrough()
    {
        // Parity guard: without the verified latch, a probability-1.0 bot must still bucket
        // VeryHigh. Proves the friendly-pin is gated on verification (no sensitivity traded
        // away) AND that this integration test would actually catch a regression that flipped
        // the friendly bot back to VeryHigh.
        var store = await NewStoreAsync();
        const string primarySig = "sig-unverified-bot";
        const string fpId = "fp-unverified-bot";

        await store.InsertFingerprintAsync(NewFingerprint(fpId), primarySig, CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None);
        store.RecordVerdictWriteBehind(fpId, botProbability: 1.0, botType: BotType.AiBot.ToString());

        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { primarySig }, CancellationToken.None);

        Assert.True(verdicts.ContainsKey(primarySig));
        var v = verdicts[primarySig];
        Assert.False(v.IsVerifiedBot);
        Assert.Equal("VeryHigh", v.RiskBand);
    }
}
