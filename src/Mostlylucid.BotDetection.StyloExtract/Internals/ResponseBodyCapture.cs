using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
/// Wraps the response body stream so downstream middleware can write normally while
/// this helper intercepts the bytes for inspection or replacement.
///
/// The intended call pattern when running as part of an <c>IActionPolicy</c> is:
/// 1. Call <see cref="InstallInterceptor"/> before returning from ExecuteAsync.
/// 2. The StyloBot middleware calls <c>next()</c>; downstream writes into the interceptor.
/// 3. The interceptor's <see cref="BodyInterceptStream.FlushAsync"/> / Dispose fires the
///    transformation and writes to the original body.
///
/// For tests and helpers that control the full call stack, use
/// <see cref="CaptureBodyAsync"/> with a real downstream delegate.
/// </summary>
public sealed class ResponseBodyCapture
{
    /// <summary>
    /// Installs a <see cref="BodyInterceptStream"/> on <paramref name="context"/> that
    /// buffers all bytes written by the next middleware. When the interceptor is flushed
    /// or disposed, <paramref name="transform"/> is called with the captured text (null
    /// when the response is not HTML or has no body). The transform result is written to
    /// the original body; when transform returns null the captured bytes are written back
    /// unchanged (pass-through).
    /// </summary>
    /// <returns>The interceptor stream (caller can store it to read <see cref="BodyInterceptStream.OriginalBody"/>).</returns>
    public BodyInterceptStream InstallInterceptor(
        HttpContext context,
        Func<string, Task<string?>> transform)
    {
        var interceptor = new BodyInterceptStream(context.Response.Body, context, transform);
        context.Response.Body = interceptor;
        return interceptor;
    }

    /// <summary>
    /// Convenience helper for tests and code paths that own the full call stack.
    /// Runs <paramref name="downstream"/>, then returns the captured HTML text
    /// (or null for non-HTML / no-body responses).
    ///
    /// The original body is NOT automatically written back by this method.
    /// The caller is responsible for writing either the transformed content or
    /// the original captured bytes.
    /// </summary>
    public async Task<string?> CaptureBodyAsync(HttpContext context, Func<Task> downstream)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await downstream();

            var status = context.Response.StatusCode;
            if (status is 204 or 304 || (status >= 300 && status < 400))
            {
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
                return null;
            }

            if (!IsHtmlContentType(context.Response.ContentType))
            {
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
                return null;
            }

            buffer.Seek(0, SeekOrigin.Begin);
            return await new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true).ReadToEndAsync();
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="stream"/> as UTF-8, returning the
    /// byte count so the caller can update Content-Length if desired.
    /// </summary>
    public static async Task<int> WriteTextAsync(Stream stream, string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
        return bytes.Length;
    }

    /// <summary>
    /// Returns true when <paramref name="contentType"/> indicates an HTML payload that
    /// StyloExtract can process. Case-insensitive; charset suffix is ignored.
    /// </summary>
    public static bool IsHtmlContentType(string? contentType)
    {
        if (contentType is null) return false;
        var semi = contentType.IndexOf(';');
        var mime = semi >= 0 ? contentType[..semi].Trim() : contentType.Trim();
        return mime.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A <see cref="Stream"/> that transparently buffers all bytes written to it.
/// When the stream is flushed or disposed, the transform delegate is invoked and the
/// result (or the captured buffer unchanged) is written to the original body.
/// </summary>
public sealed class BodyInterceptStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly HttpContext _context;
    private readonly Func<string, Task<string?>> _transform;
    private bool _flushed;

    /// <summary>The body stream that was replaced.</summary>
    public Stream OriginalBody { get; }

    public BodyInterceptStream(Stream originalBody, HttpContext context, Func<string, Task<string?>> transform)
    {
        OriginalBody = originalBody;
        _context = context;
        _transform = transform;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => _buffer.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _buffer.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _buffer.WriteAsync(buffer, cancellationToken);

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_flushed) return;
        _flushed = true;

        _context.Response.Body = OriginalBody;

        var status = _context.Response.StatusCode;
        if (status is 204 or 304 || (status >= 300 && status < 400) || _buffer.Length == 0)
        {
            _buffer.Seek(0, SeekOrigin.Begin);
            await _buffer.CopyToAsync(OriginalBody, cancellationToken);
            return;
        }

        if (!ResponseBodyCapture.IsHtmlContentType(_context.Response.ContentType))
        {
            _buffer.Seek(0, SeekOrigin.Begin);
            await _buffer.CopyToAsync(OriginalBody, cancellationToken);
            return;
        }

        _buffer.Seek(0, SeekOrigin.Begin);
        var html = await new StreamReader(_buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true).ReadToEndAsync(cancellationToken);

        string? transformed = null;
        try
        {
            transformed = await _transform(html);
        }
        catch
        {
            // Transform failed; fall through to write original bytes.
        }

        if (transformed is null)
        {
            var original = Encoding.UTF8.GetBytes(html);
            await OriginalBody.WriteAsync(original, cancellationToken);
        }
        else
        {
            await OriginalBody.WriteAsync(Encoding.UTF8.GetBytes(transformed), cancellationToken);
        }

        await OriginalBody.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_flushed)
            Flush();
        base.Dispose(disposing);
    }
}
