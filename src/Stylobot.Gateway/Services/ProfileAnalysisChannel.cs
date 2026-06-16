using System.Threading.Channels;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Services;

public sealed class ProfileAnalysisChannel
{
    private readonly Channel<ProfileRequestSnapshot> _channel;
    private readonly int _capacity;
    private long _totalEnqueued;
    // Best-effort lower bound: non-atomic check+write means drops may be undercounted under concurrent writers
    private long _totalDropped;

    public ProfileAnalysisChannel(ProfileModeOptions options)
    {
        _capacity = options.ChannelCapacity;
        _channel = Channel.CreateBounded<ProfileRequestSnapshot>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public int QueueDepth => _channel.Reader.Count;
    public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    public bool TryEnqueue(ProfileRequestSnapshot snapshot)
    {
        // With DropOldest, TryWrite always returns true (it drops the oldest item to make room).
        // Track drops by checking if the channel was already at capacity before writing.
        var wasFull = _channel.Reader.Count >= _capacity;

        _channel.Writer.TryWrite(snapshot);
        Interlocked.Increment(ref _totalEnqueued);

        if (wasFull)
            Interlocked.Increment(ref _totalDropped);

        return true;
    }

    public IAsyncEnumerable<ProfileRequestSnapshot> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    ///     Non-blocking single-item read used by the tick-driven drainer
    ///     (<see cref="ProfileAnalysisWorker"/>'s ScheduleCoordinator handler).
    ///     Returns <c>false</c> when the channel is empty so the tick can
    ///     finish its drain pass and yield to the next tick.
    /// </summary>
    public bool TryRead(out ProfileRequestSnapshot snapshot) =>
        _channel.Reader.TryRead(out snapshot!);

    public void Complete() => _channel.Writer.Complete();
}
