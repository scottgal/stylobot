using Microsoft.Extensions.Logging;
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
/// </summary>
public sealed class SignatureVerdictGate
{
    private readonly SignatureCoordinator _coordinator;
    private readonly ILogger<SignatureVerdictGate> _logger;

    public SignatureVerdictGate(SignatureCoordinator coordinator, ILogger<SignatureVerdictGate> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task<GateDecision> DecideAsync(
        string? signature,
        SignatureCacheOptions options,
        CancellationToken ct = default)
    {
        if (!options.Enabled || string.IsNullOrEmpty(signature))
            return new GateDecision(GateAction.Miss, null);

        var verdict = await _coordinator.TryGetVerdictAsync(signature, ct);
        if (verdict is null)
            return new GateDecision(GateAction.Miss, null);

        // Reject very-low-confidence entries: noise.
        if (verdict.Confidence < options.BiasMinConfidence)
            return new GateDecision(GateAction.Miss, verdict);

        var ageSeconds = (DateTime.UtcNow - verdict.LastSeenUtc).TotalSeconds;

        var skipEligible =
            verdict.Confidence >= options.SkipMinConfidence
            && ageSeconds <= options.SkipMaxAgeSeconds;

        if (skipEligible && !ShouldRefresh(signature, options.SkipSamplingRate))
            return new GateDecision(GateAction.Skip, verdict);

        var biasEligible = ageSeconds <= options.BiasMaxAgeSeconds;
        return new GateDecision(biasEligible ? GateAction.Bias : GateAction.Miss, verdict);
    }

    // Deterministic refresh: a fraction of Skip-eligible requests are downgraded to
    // Bias so the pipeline runs and refreshes the live state. Stable by signature hash
    // so retries land identically. See DeterministicBucket for the shared impl.
    private static bool ShouldRefresh(string signature, double rate)
        => DeterministicBucket.ShouldFire(signature, rate);
}
