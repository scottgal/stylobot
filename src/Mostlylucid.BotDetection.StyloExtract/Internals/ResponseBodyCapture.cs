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

        // P0 (2026-08-17): confirmed via direct instrumentation that YARP writes the full
        // proxied body into this interceptor (bytes land in _buffer correctly) but then
        // completes the response itself -- via HttpResponse.CompleteAsync()/the
        // IHttpResponseBodyFeature pipe, NOT through the Response.Body Stream we substituted
        // -- so HasStarted flips true with nothing ever written to the real connection. Two
        // finalize triggers were tried and both fired too late (HasStarted already true,
        // any Response.* write throws and the framework silently swallows it): a plain
        // FlushAsync-on-first-call (the pre-existing behaviour, which additionally had the
        // separate premature-empty-buffer bug) and RegisterForDisposeAsync (fires at
        // HttpContext disposal, well after YARP's completion). OnStarting is the correct
        // hook: it runs synchronously, exactly once, immediately BEFORE headers are actually
        // sent -- i.e. before HasStarted flips true -- regardless of which API (Stream vs.
        // PipeWriter vs. CompleteAsync) downstream used to finish writing.
        context.Response.OnStarting(static state =>
            ((BodyInterceptStream)state).FinalizeAsync(), interceptor);
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

    // Non-definitive: a bare Flush()/FlushAsync() call (not the OnStarting/Dispose backstops)
    // is treated as advisory. The primary finalize trigger is FinalizeAsync(), invoked via
    // Response.OnStarting -- see ResponseBodyCapture.InstallInterceptor for why: YARP writes
    // the full proxied body into _buffer (confirmed) but completes the response itself via
    // HttpResponse.CompleteAsync()/the pipe-writer feature, bypassing this Stream override
    // entirely, so nothing here ever observes that completion directly. An empty buffer on a
    // bare Flush() may just mean "nothing written yet" -- defer rather than finalize on it.
    public override Task FlushAsync(CancellationToken cancellationToken) => FlushCoreAsync(cancellationToken, isDefinitive: false);

    /// <summary>The authoritative finalize entry point -- see the OnStarting registration in
    /// <see cref="ResponseBodyCapture.InstallInterceptor"/>.</summary>
    public Task FinalizeAsync() => FlushCoreAsync(CancellationToken.None, isDefinitive: true);

    private async Task FlushCoreAsync(CancellationToken cancellationToken, bool isDefinitive)
    {
        if (_flushed) return;

        var status = _context.Response.StatusCode;
        var bodylessStatus = status is 204 or 304 || (status >= 300 && status < 400);

        if (!isDefinitive && !bodylessStatus && _buffer.Length == 0)
            return; // Nothing written yet and nothing forces finality -- wait for the real trigger.

        _flushed = true;
        _context.Response.Body = OriginalBody;

        if (bodylessStatus || _buffer.Length == 0)
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

        // Snapshot the original bytes BEFORE decoding to a string. On pass-through (the
        // transform returns null or throws) we write these bytes back unchanged - if we
        // instead re-encoded the decoded string as UTF-8, we would silently rewrite any
        // BOM / charset / byte-exact characteristics of the downstream response,
        // contradicting the policy contract that pass-through preserves the original body.
        var originalBytes = _buffer.ToArray();

        var html = Encoding.UTF8.GetString(
            originalBytes,
            originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF
                ? 3 : 0,
            originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF
                ? originalBytes.Length - 3 : originalBytes.Length);

        string? transformed = null;
        try
        {
            transformed = await _transform(html);
        }
        catch
        {
            // Transform failed; fall through to write original bytes.
        }

        // Whatever we're about to write -- original bytes unchanged, or a transform's
        // (possibly differently-sized) replacement -- is the ONLY source of truth for
        // Content-Length. The upstream value YARP copied by default (and any header a
        // variant's TransformAsync tried to set from inside the transform callback above)
        // is stale the moment either path runs; setting it here, right before the write,
        // is what actually lands. Must run before HasStarted flips true -- see the
        // OnStarting registration in ResponseBodyCapture.InstallInterceptor.
        var finalBytes = transformed is null ? originalBytes : Encoding.UTF8.GetBytes(transformed);
        _context.Response.ContentLength = finalBytes.Length;

        await OriginalBody.WriteAsync(finalBytes, cancellationToken);
        await OriginalBody.FlushAsync(cancellationToken);
    }

    // Backstops only -- FinalizeAsync() via Response.OnStarting is the primary trigger.
    // These cover code paths where OnStarting somehow never fires (harmless no-ops
    // otherwise thanks to the _flushed guard).
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_flushed)
            FlushCoreAsync(CancellationToken.None, isDefinitive: true).GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_flushed)
            await FlushCoreAsync(CancellationToken.None, isDefinitive: true).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
