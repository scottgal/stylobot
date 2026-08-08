using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     CROSS-PATH regression guard for the operator P0 fixed in <c>61032c89</c>
///     ("Very Low Risk Profile: 98% bot probability").
///
///     <para>
///         <b>Why a band assertion is not enough.</b> The defect was never a wrong band in
///         isolation — both values were individually defensible. It was TWO PATHS DISAGREEING:
///         <see cref="SignatureAggregateCache.WarmFromDetections"/> seeded a verdict whose
///         RiskBand was the MODE of the detection window while its BotProbability was the
///         sticky MAX of that same window, and the fingerprint read-through
///         (<c>SqliteFingerprintStore.GetResolvedVerdictsBySignaturesAsync</c>) derived a band
///         from the raw facts. A test that only checks "band is high when probability is high"
///         passes on a rebuilt mode-beside-max, because the mode is usually right. Only
///         comparing the two paths catches it.
///     </para>
///
///     <para>
///         <b>What this asserts.</b> For the SAME facts, the warm seed and the real read-through
///         must produce the SAME RiskBand — so a warmed row cannot visibly change when
///         <c>ApplyResolvedVerdicts</c> supersedes it, and cannot contradict the probability
///         printed beside it. Both sides are driven for real: a genuine
///         <see cref="SqliteFingerprintStore"/> on disk, and a genuine
///         <see cref="SignatureAggregateCache"/>.
///     </para>
///
///     <para>
///         <b>This test fails on the pre-fix code.</b> The window below is deliberately shaped
///         so mode != max: nine low-signal rows (band VeryLow) behind one identifying hit at
///         0.98. Pre-fix, the warm seed reported VeryLow while the read-through reported
///         VeryHigh — exactly the operator's screenshot.
///     </para>
/// </summary>
public class WarmSeedReadThroughAgreementTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    private const string PrimarySig = "sig-warm-vs-readthrough";
    private const string FpId = "fp-warm-vs-readthrough";
    private const double IdentifyingProbability = 0.98;
    private const double Confidence = 0.9;
    private const string CatalogueBotType = "SearchEngine";

    public WarmSeedReadThroughAgreementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"warm-readthrough-{Guid.NewGuid():N}");
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

    private static Fingerprint NewFingerprint()
    {
        var now = DateTime.UtcNow;
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = FpId,
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
            // Must match the Confidence carried on the detection rows below, or the two
            // paths are being asked to derive from different facts and any agreement
            // would be luck.
            InferredTypeConfidence = Confidence,
            InferredTypeChangedAt = now
        };
    }

    /// <summary>
    ///     The asset-heavy crawl that produced the P0: mostly low-signal requests, one
    ///     identifying hit. detections[0] is the freshest — WarmFromDetections documents
    ///     timestamp DESC ordering and takes detections[0] as `latest`.
    /// </summary>
    private static List<DashboardDetectionEvent> AssetHeavyCrawlWindow()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DashboardDetectionEvent> { Detection(IdentifyingProbability, "VeryHigh", now) };
        for (var i = 1; i <= 9; i++)
            rows.Add(Detection(0.10, "VeryLow", now.AddSeconds(-i * 10)));
        return rows;
    }

    private static DashboardDetectionEvent Detection(double probability, string riskBand, DateTime ts) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = ts,
        IsBot = true,
        BotProbability = probability,
        BotType = CatalogueBotType,
        PrimarySignature = PrimarySig,
        RiskBand = riskBand,
        ThreatScore = 0.0,
        ThreatBand = "None",
        Confidence = Confidence,
        Method = "GET",
        Path = "/",
    };

    [Fact]
    public async Task Warm_seed_and_real_read_through_agree_on_the_risk_band_for_the_same_facts()
    {
        // ── Path A: the REAL fingerprint read-through ────────────────────────────
        var store = await NewStoreAsync();
        await store.InsertFingerprintAsync(NewFingerprint(), PrimarySig, CancellationToken.None);
        await store.GetFingerprintAsync(FpId, CancellationToken.None);
        store.RecordVerdictWriteBehind(FpId, IdentifyingProbability, CatalogueBotType);

        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { PrimarySig }, CancellationToken.None);
        Assert.True(verdicts.ContainsKey(PrimarySig), "read-through produced no verdict for the signature");
        var readThrough = verdicts[PrimarySig];

        // ── Path B: the warm seed from a detection window carrying the SAME facts ──
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.WarmFromDetections(PrimarySig, AssetHeavyCrawlWindow());
        var warmSeed = cache.GetResolvedVerdict(PrimarySig);
        Assert.NotNull(warmSeed);

        // Preconditions: both paths carry the same headline probability, so any band
        // disagreement below is a genuine cross-path defect and not different inputs.
        Assert.Equal(IdentifyingProbability, readThrough.BotProbability, precision: 3);
        Assert.Equal(IdentifyingProbability, warmSeed!.BotProbability, precision: 3);

        // ── THE INVARIANT ────────────────────────────────────────────────────────
        // Pre-fix this was ("VeryLow", "VeryHigh") — the warm seed reporting the window
        // MODE against a read-through derived from the raw facts. The row visibly changed
        // the moment ApplyResolvedVerdicts landed, and until then contradicted the
        // percentage printed next to it.
        Assert.Equal(readThrough.RiskBand, warmSeed.RiskBand);
    }

    /// <summary>
    ///     Guard on the guard: proves the window above genuinely has mode != max, so the
    ///     agreement asserted by the test is a real constraint rather than a coincidence of
    ///     a uniform window. If someone flattens this fixture, the test above stops being a
    ///     regression guard and this fails to say so.
    /// </summary>
    [Fact]
    public void Fixture_window_has_a_mode_that_disagrees_with_its_max()
    {
        var window = AssetHeavyCrawlWindow();

        var max = window.Max(d => d.BotProbability);
        var modeBand = window
            .GroupBy(d => d.RiskBand)
            .OrderByDescending(g => g.Count())
            .First().Key;

        Assert.Equal(IdentifyingProbability, max, precision: 3);
        Assert.Equal("VeryLow", modeBand);
    }
}
