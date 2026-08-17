using System.Text;
using FluentAssertions;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Middleware;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class ExtractMarkdownCacheAiActionPolicyTests
{
    private const string Html = "<html><body><h1>Hello</h1><p>World</p></body></html>";
    private const string Markdown = "# Hello\n\nWorld\n";

    [Fact]
    public async Task ExecuteAsync_returns_Allowed()
    {
        var fake = new FakeExtractor { MarkdownToReturn = Markdown };
        var policy = PolicyFactory.Markdown(fake);
        var context = HttpContextBuilder.CreateHtmlContext();

        var result = await policy.ExecuteAsync(context, Evidence.Bot());

        result.Continue.Should().BeTrue();
    }

    [Fact]
    public async Task Name_is_extract_markdown()
    {
        var policy = PolicyFactory.Markdown();
        policy.Name.Should().Be("extract-markdown-cache-ai");
    }

    [Fact]
    public async Task ActionType_is_Custom()
    {
        var policy = PolicyFactory.Markdown();
        policy.ActionType.Should().Be(Mostlylucid.BotDetection.Actions.ActionType.Custom);
    }

    [Fact]
    public async Task After_downstream_writes_html_body_is_markdown()
    {
        var fake = new FakeExtractor { MarkdownToReturn = Markdown };
        var policy = PolicyFactory.Markdown(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        var (body, _) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        body.Should().Be(Markdown);
    }

    [Fact]
    public async Task After_transform_content_type_is_text_markdown()
    {
        var fake = new FakeExtractor { MarkdownToReturn = Markdown };
        var policy = PolicyFactory.Markdown(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        context.Response.ContentType.Should().StartWith("text/markdown");
    }

    [Fact]
    public async Task Query_override_format_eq_markdown_triggers_transform()
    {
        var fake = new FakeExtractor { MarkdownToReturn = Markdown };
        var opts = new StyloExtractActionOptions
        {
            EnableQueryOverride = true,
            QueryParamName = "format",
            QueryParamValue = "markdown"
        };
        var policy = PolicyFactory.Markdown(fake, opts);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext("format=markdown");
        context.Response.Body = originalBody;
        // The override marker is what the policy honours (MarkdownQueryOverrideMiddleware sets it
        // BEFORE ExecuteAsync after matching the query parameter). A Human request carrying the
        // marker is the explicit test action: it gets the Markdown variant, never HTML.
        context.Items[MarkdownQueryOverrideMiddleware.MarkerKey] = true;

        var (body, _) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Human()),
            Html,
            originalBody);

        body.Should().Be(Markdown);
    }

    [Fact]
    public async Task Human_without_override_marker_gets_html_not_markdown()
    {
        var fake = new FakeExtractor { MarkdownToReturn = Markdown };
        var policy = PolicyFactory.Markdown(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext("format=markdown");
        context.Response.Body = originalBody;

        // No marker (the middleware only sets it when the override is enabled and the query
        // matches) and Human evidence -> eligibility gate refuses; HTML passes through.
        var (body, _) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Human()),
            Html,
            originalBody);

        body.Should().Be(Html);
        fake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Content_length_matches_the_actual_markdown_bytes_not_the_upstream_html_length()
    {
        // P0 regression (2026-08-17): "Response Content-Length mismatch: too few bytes written
        // (0 of 141)". YARP copies the upstream response's Content-Length verbatim onto the
        // outgoing response BEFORE our interceptor's transform runs; the transform then writes
        // a DIFFERENT-length body (markdown is virtually always shorter than the source HTML)
        // without ever reconciling the header against what actually got written. Simulate the
        // upstream-declared length here the way YARP would set it, and assert the final header
        // matches the transformed body's real byte count -- not the stale HTML-sized promise.
        var longerHtml = "<html><body>" + new string('x', 500) + "</body></html>";
        var shortMarkdown = "# Short\n";
        var fake = new FakeExtractor { MarkdownToReturn = shortMarkdown };
        var policy = PolicyFactory.Markdown(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(longerHtml); // YARP's stale promise

        var (body, headers) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            longerHtml,
            originalBody);

        body.Should().Be(shortMarkdown);
        headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(shortMarkdown),
            "Content-Length must reflect the transformed body actually written, or Kestrel " +
            "throws a Content-Length mismatch against the stale upstream-HTML-sized promise");
    }

    [Fact]
    public async Task Markdown_cache_hit_short_circuits_without_reextracting()
    {
        var options = new StyloExtractActionOptions
        {
            TransformedContentCache = new TransformedContentCacheOptions { Enabled = true }
        };
        var extractor = new FakeExtractor { MarkdownToReturn = Markdown };
        await using var cache = new MarkdownResponseCache(options.TransformedContentCache);
        var policy = PolicyFactory.Markdown(extractor, options, cache);
        var first = HttpContextBuilder.CreateHtmlContext();
        first.Request.Method = "GET";
        first.Request.Host = new HostString("example.test");
        first.Request.Path = "/docs/cache";
        var origin = new MemoryStream();
        first.Response.Body = origin;

        await ActionPolicyRunner.RunAndFlushAsync(first, c => policy.ExecuteAsync(c, Evidence.Bot()), Html, origin);

        var second = HttpContextBuilder.CreateHtmlContext();
        second.Request.Method = "GET";
        second.Request.Host = new HostString("example.test");
        second.Request.Path = "/docs/cache";
        var cachedBody = new MemoryStream();
        second.Response.Body = cachedBody;

        var result = await policy.ExecuteAsync(second, Evidence.Bot());

        result.Continue.Should().BeFalse();
        Encoding.UTF8.GetString(cachedBody.ToArray()).Should().Be(Markdown);
        extractor.CallCount.Should().Be(1);
    }
}
