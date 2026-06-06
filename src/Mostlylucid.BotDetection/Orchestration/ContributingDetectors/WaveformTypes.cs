using System.Collections.Immutable;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     One captured request in the behavioural waveform history for a
///     signature. Internal-visibility because the analysis logic lives
///     inside <see cref="BehavioralWaveformContributor"/>; the store layer
///     uses it as the merge / persist op.
/// </summary>
public sealed record RequestSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string UserAgent { get; init; } = string.Empty;
    public string RefererHash { get; init; } = string.Empty;
    public ContentClass ContentClass { get; init; }
}

/// <summary>
///     Coarse content class used as the Markov-chain state for the
///     waveform's request-transition analysis. Ordering matters because
///     consumers project it into a transition matrix by ordinal.
/// </summary>
public enum ContentClass
{
    Page = 0,
    Asset = 1,
    Api = 2,
    WebSocket = 3,
    SSE = 4,
    SignalR = 5
}

/// <summary>
///     Immutable per-signature history aggregate held in the hot tier of
///     <see cref="WaveformHistoryStore"/>. <see cref="LastActivityTicks"/>
///     drives LFU coldness scoring (recent activity stays warm). The
///     snapshot list is FIFO-trimmed to <see cref="WaveformHistoryStore.MaxSnapshotsPerSignature"/>
///     on merge.
/// </summary>
public sealed record WaveformHistory(
    ImmutableList<RequestSnapshot> Snapshots,
    long LastActivityTicks)
{
    public static WaveformHistory Empty => new(ImmutableList<RequestSnapshot>.Empty, 0);
}
