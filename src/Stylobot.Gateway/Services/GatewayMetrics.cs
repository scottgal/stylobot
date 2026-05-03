using System.Diagnostics;

namespace Stylobot.Gateway.Services;

/// <summary>
/// Gateway metrics tracking service.
/// </summary>
public class GatewayMetrics
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _requestsTotal;
    private long _errorsTotal;
    private long _activeConnections;
    private long _bytesIn;
    private long _bytesOut;
    private readonly object _rpsLock = new();
    private DateTime _rpsWindowStart = DateTime.UtcNow;
    private long _rpsWindowCount;

    /// <summary>
    /// Gateway uptime.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    /// <summary>
    /// Total requests processed.
    /// </summary>
    public long RequestsTotal => Interlocked.Read(ref _requestsTotal);

    /// <summary>
    /// Approximate requests per second (rolling window).
    /// </summary>
    public double RequestsPerSecond
    {
        get
        {
            lock (_rpsLock)
            {
                var elapsed = (DateTime.UtcNow - _rpsWindowStart).TotalSeconds;
                if (elapsed < 0.1) return 0;
                return _rpsWindowCount / elapsed;
            }
        }
    }

    /// <summary>
    /// Total errors.
    /// </summary>
    public long ErrorsTotal => Interlocked.Read(ref _errorsTotal);

    /// <summary>
    /// Current active connections.
    /// </summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>
    /// Total bytes received.
    /// </summary>
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    /// <summary>
    /// Total bytes sent.
    /// </summary>
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>
    /// Record a request.
    /// </summary>
    public void RecordRequest()
    {
        Interlocked.Increment(ref _requestsTotal);

        lock (_rpsLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _rpsWindowStart).TotalSeconds > 10)
            {
                // Reset window every 10 seconds
                _rpsWindowStart = now;
                _rpsWindowCount = 0;
            }
            _rpsWindowCount++;
        }
    }

    /// <summary>
    /// Record an error.
    /// </summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errorsTotal);
    }

    /// <summary>
    /// Record connection start.
    /// </summary>
    public void ConnectionStarted()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    /// <summary>
    /// Record connection end.
    /// </summary>
    public void ConnectionEnded()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    /// <summary>
    /// Record bytes transferred.
    /// </summary>
    public void RecordBytes(long bytesIn, long bytesOut)
    {
        Interlocked.Add(ref _bytesIn, bytesIn);
        Interlocked.Add(ref _bytesOut, bytesOut);
    }
}
