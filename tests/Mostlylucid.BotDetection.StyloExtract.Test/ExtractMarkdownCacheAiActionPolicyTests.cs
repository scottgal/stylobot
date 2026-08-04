using System.Text;
using FluentAssertions;
using Mostlylucid.BotDetection.StyloExtract.Internals;
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

        var (body, _) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Human()),
            Html,
            originalBody);

        body.Should().Be(Markdown);
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
