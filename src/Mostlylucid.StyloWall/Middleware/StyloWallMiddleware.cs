using System.Buffers;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Mostlylucid.StyloWall.Abstractions;
using Mostlylucid.StyloWall.Models;

namespace Mostlylucid.StyloWall.Middleware;

public sealed class StyloWallMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StyloWallGate _gate;
    private readonly IReadOnlyDictionary<string, IResponseTransform[]> _transformsByMode;
    private readonly IOptionsMonitor<StyloWallOptions> _options;
    private readonly ILogger<StyloWallMiddleware> _logger;

    public StyloWallMiddleware(
        RequestDelegate next,
        StyloWallGate gate,
        IEnumerable<IResponseTransform> transforms,
        IOptionsMonitor<StyloWallOptions> options,
        ILogger<StyloWallMiddleware> logger)
    {
        _next = next;
        _gate = gate;
        _options = options;
        _logger = logger;
        _transformsByMode = transforms
            .GroupBy(t => t.Mode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var verdict = _gate.Evaluate(context);
        if (!verdict.ShouldTransform || !_transformsByMode.ContainsKey(verdict.Mode))
        {
            await _next(context);
            return;
        }

        var original = context.Response.Body;
        var maxBuffer = _options.CurrentValue.MaxBufferBytes;
        using var buffer = new RecyclableMemoryStream(maxBuffer);
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        catch
        {
            context.Response.Body = original;
            throw;
        }

        context.Response.Body = original;

        if (!IsTransformableResponse(context, buffer.Length))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
            return;
        }

        var sourceContentType = context.Response.ContentType ?? MediaTypeNames.Text.Html;
        var encoding = ResolveEncoding(sourceContentType);

        var transformCtx = new ResponseTransformContext
        {
            Http = context,
            Verdict = verdict,
            SourceBody = buffer.GetWrittenMemory(),
            SourceContentType = sourceContentType,
            SourceEncoding = encoding.WebName
        };

        var transforms = _transformsByMode[verdict.Mode];
        IResponseTransform? selected = null;
        foreach (var t in transforms)
        {
            if (t.CanTransform(transformCtx))
            {
                selected = t;
                break;
            }
        }

        if (selected is null)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
            return;
        }

        TransformResult result;
        try
        {
            result = await selected.TransformAsync(transformCtx, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StyloWall transform '{Transform}' failed; passing through.", selected.GetType().Name);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
            return;
        }

        context.Response.ContentType = result.ContentType;
        context.Response.ContentLength = result.Body.Length;
        context.Response.Headers["X-StyloWall-Mode"] = verdict.Mode;
        context.Response.Headers["X-StyloWall-Trigger"] = verdict.Trigger.ToString();

        await original.WriteAsync(result.Body, context.RequestAborted);
    }

    private static bool IsTransformableResponse(HttpContext context, long bodyLength)
    {
        if (bodyLength == 0) return false;
        if (context.Response.StatusCode is < 200 or >= 300) return false;
        var contentType = context.Response.ContentType;
        if (string.IsNullOrEmpty(contentType)) return false;
        return contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding ResolveEncoding(string contentType)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.Charset.HasValue)
        {
            try { return Encoding.GetEncoding(parsed.Charset.Value!); }
            catch { /* fall through */ }
        }
        return Encoding.UTF8;
    }

    private sealed class RecyclableMemoryStream : Stream
    {
        private byte[] _buffer;
        private int _length;
        private int _position;
        private readonly int _maxLength;
        private bool _disposed;

        public RecyclableMemoryStream(int maxLength)
        {
            _maxLength = maxLength;
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(8192, maxLength));
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = checked((int)value);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = _length - _position;
            if (available <= 0) return 0;
            var toCopy = Math.Min(count, available);
            Buffer.BlockCopy(_buffer, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => checked((int)offset),
                SeekOrigin.Current => checked(_position + (int)offset),
                SeekOrigin.End => checked(_length + (int)offset),
                _ => _position
            };
            return _position;
        }

        public override void SetLength(long value)
        {
            EnsureCapacity(checked((int)value));
            _length = checked((int)value);
            if (_position > _length) _position = _length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(_position + count);
            Buffer.BlockCopy(buffer, offset, _buffer, _position, count);
            _position += count;
            if (_position > _length) _length = _position;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(_position + buffer.Length);
            buffer.CopyTo(_buffer.AsSpan(_position));
            _position += buffer.Length;
            if (_position > _length) _length = _position;
        }

        public ReadOnlyMemory<byte> GetWrittenMemory() => _buffer.AsMemory(0, _length);

        private void EnsureCapacity(int required)
        {
            if (required > _maxLength)
                throw new InvalidOperationException(
                    $"StyloWall response buffer exceeded {_maxLength} bytes (needed {required}).");
            if (required <= _buffer.Length) return;
            var newSize = _buffer.Length;
            while (newSize < required) newSize = Math.Min(_maxLength, newSize * 2);
            var next = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, next, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            base.Dispose(disposing);
        }
    }
}
