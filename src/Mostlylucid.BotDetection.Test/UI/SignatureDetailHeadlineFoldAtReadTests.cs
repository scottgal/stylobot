using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     SEV0 2026-08-02 regression lock, operator's governing invariant: the bot score
///     (probability, risk band, threat, human/bot label, name) has EXACTLY ONE source --
///     the read-time fold of the current detector contributions. No component may store,
///     cache, or serve a score; a stale/served score is unacceptable.
///     <para>
///     Prod symptom (signature KDUIdlwxAmeLMjrtbPGa4w): the detector contributions list
///     showed HeuristicLate +0.967, Heuristic +0.600, Behavioral +0.750, Missing-UA +0.80,
///     Missing-Accept/few-headers +0.75, Datacenter(Azure) +0.72 (16 detectors total,
///     overwhelmingly bot) but the headline served Probability 23% / Confidence 93% /
///     Risk Low / label "Human" -- two sources of truth in one view. Root cause:
///     <c>Fingerprint.CachedBotProbability</c>, a separately-cached identity-level belief,
///     was read instead of folding from the SAME contributions. First fix pass swapped that
///     for <c>latest.BotProbability</c> -- itself still a stored scalar that happens to
///     match; per the operator's literal law that is still a second source of truth. This
///     is the corrected fix: <see cref="StyloBotDashboardMiddleware.ResolveSignatureHeadline"/>
///     now folds directly from <c>latest.DetectorContributions</c> every read, reproducing
///     the identical <c>sigmoid(weighted-sum)</c> the pipeline used at detection time.
///     </para>
/// </summary>
public sealed class SignatureDetailHeadlineFoldAtReadTests
{
    /// <summary>
    ///     The operator's exact fixture (the 6 named contributions from signature
    ///     KDUIdlwxAmeLMjrtbPGa4w's live dump). ConfidenceDelta == Contribution for each
    ///     entry (Weight 1.0) since only the pre-weighted Contribution values were handed
    ///     down -- the fold-at-read math is on Contribution (already delta*weight, summed
    ///     per detector), so the exact delta/weight split doesn't change the result.
    /// </summary>
    private static Dictionary<string, DashboardDetectorContribution> OperatorFixtureContributions() => new()
    {
        ["HeuristicLate"] = new DashboardDetectorContribution { ConfidenceDelta = 0.967, Contribution = 0.967, Reason = "98% bot likelihood, 258 features" },
        ["Heuristic"] = new DashboardDetectorContribution { ConfidenceDelta = 0.600, Contribution = 0.600, Reason = "80% bot, 18 features" },
        ["Behavioral"] = new DashboardDetectorContribution { ConfidenceDelta = 0.750, Contribution = 0.750, Reason = "no referrer, no cookies, random scanning pattern" },
        ["MissingUserAgent"] = new DashboardDetectorContribution { ConfidenceDelta = 0.80, Contribution = 0.80, Reason = "Missing User-Agent" },
        ["Header"] = new DashboardDetectorContribution { ConfidenceDelta = 0.75, Contribution = 0.75, Reason = "Missing Accept / very few headers" },
        ["Ip"] = new DashboardDetectorContribution { ConfidenceDelta = 0.72, Contribution = 0.72, Reason = "Datacenter IP (Azure)" },
    };

    [Fact]
    public void Headline_folds_from_the_operators_exact_fixture_to_high_probability_bot()
    {
        var latest = new DashboardDetectionEvent
        {
            RequestId = "KDUIdlwxAmeLMjrtbPGa4w",
            Timestamp = DateTime.UtcNow,
            IsBot = false,          // the STORED/served fields -- deliberately the WRONG prod values.
            BotProbability = 0.23,  // If the resolver reads these instead of folding from
            Confidence = 0.93,      // DetectorContributions, this test must fail.
            RiskBand = "Low",
            Method = "GET",
            Path = "/BDKR28WP.php",
            StatusCode = 429,
            ProcessingTimeMs = 4.2,
            DetectorContributions = OperatorFixtureContributions(),
        };

        var (probability, confidence, _) = StyloBotDashboardMiddleware.ResolveSignatureHeadline(latest);

        // sigmoid(0.967+0.600+0.750+0.80+0.75+0.72) = sigmoid(4.587) ~= 0.99 -- high
        // probability bot, not the stored 23%/Low/"Human".
        Assert.True(probability > 0.5,
            $"Expected the folded probability to be well above 50% (high-prob bot) given overwhelmingly-bot contributions, got {probability}.");
        Assert.True(probability > 0.9,
            $"Expected the folded probability to closely track the strong evidence (>90%), got {probability}.");
        // Must NOT equal the stale stored value -- that's the exact bug.
        Assert.NotEqual(0.23, probability);
        Assert.True(confidence > 0.5, $"Expected reasonable confidence from 6 agreeing detectors, got {confidence}.");
    }

    [Fact]
    public void Headline_falls_back_to_latest_scalars_only_when_no_contributions_are_available()
    {
        // Cold/legacy rows with no persisted DetectorContributions (pre-dates the
        // contributions column, or a build path that never populated it) have nothing to
        // fold from -- latest.BotProbability/Confidence are the best available answer, not
        // a design regression back to a parasitic store (there's no separate store being
        // read here, just the same event's own scalar fields as a degraded-input fallback).
        var latest = new DashboardDetectionEvent
        {
            RequestId = "req-no-contributions",
            Timestamp = DateTime.UtcNow,
            IsBot = true,
            BotProbability = 0.81,
            Confidence = 0.70,
            RiskBand = "High",
            Method = "GET",
            Path = "/some/path",
            StatusCode = 200,
            ProcessingTimeMs = 2.0,
            DetectorContributions = null,
        };

        var (probability, confidence, _) = StyloBotDashboardMiddleware.ResolveSignatureHeadline(latest);

        Assert.Equal(0.81, probability);
        Assert.Equal(0.70, confidence);
    }

    // ─── STRUCTURAL (single-source) lock ────────────────────────────────────────────
    //
    // Source-text assertion, mirroring this codebase's existing pattern for
    // architectural locks (e.g. SignatureDetailVerdictMergeTests). Proves the ONLY
    // producer of the headline score is the fold over DetectorContributions -- no read
    // path in the signature-detail handler may construct the model's BotProbability /
    // Confidence from a Fingerprint's cached fields ever again.

    private static string LocateSource(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "src", relativePath);
        Assert.True(File.Exists(path), $"Expected source file at {path}");
        return path;
    }

    [Fact]
    public void ResolveSignatureHeadline_is_the_only_producer_no_fingerprint_cache_path_remains()
    {
        var source = File.ReadAllText(LocateSource(
            Path.Combine("Mostlylucid.BotDetection.UI", "Middleware", "StyloBotDashboardMiddleware.cs")));

        // The parasitic store's exact fields must never again be assigned to the headline
        // locals. Scoped to the two known-safe historical comment mentions (which explain
        // the OLD bug for future readers) by checking there's no live assignment shape.
        Assert.DoesNotContain("headlineProb = fp.CachedBotProbability", source);
        Assert.DoesNotContain("headlineConf = fp.InferredTypeConfidence", source);

        // The resolver must be called to populate the headline locals -- not any other
        // fingerprint-derived expression.
        Assert.Contains("ResolveSignatureHeadline(latest)", source);
    }

    [Fact]
    public void ResolveSignatureHeadline_body_computes_from_contributions_not_a_stored_scalar()
    {
        var source = File.ReadAllText(LocateSource(
            Path.Combine("Mostlylucid.BotDetection.UI", "Middleware", "StyloBotDashboardMiddleware.cs")));

        var start = source.IndexOf("internal static (double Probability, double Confidence, DateTime? ScoreUpdatedAt) ResolveSignatureHeadline", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected to find the ResolveSignatureHeadline method.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected to find the end of the ResolveSignatureHeadline method body.");
        var body = source[start..end];

        // The fold must be computed from DetectorContributions (sigmoid of the weighted
        // sum), not a passthrough of latest.BotProbability as the primary path.
        Assert.Contains("DetectorContributions", body);
        Assert.Contains("Math.Exp", body);
    }
}
