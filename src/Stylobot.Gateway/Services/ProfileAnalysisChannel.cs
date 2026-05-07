using System.Threading.Channels;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Services;

public sealed class ProfileAnalysisChannel
{
    private readonly Channel<ProfileRequestSnapshot> _channel;
    private readonly int _capacity;
    private long _totalEnqueued;
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
        {
            Interlocked.Increment(ref _totalDropped);
            return false;
        }

        return true;
    }

    public IAsyncEnumerable<ProfileRequestSnapshot> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.Complete();
}
