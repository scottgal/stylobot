namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     <see cref="IDataRateLimiter"/> that throttles writes via a per-call
///     <see cref="ThrottledStream"/>. Each wrap gets its own leaky bucket;
///     buckets are not shared across requests because each request has its
///     own response body stream. Aggregating per-subject across concurrent
///     requests is a v2 problem (it requires the same Redis-backed token
///     accounting the request-rate limiter uses).
/// </summary>
public sealed class MemoryDataRateLimiter : IDataRateLimiter
{
    public Stream WrapResponseStream(
        IReadOnlyList<RateLimitSubject> subjects,
        Stream inner,
        long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return inner;
        return new ThrottledStream(inner, bytesPerSecond);
    }

    /// <summary>
    ///     Write-only wrapper. Each Write block-awaits until the bucket has
    ///     room. The bucket refills at <c>bytesPerSecond</c> tokens per second
    ///     up to a one-second burst -- a sudden 200 KB write on a 100 KB/s
    ///     budget completes over two seconds.
    /// </summary>
    private sealed class ThrottledStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _bytesPerSecond;
        private readonly long _burst;
        private double _tokens;
        private long _lastRefillTicks;
        private static readonly double TicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

        public ThrottledStream(Stream inner, long bytesPerSecond)
        {
            _inner = inner;
            _bytesPerSecond = bytesPerSecond;
            _burst = bytesPerSecond;     // one-second burst
            _tokens = bytesPerSecond;
            _lastRefillTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var remaining = buffer;
            while (remaining.Length > 0)
            {
                var allowed = await AcquireAsync(remaining.Length, ct).ConfigureAwait(false);
                await _inner.WriteAsync(remaining[..allowed], ct).ConfigureAwait(false);
                remaining = remaining[allowed..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Synchronous write blocks the calling thread on the bucket refill.
            // ASP.NET Core's response pipeline is async-by-default; this path is
            // rare but kept for full Stream contract compliance.
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>
        ///     Refill the bucket, take as many tokens as are available up to
        ///     <paramref name="requested"/>. If the bucket is empty after refill,
        ///     await the next refill window before returning a positive grant.
        /// </summary>
        private async ValueTask<int> AcquireAsync(int requested, CancellationToken ct)
        {
            while (true)
            {
                Refill();
                var grant = (int)Math.Min(requested, Math.Floor(_tokens));
                if (grant > 0)
                {
                    _tokens -= grant;
                    return grant;
                }

                // No tokens; wait long enough for at least one to refill.
                // 250 ms granularity matches the spec's tentative answer; small
                // enough that bandwidth feels smooth, large enough that the
                // CPU cost is negligible at scale.
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        private void Refill()
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsedSeconds = (now - _lastRefillTicks) / TicksPerSecond;
            if (elapsedSeconds <= 0) return;
            _tokens = Math.Min(_burst, _tokens + elapsedSeconds * _bytesPerSecond);
            _lastRefillTicks = now;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
