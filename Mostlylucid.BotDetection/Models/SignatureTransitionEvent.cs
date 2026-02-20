namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Records a meaningful change in a signature's behavioral profile.
///     Provides full attribution for why a bot score changed at a given instant.
/// </summary>
public sealed record SignatureTransitionEvent
{
    /// <summary>Hashed signature identifier.</summary>
    public required string Signature { get; init; }

    /// <summary>When the event occurred.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>What kind of behavioral shift was detected.</summary>
    public required TransitionEventType EventType { get; init; }

    /// <summary>Previous metric value (before the shift).</summary>
    public double FromValue { get; init; }

    /// <summary>New metric value (after the shift).</summary>
    public double ToValue { get; init; }

    /// <summary>Human-readable explanation of what triggered this event.</summary>
    public required string Trigger { get; init; }

    /// <summary>Snapshot of the top signals at the moment of the event.</summary>
    public Dictionary<string, object>? ContributingSignals { get; init; }
}

/// <summary>
///     Types of behavioral transition events.
/// </summary>
public enum TransitionEventType
{
    /// <summary>Self-drift JS divergence crossed threshold.</summary>
    DriftSpike,

    /// <summary>Path entropy dropped significantly (becoming robotic).</summary>
    EntropyDrop,

    /// <summary>Path entropy spiked (switching to scatter mode).</summary>
    EntropySpike,

    /// <summary>A→B→A→B loop pattern detected.</summary>
    LoopDetected,

    /// <summary>Signature's cohort assignment changed.</summary>
    CohortShift,

    /// <summary>High fraction of novel transitions (new exploration).</summary>
    NoveltyBurst,

    /// <summary>Signature joined a cluster.</summary>
    ClusterJoined,

    /// <summary>Cluster type promoted (e.g., Emergent → BotProduct).</summary>
    ClusterPromoted,

    /// <summary>Adaptive weight for a feature shifted significantly.</summary>
    WeightShift,

    /// <summary>Bot probability crossed a band boundary.</summary>
    ScoreBandChange,

    /// <summary>Sequence surprise exceeded threshold under cohort baseline.</summary>
    HighSurprise
}
