namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Configuration for <see cref="SqlitePolicyDecisionLog"/>. Every knob
///     lives here so deployments can tune the writer behaviour without
///     code changes.
/// </summary>
public sealed class PolicyDecisionLogOptions
{
    /// <summary>SQLite database file path. <c>:memory:</c> is honoured for tests.</summary>
    public string DatabasePath { get; init; } = "policy-decisions.sqlite";

    /// <summary>How often the background drainer wakes to flush the channel.</summary>
    public TimeSpan DrainerInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum number of rows flushed in a single drainer cycle / transaction.</summary>
    public int DrainerBatchSize { get; init; } = 500;

    /// <summary>Upper bound on the in-memory channel. When full, the oldest row is dropped.</summary>
    public int ChannelCapacity { get; init; } = 50_000;

    /// <summary>
    ///     Rows older than this are deleted by an external sweeper task.
    ///     <see cref="SqlitePolicyDecisionLog"/> does not enforce retention
    ///     itself; the option records operator intent for the sweeper to read.
    /// </summary>
    public int RetentionDays { get; init; } = 30;
}
