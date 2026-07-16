using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Regression guard for the home "your detection" widget showing 0% bot probability.
///     <para>
///     The request DID run through detection, so the visitor's fingerprint carries the
///     in-flight verdict (its <c>CachedBotProbability</c>). When the in-flight verdict wasn't
///     carried across the viewer/YARP boundary in <c>HttpContext.Items</c> (ProcessingTimeMs
///     stays 0), the widget must read the headline THROUGH that fingerprint (the single source,
///     which <c>GetFingerprintAsync</c> never returns empty for -- it DB-reads on a cold-LFU
///     miss), never a 0% default and never a second event-store lookup.
///     </para>
/// </summary>
public class BotDetectionDetailsWidgetVerdictTests
{
    private static readonly IdentityVectorLayout Layout = IdentityVectorLayout.DefaultV1();

    private static Fingerprint MakeFingerprint(double cachedProb, string claimStatus = "unverified")
    {
        var weights = new float[Layout.Dimension];
        System.Array.Fill(weights, 1.0f);
        var now = System.DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = "fp-1",
            Centroid = new float[Layout.Dimension],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 12,
            CorrectionCount = 0,
            FirstSeen = now.AddHours(-1),
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "bot",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = cachedProb,
            CachedScoreUpdatedAt = now,   // has a verdict -> eligible to headline
            ClaimStatus = claimStatus,
        };
    }

    private static BotDetectionDetailsViewComponent NewComponent(IFingerprintReader reader)
    {
        var encoder = new IdentityVectorEncoder(Layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        return new BotDetectionDetailsViewComponent(new DetectionDataExtractor(), reader, archetypes, Layout);
    }

    private static void SetHttpContext(ViewComponent vc, HttpContext ctx)
    {
        vc.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = ctx },
        };
    }

    private static DetectionDisplayModel ModelOf(IViewComponentResult result) =>
        (DetectionDisplayModel)((ViewViewComponentResult)result).ViewData!.Model!;

    [Fact]
    public async Task NoInflightContextVerdict_reads_headline_from_the_fingerprint_never_zero()
    {
        // No AggregatedEvidence / BotDetectionResult in context -> extractor leaves the headline
        // at ProcessingTimeMs=0 (the viewer/YARP-boundary shape). The fingerprint carries 0.9.
        var reader = new Mock<IFingerprintReader>(MockBehavior.Loose);
        reader.Setup(r => r.GetFingerprintAsync("fp-1", It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync(MakeFingerprint(cachedProb: 0.9));

        var vc = NewComponent(reader.Object);
        var ctx = new DefaultHttpContext();
        ctx.Items[SignalKeys.IdentityFingerprintId] = "fp-1";
        SetHttpContext(vc, ctx);

        var model = ModelOf(await vc.InvokeAsync());

        Assert.Equal(0.9, model.BotProbability); // was 0% before the fix
        Assert.True(model.IsBot);
        // DISPLAY GATE: the view renders the % iff HasVerdict. The fp path leaves ProcessingTimeMs=0,
        // so gating on ProcessingTimeMs (the 408c48ed regression) hid the number. HasVerdict must be
        // true here so the widget shows "90%", not "-".
        Assert.True(model.HasVerdict);
    }

    [Fact]
    public async Task VerifiedFingerprint_headline_derives_Low_band_not_veryhigh()
    {
        // A verified good bot at probability 1.0: the derived band must be Low (friendly-pin),
        // consistent with the rest of the surfaces (#115). Never a stored/naive VeryHigh.
        var reader = new Mock<IFingerprintReader>(MockBehavior.Loose);
        reader.Setup(r => r.GetFingerprintAsync("fp-1", It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync(MakeFingerprint(cachedProb: 1.0, claimStatus: "verified"));

        var vc = NewComponent(reader.Object);
        var ctx = new DefaultHttpContext();
        ctx.Items[SignalKeys.IdentityFingerprintId] = "fp-1";
        SetHttpContext(vc, ctx);

        var model = ModelOf(await vc.InvokeAsync());

        Assert.Equal(1.0, model.BotProbability);
        Assert.Equal("Low", model.RiskBand);
        Assert.True(model.HasVerdict); // the % renders
    }

    [Fact]
    public async Task ForwardedBotDetectionResult_viewer_host_shows_the_percent_never_dash()
    {
        // Viewer host behind YARP: no AggregatedEvidence (doesn't cross the boundary), fingerprint
        // cold (bypass-key DisableLearningWrites suppresses the cached write), BUT the gateway
        // forwarded the verdict -> context.Items["BotDetectionResult"] (header-hydrated). The hero
        // MUST show that %, never "-". (Regression deploy caught on staging after 8b47b1cb.)
        var reader = new Mock<IFingerprintReader>(MockBehavior.Loose); // GetFingerprintAsync -> null (cold)
        var vc = NewComponent(reader.Object);
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetectionResult"] = new BotDetectionResult { ConfidenceScore = 0.86, IsBot = true };
        SetHttpContext(vc, ctx);

        var model = ModelOf(await vc.InvokeAsync());

        Assert.True(model.HasVerdict);                        // the % renders, not "-"
        Assert.Equal(0.86, model.BotProbability, precision: 3);
    }

    [Fact]
    public async Task NoFingerprint_and_no_inflight_verdict_leaves_HasVerdict_false_so_view_shows_dash()
    {
        // Genuinely unhydrated: no fingerprint id, no in-flight verdict. HasVerdict must stay false
        // so the view shows "-" (not a fabricated 0%). This is the ONLY state that should show "-".
        var reader = new Mock<IFingerprintReader>(MockBehavior.Loose);
        var vc = NewComponent(reader.Object);
        var ctx = new DefaultHttpContext(); // no IdentityFingerprintId, no evidence
        SetHttpContext(vc, ctx);

        var model = ModelOf(await vc.InvokeAsync());

        Assert.False(model.HasVerdict);
        Assert.Equal(0, model.ProcessingTimeMs);
    }
}
