namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Signal raised on the shared threat-intel refresh sink after a successful
///     <see cref="IThreatIntelProvider.RefreshAsync"/>. Consumers subscribe
///     reactively instead of polling <see cref="ProviderStatus.LastRefreshUtc"/>;
///     the provider stays the source of truth (this signal carries no cached
///     verdicts), and the signal is a "feed landed" notification.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors the task-#65 <c>BotListUpdatedSignal</c> shape: a small
///         notification with the provider name and success metadata, sourced
///         through <see cref="Mostlylucid.Ephemeral.TypedSignalSink{T}"/>.
///     </para>
///     <para>
///         Kept payload-free apart from identifying which provider landed,
///         because the cache lives in the provider; consumers that need the
///         fresh verdicts call <see cref="IThreatIntelProvider.TryLookup"/>.
///         This preserves <c>feedback_no_caches_freshness_over_locality</c>.
///     </para>
/// </remarks>
public sealed record ThreatIntelRefreshedSignal
{
    /// <summary>Named typed key for this signal.</summary>
    public static readonly Mostlylucid.Ephemeral.SignalKey<ThreatIntelRefreshedSignal> Key =
        new("threatintel.refreshed");

    /// <summary>Provider whose refresh just landed (<see cref="IThreatIntelProvider.Name"/>).</summary>
    public required string Provider { get; init; }

    /// <summary>When the refresh completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    ///     Was this refresh the recovery landing after a prior failure? Set when
    ///     the immediately-preceding refresh for the same provider failed and
    ///     this one succeeded. Useful for monitoring / alert de-flap.
    /// </summary>
    public bool RecoveredFromFailure { get; init; }
}