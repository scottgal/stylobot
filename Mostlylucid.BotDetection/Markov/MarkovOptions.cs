namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Configuration for the Markov chain path learning and drift detection system.
/// </summary>
public class MarkovOptions
{
    /// <summary>Half-life for per-signature chains (hours). Default: 1</summary>
    public double SignatureHalfLifeHours { get; set; } = 1.0;

    /// <summary>Half-life for cohort baselines (hours). Default: 6</summary>
    public double CohortHalfLifeHours { get; set; } = 6.0;

    /// <summary>Half-life for global baseline (hours). Default: 24</summary>
    public double GlobalHalfLifeHours { get; set; } = 24.0;

    /// <summary>Maximum outgoing edges per source node (top-K pruning). Default: 20</summary>
    public int MaxEdgesPerNode { get; set; } = 20;

    /// <summary>Minimum transitions before computing drift signals. Default: 5</summary>
    public int MinTransitionsForDrift { get; set; } = 5;

    /// <summary>Size of the recent transition buffer per signature. Default: 30</summary>
    public int RecentTransitionWindowSize { get; set; } = 30;

    /// <summary>Interval for flushing cohort updates (seconds). Default: 5</summary>
    public int CohortFlushIntervalSeconds { get; set; } = 5;

    /// <summary>Interval for snapshotting models to storage (seconds). Default: 300</summary>
    public int SnapshotIntervalSeconds { get; set; } = 300;

    /// <summary>Interval for batching transition events to storage (seconds). Default: 10</summary>
    public int TransitionEventFlushIntervalSeconds { get; set; } = 10;

    /// <summary>Maximum transition events per signature in memory. Default: 50</summary>
    public int MaxTransitionEventsPerSignature { get; set; } = 50;

    // Drift thresholds for escalation
    /// <summary>Self-drift JS divergence threshold. Default: 0.3</summary>
    public double SelfDriftThreshold { get; set; } = 0.3;

    /// <summary>Human-drift JS divergence threshold. Default: 0.4</summary>
    public double HumanDriftThreshold { get; set; } = 0.4;

    /// <summary>Loop score threshold. Default: 0.5</summary>
    public double LoopScoreThreshold { get; set; } = 0.5;

    /// <summary>Sequence surprise threshold. Default: 3.0</summary>
    public double SequenceSurpriseThreshold { get; set; } = 3.0;

    /// <summary>Transition novelty threshold. Default: 0.4</summary>
    public double TransitionNoveltyThreshold { get; set; } = 0.4;

    /// <summary>Entropy delta threshold (absolute). Default: 1.0</summary>
    public double EntropyDeltaThreshold { get; set; } = 1.0;
}
