using System.Text;
using FluentAssertions;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class ContentCacheActionPolicyTests
{
    [Fact]
    public async Task Second_matching_request_short_circuits_with_cached_html()
    {
        var options = new StyloExtractActionOptions
        {
            TransformedContentCache = new TransformedContentCacheOptions { Enabled = true }
        };
        var policy = PolicyFactory.ContentCache(options);
        var first = HttpContextBuilder.CreateHtmlContext();
        first.Request.Method = "GET";
        first.Request.Host = new HostString("example.test");
        first.Request.Path = "/docs/cache";
        var origin = new MemoryStream();
        first.Response.Body = origin;

        await ActionPolicyRunner.RunAndFlushAsync(
            first,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            "<html><body>cached</body></html>",
            origin);

        var second = HttpContextBuilder.CreateHtmlContext();
        second.Request.Method = "GET";
        second.Request.Host = new HostString("example.test");
        second.Request.Path = "/docs/cache";
        var cachedBody = new MemoryStream();
        second.Response.Body = cachedBody;

        var result = await policy.ExecuteAsync(second, Evidence.Bot());

        result.Continue.Should().BeFalse();
        Encoding.UTF8.GetString(cachedBody.ToArray()).Should().Be("<html><body>cached</body></html>");
    }
}
