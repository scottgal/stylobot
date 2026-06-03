using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

public enum GateAction
{
    /// <summary>No usable verdict in the window. Run the full pipeline.</summary>
    Miss,

    /// <summary>Verdict exists but does not qualify for Skip. Run the pipeline with the verdict injected as a prior.</summary>
    Bias,

    /// <summary>Verdict is fresh and confident. Skip the pipeline and enforce the cached verdict (subject to watchdog veto).</summary>
    Skip,
}

public sealed record GateDecision(GateAction Action, SignatureVerdict? Verdict);

/// <summary>
///     Decides per request whether to skip the detector pipeline (Skip), bias it with
///     the live signature verdict (Bias), or run it fresh (Miss). Reads the verdict
///     from <see cref="SignatureCoordinator.TryGetVerdictAsync"/> (the live sliding
///     window) and applies the policy's <see cref="SignatureCacheOptions"/> thresholds.
///     There is no parallel cache: this gate is a thin decision over the existing
///     coordinator state.
///
///     When the metastable identity layer is enabled and an
///     <see cref="IdentityVerdictLookup"/> is supplied, the gate also reads the
///     fingerprint-cached verdict via <c>fingerprint_keys[primary_signature]</c>. The
///     fresher of the two sources wins; the fingerprint source survives IP/UA rotation
///     so a returning visitor inherits their prior verdict instead of paying for a
///     fresh pipeline pass.
/// </summary>
public sealed class SignatureVerdictGate
{
    private readonly SignatureCoordinator _coordinator;
    private readonly IdentityVerdictLookup? _identityLookup;
    private readonly ILogger<SignatureVerdictGate> _logger;

    public SignatureVerdictGate(
        SignatureCoordinator coordinator,
        ILogger<SignatureVerdictGate> logger,
        IdentityVerdictLookup? identityLookup = null)
    {
        _coordinator = coordinator;
        _logger = logger;
        _identityLookup = identityLookup;
    }

    public async Task<GateDecision> DecideAsync(
        string? signature,
        SignatureCacheOptions options,
        CancellationToken ct = default)
    {
        if (!options.Enabled || string.IsNullOrEmpty(signature))
        {
            _logger.LogDebug(
                "VerdictGate sig={SigPrefix} -> Miss (gate disabled or empty signature)",
                signature?.Substring(0, Math.Min(8, signature.Length)) ?? "(null)");
            return new GateDecision(GateAction.Miss, null);
        }

        // Time each lookup so we can see SQLite contention under HTTP/2 burst.
        // Live finding (sig lXkzR1RLyBbyq19hUIP9ZA on prod): request 1 (HTML) hit
        // identity-cache in 1.2 ms; requests 2-5 (CSS assets from the same visitor,
        // same primarySig, 1 second later) missed and ran the full pipeline at
        // 13-15 ms each. Static code analysis says both should hit; only runtime
        // logs will reveal whether the SQLite row vanished, the connection raced,
        // or sigVerdict/idVerdict diverged across the burst.
        var sigSwStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var sigVerdict = await _coordinator.TryGetVerdictAsync(signature, ct);
        var sigLookupMs = ElapsedMs(sigSwStart);

        var idSwStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var idVerdict = _identityLookup is null
            ? null
            : await _identityLookup.TryGetAsync(signature, ct);
        var idLookupMs = ElapsedMs(idSwStart);

        var sigPrefix = signature.Substring(0, Math.Min(8, signature.Length));

        var verdict = ComposeVerdicts(signature, sigVerdict, idVerdict);
        if (verdict is null)
        {
            _logger.LogInformation(
                "VerdictGate sig={SigPrefix} -> Miss (no cached verdict) " +
                "sigHit={SigHit} sigMs={SigMs:F1} idHit={IdHit} idMs={IdMs:F1}",
                sigPrefix, sigVerdict is not null, sigLookupMs, idVerdict is not null, idLookupMs);
            return new GateDecision(GateAction.Miss, null);
        }

        // Reject very-low-confidence entries: noise.
        if (verdict.Confidence < options.BiasMinConfidence)
        {
            _logger.LogInformation(
                "VerdictGate sig={SigPrefix} -> Miss (low confidence {Conf:F2} < BiasMin {Min:F2}) " +
                "sigHit={SigHit} sigMs={SigMs:F1} idHit={IdHit} idMs={IdMs:F1}",
                sigPrefix, verdict.Confidence, options.BiasMinConfidence,
                sigVerdict is not null, sigLookupMs, idVerdict is not null, idLookupMs);
            return new GateDecision(GateAction.Miss, verdict);
        }

        var ageSeconds = (DateTime.UtcNow - verdict.LastSeenUtc).TotalSeconds;

        var skipEligible =
            verdict.Confidence >= options.SkipMinConfidence
            && ageSeconds <= options.SkipMaxAgeSeconds;

        if (skipEligible && !ShouldRefresh(signature, options.SkipSamplingRate))
        {
            _logger.LogInformation(
                "VerdictGate sig={SigPrefix} -> Skip conf={Conf:F2} age={AgeSec:F1}s " +
                "sigHit={SigHit} sigMs={SigMs:F1} idHit={IdHit} idMs={IdMs:F1} prob={Prob:F2}",
                sigPrefix, verdict.Confidence, ageSeconds,
                sigVerdict is not null, sigLookupMs, idVerdict is not null, idLookupMs,
                verdict.BotProbability);
            return new GateDecision(GateAction.Skip, verdict);
        }

        var biasEligible = ageSeconds <= options.BiasMaxAgeSeconds;
        var action = biasEligible ? GateAction.Bias : GateAction.Miss;
        _logger.LogInformation(
            "VerdictGate sig={SigPrefix} -> {Action} conf={Conf:F2} age={AgeSec:F1}s skipMin={SkipMin:F2} skipMaxAge={SkipMaxAge}s biasMaxAge={BiasMaxAge}s " +
            "sigHit={SigHit} sigMs={SigMs:F1} idHit={IdHit} idMs={IdMs:F1} refresh={Refresh}",
            sigPrefix, action, verdict.Confidence, ageSeconds, options.SkipMinConfidence,
            options.SkipMaxAgeSeconds, options.BiasMaxAgeSeconds,
            sigVerdict is not null, sigLookupMs, idVerdict is not null, idLookupMs,
            ShouldRefresh(signature, options.SkipSamplingRate));
        return new GateDecision(action, verdict);
    }

    private static double ElapsedMs(long startTicks)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // Deterministic refresh: a fraction of Skip-eligible requests are downgraded to
    // Bias so the pipeline runs and refreshes the live state. Stable by signature hash
    // so retries land identically. See DeterministicBucket for the shared impl.
    private static bool ShouldRefresh(string signature, double rate)
        => DeterministicBucket.ShouldFire(signature, rate);

    /// <summary>
    ///     Composes the per-signature verdict and the per-fingerprint cached verdict.
    ///     Rule: take whichever source is fresher by timestamp. The fingerprint source
    ///     survives IP/UA rotation, so when it's fresher the visitor's stable identity
    ///     verdict wins over a possibly-stale per-signature aggregate. When the
    ///     fingerprint source wins it carries its own probability and risk band into
    ///     the gate; when the signature source wins, the fingerprint id is still
    ///     attached so dashboards see a continuous identity. Confidence on a fingerprint
    ///     verdict is derived from the fingerprint's observation count using the same
    ///     ramp the signature verdict uses (full at 10+).
    /// </summary>
    internal static SignatureVerdict? ComposeVerdicts(
        string signatureId,
        SignatureVerdict? sig,
        IdentityCachedVerdict? id)
    {
        if (sig is null && id is null) return null;
        if (id is null) return sig; // identity disabled or no fingerprint match: existing behaviour
        if (sig is null) return SynthesiseFromIdentity(signatureId, id);

        // Both present — pick the fresher source and tag with the fingerprint id either way.
        if (id.UpdatedAtUtc > sig.LastSeenUtc)
            return SynthesiseFromIdentity(signatureId, id);

        return sig with { IdentityFingerprintId = id.FingerprintId };
    }

    private static SignatureVerdict SynthesiseFromIdentity(string signatureId, IdentityCachedVerdict id)
    {
        var confidence = Math.Min(1.0, id.ObservationCount / 10.0);
        // cached_risk_band is only written by IdentityAiOpinionService (slow-path L2). For
        // the >90% of fingerprints that never trigger L2 the column is NULL, which used to
        // fall through to RiskBand.Unknown and consume the whole risk-band histogram on
        // the dashboard. Derive the band from the cached bot_probability instead -- a
        // 10+-observation fingerprint at prob=0 is structurally VeryLow, not Unknown.
        RiskBand band;
        if (Enum.TryParse<RiskBand>(id.RiskBand, ignoreCase: true, out var parsed) && parsed != RiskBand.Unknown)
        {
            band = parsed;
        }
        else
        {
            band = Risk.SignatureRiskVerdictComposer.BucketRisk(id.BotProbability, confidence);
        }
        return new SignatureVerdict
        {
            SignatureId = signatureId,
            BotProbability = id.BotProbability,
            Confidence = confidence,
            RiskBand = band,
            ThreatScore = 0,
            RequestCount = id.ObservationCount,
            LastSeenUtc = id.UpdatedAtUtc,
            IdentityFingerprintId = id.FingerprintId,
            FromIdentityCache = true
        };
    }
}
