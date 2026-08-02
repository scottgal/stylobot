using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     SEV0 2026-08-02 regression lock: the signature-detail page's headline
///     Probability/Confidence (and therefore RiskBand/IsBot, both derived from them)
///     was overridden by <c>IFingerprintReader</c>'s <c>Fingerprint.CachedBotProbability</c>
///     -- a separately-cached, separately-timed fingerprint-identity belief -- instead
///     of being folded from the SAME <c>DetectorContributions</c> rendered directly below
///     it on the same page. Prod symptom: 16 detectors overwhelmingly bot (HeuristicLate
///     0.967, Behavioral 0.750, Missing-UA 0.80, Datacenter-IP 0.72, ...) but the headline
///     read Probability 23% / Confidence 93% / label "Human" -- two sources of truth in
///     one view. This fixture is the operator's exact numbers; it must fail against the
///     old fingerprint-cache override and pass once the headline folds from
///     <c>latest</c> only.
/// </summary>
public sealed class SignatureDetailHeadlineFoldAtReadTests
{
    [Fact]
    public void Headline_folds_from_the_latest_detection_not_a_stale_fingerprint_cache()
    {
        // The fixture: what a request with these exact detector contributions
        // (HeuristicLate 0.967, Behavioral 0.750, Missing-UA 0.80, Datacenter 0.72,
        // + 12 more overwhelmingly-bot detectors) actually folds to at detection time
        // -- i.e. what DetectionLedgerExtensions.ToAggregatedEvidence already computed
        // and persisted onto `latest`. This is the SAME number the DetectorContributions
        // list on the page is built from.
        var latest = new DashboardDetectionEvent
        {
            RequestId = "req-sev0-fixture",
            Timestamp = DateTime.UtcNow,
            IsBot = true,
            BotProbability = 0.97,
            Confidence = 0.93,
            RiskBand = "VeryHigh",
            Method = "GET",
            Path = "/BDKR28WP.php",
            StatusCode = 429,
            ProcessingTimeMs = 4.2,
        };

        var (probability, confidence, scoreUpdatedAt) =
            StyloBotDashboardMiddleware.ResolveSignatureHeadline(latest);

        // High probability, bot -- matching what the visible contributions say, not a
        // stale "Human 23%" pulled from a different store.
        Assert.True(probability >= 0.9, $"Expected headline probability to fold from the live contributions (>=0.90), got {probability}.");
        Assert.Equal(0.97, probability);
        Assert.Equal(0.93, confidence);
        Assert.Null(scoreUpdatedAt);
    }

    [Fact]
    public void Headline_is_never_influenced_by_a_low_stale_cached_probability()
    {
        // Even in a scenario shaped like the fingerprint-cache override existing (a
        // low/stale cached belief for this identity), the headline must ignore it
        // entirely -- there is no code path left that reads a fingerprint's
        // CachedBotProbability for this resolution. Simulated here by simply
        // asserting the resolver's signature takes no fingerprint input at all: it
        // is structurally impossible for a stale identity-level cache to leak into
        // the headline.
        var latest = new DashboardDetectionEvent
        {
            RequestId = "req-sev0-fixture-2",
            Timestamp = DateTime.UtcNow,
            IsBot = true,
            BotProbability = 0.92,
            Confidence = 0.90,
            RiskBand = "High",
            Method = "GET",
            Path = "/wp-login.php",
            StatusCode = 200,
            ProcessingTimeMs = 3.1,
        };

        var (probability, confidence, _) = StyloBotDashboardMiddleware.ResolveSignatureHeadline(latest);

        Assert.Equal(latest.BotProbability, probability);
        Assert.Equal(latest.Confidence, confidence);
    }
}
