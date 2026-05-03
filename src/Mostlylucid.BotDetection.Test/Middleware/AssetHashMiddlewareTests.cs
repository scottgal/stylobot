using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class AssetHashMiddlewareTests : IAsyncLifetime
{
    private AssetHashStore _store = null!;
    private CentroidSequenceStore _centroidStore = null!;

    public async Task InitializeAsync()
    {
        var cs = $"Data Source=asset_middleware_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _centroidStore = new CentroidSequenceStore(cs, NullLogger<CentroidSequenceStore>.Instance);
        _store = new AssetHashStore(cs, _centroidStore, NullLogger<AssetHashStore>.Instance);
        await _centroidStore.InitializeAsync();
        await _store.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AssetHashMiddleware CreateMiddleware(string? responseEtag = null, string? responseLastModified = null, long? contentLength = null)
    {
        RequestDelegate next = ctx =>
        {
            if (responseEtag != null) ctx.Response.Headers.ETag = responseEtag;
            if (responseLastModified != null) ctx.Response.Headers["Last-Modified"] = responseLastModified;
            if (contentLength.HasValue) ctx.Response.ContentLength = contentLength;
            return Task.CompletedTask;
        };
        return new AssetHashMiddleware(next, _store, NullLogger<AssetHashMiddleware>.Instance);
    }

    [Fact]
    public async Task NonStaticPath_does_not_record_hash()
    {
        var mw = CreateMiddleware(responseEtag: "\"abc\"");
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/blog/post";
        await mw.InvokeAsync(ctx);
        Assert.False(_store.IsRecentlyChanged("/blog/post"));
    }

    [Fact]
    public async Task StaticPath_with_etag_change_records_change()
    {
        // First request: record "abc"
        var mw1 = CreateMiddleware(responseEtag: "\"abc\"");
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Path = "/vendor/tailwind.css";
        await mw1.InvokeAsync(ctx1);

        // Second request: different ETag → change detected
        var mw2 = CreateMiddleware(responseEtag: "\"def\"");
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Path = "/vendor/tailwind.css";
        await mw2.InvokeAsync(ctx2);

        Assert.True(_store.IsRecentlyChanged("/vendor/tailwind.css"));
    }

    [Fact]
    public async Task StaticPath_no_etag_uses_last_modified_fallback()
    {
        var mw1 = CreateMiddleware(responseLastModified: "Wed, 23 Apr 2026 00:00:00 GMT", contentLength: 1024);
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Path = "/vendor/app.js";
        await mw1.InvokeAsync(ctx1);

        var mw2 = CreateMiddleware(responseLastModified: "Thu, 24 Apr 2026 00:00:00 GMT", contentLength: 2048);
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Path = "/vendor/app.js";
        await mw2.InvokeAsync(ctx2);

        Assert.True(_store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public async Task StaticPath_no_fingerprint_headers_skips_recording()
    {
        var mw = CreateMiddleware(); // no headers
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/vendor/font.woff2";
        await mw.InvokeAsync(ctx);
        Assert.False(_store.IsRecentlyChanged("/vendor/font.woff2"));
    }
}
