namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Signal raised on the shared bot-list update sink after a successful
///     refresh via <see cref="BotListUpdateService.PerformUpdateWithRetriesAsync"/>.
///     Consumers subscribe reactively instead of polling
///     <see cref="Data.IBotListDatabase"/>; the source of truth stays with
///     the database (this signal carries no payload data), and the signal is
///     just a "changes landed" notification.
/// </summary>
/// <remarks>
///     <para>
///         Reference implementation of the task-#65 "list updater emits to
///         a sink" pattern. Other refresh services (JA3 corpus, well-known
///         bots, verified-bot registry, threat intel enrichment, etc.)
///         should adopt the same shape: a small notification signal with a
///         shared sink, so consumers can react without touching the
///         parasite store.
///     </para>
///     <para>
///         Kept payload-free by design: the sink is a notification bus,
///         not a data channel. Consumers that need the fresh data read
///         from <see cref="Data.IBotListDatabase"/> in response. This
///         avoids duplicating the full pattern list on every raise
///         (violating <c>feedback_no_caches_freshness_over_locality</c>).
///     </para>
/// </remarks>
public sealed record BotListUpdatedSignal
{
    /// <summary>Named typed key for this signal.</summary>
    public static readonly Mostlylucid.Ephemeral.SignalKey<BotListUpdatedSignal> Key =
        new("botlist.updated");

    /// <summary>When the refresh completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    ///     How many consecutive-failure retries had run before the successful
    ///     refresh landed. Zero on a clean first-attempt success. Non-zero
    ///     values are useful for monitoring alert thresholds.
    /// </summary>
    public int RecoveredFromFailures { get; init; }
}