namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     No-op <see cref="IPolicyDecisionLogQueue"/> for hosts that didn't wire
///     a durable <see cref="IPolicyDecisionLog"/> or didn't register an
///     <see cref="Scheduling.IScheduleCoordinator"/>. Calls to
///     <see cref="Enqueue"/> are silently dropped (and counted) so the
///     dispatcher continues to function without a hard dependency on the
///     decision log.
/// </summary>
public sealed class NullPolicyDecisionLogQueue : IPolicyDecisionLogQueue
{
    private long _droppedCount;

    /// <inheritdoc />
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc />
    public void Enqueue(PolicyDecision decision) => Interlocked.Increment(ref _droppedCount);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}
