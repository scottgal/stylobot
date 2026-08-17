using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Xunit;

namespace Stylobot.Gateway.Tests.Integration;

/// <summary>
///     Regression for the 2026-08-17 P0: Googlebot/GPTBot got a clean HTTP 200 with an EMPTY
///     body from the gateway's default config (content-cache-search / extract-markdown-cache-ai
///     silently produced zero bytes -- confirmed by direct instrumentation that YARP writes the
///     full proxied body into <see cref="BodyInterceptStream"/>'s buffer correctly, but nothing
///     ever finalized it: the pre-fix code only finalized on an explicit Flush()/FlushAsync()/
///     Dispose() call, none of which YARP's forwarder ever makes on the substituted
///     <c>Response.Body</c> Stream -- it completes the response via
///     <c>HttpResponse.CompleteAsync()</c> / the pipe-writer feature instead, bypassing the
///     Stream override entirely.
/// </summary>
public class ResponseBodyCaptureOnStartingTests
{
    private static DefaultHttpContext NewContextWithStartingSupport(out MemoryStream originalBody)
    {
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        originalBody = new MemoryStream();
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(originalBody));
        var context = new DefaultHttpContext(features);
        context.Response.ContentType = "text/html";
        return context;
    }

    [Fact]
    public async Task Finalize_runs_via_OnStarting_before_HasStarted_flips_true()
    {
        // Simulates YARP: write the proxied body into Response.Body (our interceptor), then
        // signal completion via StartAsync() -- exactly what triggers HasStarted to flip and
        // is where the real bug lived (nothing here previously ran before that point).
        var context = NewContextWithStartingSupport(out var originalBody);
        var capture = new ResponseBodyCapture();

        var interceptor = capture.InstallInterceptor(context, html => Task.FromResult<string?>(html.ToUpperInvariant()));

        var proxiedBytes = "<html><body>hello</body></html>"u8.ToArray();
        await context.Response.Body.WriteAsync(proxiedBytes);

        context.Response.HasStarted.Should().BeFalse("nothing should have flipped HasStarted yet -- writes only landed in the interceptor's buffer");

        await context.Response.StartAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var written = new StreamReader(originalBody).ReadToEnd();
        written.Should().Be("<HTML><BODY>HELLO</BODY></HTML>",
            "the OnStarting hook must finalize (transform + write) BEFORE the framework marks the response started -- " +
            "the regression served a 0-byte body because nothing ran until it was already too late to write");
        _ = interceptor;
    }

    [Fact]
    public async Task Passthrough_writes_original_bytes_unchanged_when_transform_returns_null()
    {
        var context = NewContextWithStartingSupport(out var originalBody);
        var capture = new ResponseBodyCapture();

        capture.InstallInterceptor(context, _ => Task.FromResult<string?>(null));

        var proxiedBytes = "<html><body>pass-through</body></html>"u8.ToArray();
        await context.Response.Body.WriteAsync(proxiedBytes);

        await context.Response.StartAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var written = new StreamReader(originalBody).ReadToEnd();
        written.Should().Be("<html><body>pass-through</body></html>");
    }

    [Fact]
    public async Task Non_html_content_type_passes_through_unchanged()
    {
        var context = NewContextWithStartingSupport(out var originalBody);
        context.Response.ContentType = "application/json";
        var capture = new ResponseBodyCapture();

        var transformCalled = false;
        capture.InstallInterceptor(context, _ =>
        {
            transformCalled = true;
            return Task.FromResult<string?>("SHOULD NOT BE USED");
        });

        var proxiedBytes = """{"ok":true}"""u8.ToArray();
        await context.Response.Body.WriteAsync(proxiedBytes);

        await context.Response.StartAsync();

        transformCalled.Should().BeFalse("non-HTML responses must never reach the transform");
        originalBody.Seek(0, SeekOrigin.Begin);
        new StreamReader(originalBody).ReadToEnd().Should().Be("""{"ok":true}""");
    }
}
