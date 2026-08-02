namespace Mostlylucid.BotDetection.Posture;

/// <summary>
///     Neutral extension point for a host-level detection posture override. FOSS ships the
///     default (<see cref="FullDetectionPostureProvider"/>): learning always proceeds and
///     enforcement always runs at full strength. A host application may register its own
///     <see cref="IDetectionPostureProvider"/> to globally suppress learning writes or force
///     enforcement into an observe-only posture, for whatever reason the host cares about
///     (a licensing tier, a maintenance window, an incident freeze) -- FOSS does not know or
///     care why, only what. This keeps the detection/learning pipeline (public, standalone)
///     free of any vocabulary belonging to a specific host's business rules.
/// </summary>
public interface IDetectionPostureProvider
{
    /// <summary>
    ///     False globally suppresses learning writes -- fingerprint verdict absorption,
    ///     observation recording (and the centroid absorption it feeds), and corrections.
    ///     Detection still runs and still reads the live cache; nothing new is folded into
    ///     it while this is false. Distinct from the per-request, per-API-key
    ///     <c>IsLearningSuppressedByApiKey</c> gate -- this is a global, host-level switch.
    ///     Default (no host implementation registered): true.
    /// </summary>
    bool LearningEnabled { get; }

    /// <summary>
    ///     True forces every action-policy dispatch into the observe-only (log-only) posture
    ///     -- the same shadow <c>BotDetectionOptions.ObserveOnly</c> already uses: detection
    ///     runs and results are recorded, nothing is blocked/throttled/challenged. Default
    ///     (no host implementation registered): false.
    /// </summary>
    bool ForceLogOnlyPosture { get; }
}

/// <summary>
///     FOSS default: full learning, full enforcement, no gating. Registered via
///     <c>TryAddSingleton</c> so a host that registers its own
///     <see cref="IDetectionPostureProvider"/> before <c>AddBotDetection</c>/<c>AddStyloBot</c>
///     runs keeps its own implementation; standalone FOSS with nothing registered gets this,
///     and behaves exactly as it did before this seam existed.
/// </summary>
public sealed class FullDetectionPostureProvider : IDetectionPostureProvider
{
    public static readonly FullDetectionPostureProvider Instance = new();

    public bool LearningEnabled => true;
    public bool ForceLogOnlyPosture => false;
}
