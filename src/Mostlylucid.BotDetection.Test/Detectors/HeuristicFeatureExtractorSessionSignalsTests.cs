using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Pins every new signal introduced this session against its expected
///     feature key in <see cref="HeuristicFeatureExtractor"/>. Without this,
///     a future addition to the SignalKeys family + a missing wire-up in
///     ExtractStructuredSignalValues would silently degrade heuristic
///     scoring (the signal exists, the contributor writes it, but the model
///     never sees it). Same diagnostic shape as the original ghost-signal
///     bug surfaced during the Bonus B work.
/// </summary>
public class HeuristicFeatureExtractorSessionSignalsTests
{
    private static AggregatedEvidence EvidenceWith(IDictionary<string, object> signals)
    {
        var ledger = new DetectionLedger("test");
        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = 0.0,
            Confidence = 0.0,
            RiskBand = RiskBand.Low,
            CategoryBreakdown = new Dictionary<string, CategoryScore>(),
            Signals = signals.ToImmutableDictionary(),
            ContributingDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>()
        };
    }

    private static HttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";
        return ctx;
    }

    // === Identity-change risk signals ======================================

    [Fact]
    public void RiskCountryChanged_LandsAs_sigv_risk_country_changed()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.RiskCountryChanged] = true }));
        Assert.Equal(1f, features["sigv:risk_country_changed"]);
    }

    [Fact]
    public void RiskShapeHashChanged_LandsAs_sigv_risk_shape_hash_changed()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.RiskShapeHashChanged] = true }));
        Assert.Equal(1f, features["sigv:risk_shape_hash_changed"]);
    }

    [Fact]
    public void RiskBotdKindChanged_LandsAs_sigv_risk_botd_kind_changed()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.RiskBotdKindChanged] = true }));
        Assert.Equal(1f, features["sigv:risk_botd_kind_changed"]);
    }

    [Fact]
    public void RiskSuspiciousChangeScore_LandsAs_sigv_risk_suspicious_change_score()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.RiskSuspiciousChangeScore] = 0.7 }));
        Assert.Equal(0.7f, features["sigv:risk_suspicious_change_score"], 0.01);
    }

    // === Client-side beacon probes =========================================

    [Fact]
    public void ClientSideIceNoSrflx_LandsAs_sigv_ice_no_srflx()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideIceNoSrflx] = true }));
        Assert.Equal(1f, features["sigv:ice_no_srflx"]);
    }

    [Fact]
    public void ClientSideMouseAllIntegerCoords_LandsAs_sigv_mouse_all_integer()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideMouseAllIntegerCoords] = true }));
        Assert.Equal(1f, features["sigv:mouse_all_integer"]);
    }

    [Fact]
    public void ClientSideMouseTimingCv_LandsAs_sigv_mouse_timing_cv()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideMouseTimingCv] = 0.18 }));
        Assert.Equal(0.18f, features["sigv:mouse_timing_cv"], 0.01);
    }

    [Fact]
    public void ClientMouseEvents_LandsAs_sigv_mouse_events_normalised()
    {
        // Cap is 50 (matches the JS sampler cap). 25 events = 0.5.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientMouseEvents] = 25 }));
        Assert.Equal(0.5f, features["sigv:mouse_events"], 0.01);
    }

    [Fact]
    public void ClientSideTtsVoiceCount_LandsAs_sigv_tts_voice_count_normalised()
    {
        // Scale is 20 (typical Android Chrome 15-30 voices). 10 = 0.5.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideTtsVoiceCount] = 10 }));
        Assert.Equal(0.5f, features["sigv:tts_voice_count"], 0.01);
    }

    [Fact]
    public void ClientSidePoolCollisionContexts_LandsAs_sigv_pool_collision_normalised()
    {
        // Scale is 5 (threshold of 3 = 0.6, well above attention bar). 3 = 0.6.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSidePoolCollisionContexts] = 3 }));
        Assert.Equal(0.6f, features["sigv:pool_collision_contexts"], 0.01);
    }

    [Fact]
    public void ClientSideBotdKind_LandsAsStringEnumFeature()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideBotdKind] = "selenium" }));
        // AddStringEnumFeature emits "{prefix}:{normalized_value}" = 1f
        Assert.Equal(1f, features["sigv:botd_kind:selenium"]);
    }

    [Fact]
    public void ClientSideConnectionType_LandsAsStringEnumFeature()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideConnectionType] = "ethernet" }));
        Assert.Equal(1f, features["sigv:conn_type:ethernet"]);
    }

    [Fact]
    public void ClientSideShapeHash_LandsAsPresenceOnlyFlag()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.ClientSideShapeHash] = "abcd1234deadbeef" }));
        Assert.Equal(1f, features["sigv:shape_hash_present"]);
    }

    [Fact]
    public void ClientSideShapeHash_AbsentMeansFeatureMissing()
    {
        // Presence-only: when the signal isn't there, the feature key shouldn't
        // appear at all (vs landing as 0f). Catches an extraction regression
        // that would default-emit the feature and lie to the model.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object>()));
        Assert.False(features.ContainsKey("sigv:shape_hash_present"));
    }

    // === TLS corpus signals ================================================

    [Fact]
    public void TlsCipherSubsetOfRealChrome_LandsAs_sigv_tls_cipher_subset()
    {
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.TlsCipherSubsetOfRealChrome] = true }));
        Assert.Equal(1f, features["sigv:tls_cipher_subset"]);
    }

    [Fact]
    public void TlsVersionDeltaFromUa_LandsAs_sigv_tls_version_delta_normalised()
    {
        // Scale is 10. Delta of 5 versions = 0.5.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.TlsVersionDeltaFromUa] = 5 }));
        Assert.Equal(0.5f, features["sigv:tls_version_delta"], 0.01);
    }

    [Fact]
    public void TlsCipherSubsetMissingCount_LandsAs_sigv_tls_cipher_subset_missing_normalised()
    {
        // Scale is 5 (damru ships up to 3 missing). 3 missing = 0.6.
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object> { [SignalKeys.TlsCipherSubsetMissingCount] = 3 }));
        Assert.Equal(0.6f, features["sigv:tls_cipher_subset_missing"], 0.01);
    }

    // === Defensive absence handling ========================================

    [Fact]
    public void NoSignalsAtAll_ProducesNoSessionFeatureKeys()
    {
        // Empty Signals dict: none of the new feature keys should leak in via
        // a default-emit path. Catches an extractor regression that confuses
        // "absence" with "zero".
        var features = HeuristicFeatureExtractor.ExtractFeatures(
            NewContext(),
            EvidenceWith(new Dictionary<string, object>()));

        Assert.False(features.ContainsKey("sigv:risk_shape_hash_changed"));
        Assert.False(features.ContainsKey("sigv:ice_no_srflx"));
        Assert.False(features.ContainsKey("sigv:tls_cipher_subset"));
        Assert.False(features.ContainsKey("sigv:mouse_timing_cv"));
    }
}
