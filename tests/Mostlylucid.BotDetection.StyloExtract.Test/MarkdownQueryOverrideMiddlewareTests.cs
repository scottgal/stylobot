using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Middleware;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

/// <summary>
///     End-to-end tests for the explicit ?markdown=true test action
///     (<see cref="MarkdownQueryOverrideMiddleware"/>). The middleware is the ONLY component that
///     maps the query parameter to the override marker; the policy only honours the marker.
/// </summary>
public sealed class MarkdownQueryOverrideMiddlewareTests
{
    private const string Html = "<html><body><h1>Hello</h1><p>World</p></body></html>";
    private const string Markdown = "# Hello\n\nWorld\n";

    [Fact]
    public async Task Hit_short_circuits_before_origin()
    {
        var (registry, options, telemetry) = Build();
        BuildPolicy(registry, options, telemetry);

        // Warm the cache: request 1 misses, next writes HTML, flush stores Markdown.
        var warm = Middleware(options, _ => Task.CompletedTask);
        var warmContext = HttpContextBuilder.CreateHtmlContext("markdown=true");
        warmContext.Request.Host = new HostString("example.test");
        warmContext.Request.Path = "/docs/cache";
        var warmBody = new MemoryStream();
        warmContext.Response.Body = warmBody;
        await warm.InvokeAsync(warmContext, registry, options);
        await HttpContextBuilder.WriteHtmlAsync(warmContext, Html);
        await warmContext.Response.Body.FlushAsync();

        // Request 2 with the same key is a hit: the middleware must NOT call next.
        var nextCalled = false;
        var second = Middleware(options, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var secondContext = HttpContextBuilder.CreateHtmlContext("markdown=true");
        secondContext.Request.Host = new HostString("example.test");
        secondContext.Request.Path = "/docs/cache";
        var secondBody = new MemoryStream();
        secondContext.Response.Body = secondBody;

        await second.InvokeAsync(secondContext, registry, options);

        nextCalled.Should().BeFalse("a cache hit must short-circuit before the origin endpoint");
        secondContext.Response.ContentType.Should().StartWith("text/markdown");
        Encoding.UTF8.GetString(secondBody.ToArray()).Should().Be(Markdown);
    }

    [Fact]
    public async Task Miss_captures_html_transforms_and_serves_markdown()
    {
        var (registry, options, telemetry) = Build();
        BuildPolicy(registry, options, telemetry);
        var nextCalled = false;
        var middleware = Middleware(options, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = HttpContextBuilder.CreateHtmlContext("markdown=true");
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/docs/cache";
        var originalBody = new MemoryStream();
        context.Response.Body = originalBody;

        await middleware.InvokeAsync(context, registry, options);

        nextCalled.Should().BeTrue("a miss continues to origin");
        // Simulate the origin writing HTML through the installed interceptor.
        await HttpContextBuilder.WriteHtmlAsync(context, Html);
        await context.Response.Body.FlushAsync();

        context.Response.ContentType.Should().StartWith("text/markdown");
        Encoding.UTF8.GetString(originalBody.ToArray()).Should().Be(Markdown);
    }

    [Fact]
    public async Task Default_query_parameter_is_markdown_true()
    {
        // Spec: "A markdown=true query override is an explicit test action". The defaults
        // (QueryParamName="markdown", QueryParamValue="true") must activate the override.
        var (registry, options, telemetry) = Build();
        BuildPolicy(registry, options, telemetry);
        var middleware = Middleware(options, _ => Task.CompletedTask);
        var context = HttpContextBuilder.CreateHtmlContext("markdown=true");
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/docs/cache";
        var originalBody = new MemoryStream();
        context.Response.Body = originalBody;

        await middleware.InvokeAsync(context, registry, options);
        await HttpContextBuilder.WriteHtmlAsync(context, Html);
        await context.Response.Body.FlushAsync();

        Encoding.UTF8.GetString(originalBody.ToArray()).Should().Be(Markdown,
            "?markdown=true is the default override parameter per the spec");
    }

    [Fact]
    public async Task Override_is_separately_labelled_in_telemetry()
    {
        var (registry, options, telemetry) = Build();
        BuildPolicy(registry, options, telemetry);
        var middleware = Middleware(options, _ => Task.CompletedTask);
        var context = HttpContextBuilder.CreateHtmlContext("markdown=true");
        var originalBody = new MemoryStream();
        context.Response.Body = originalBody;

        await middleware.InvokeAsync(context, registry, options);
        await HttpContextBuilder.WriteHtmlAsync(context, Html);
        await context.Response.Body.FlushAsync();

        var counters = telemetry.Snapshot().Single(c => c.Policy == "extract-markdown-cache-ai");
        counters.Overrides.Should().Be(1, "the override is separately labelled, never blended with hits");
        counters.Misses.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_override_passes_through_to_origin()
    {
        var options = new StaticOptions(new StyloExtractActionOptions
        {
            EnableQueryOverride = false,
            QueryParamName = "markdown",
            QueryParamValue = "true"
        });
        var registry = new ActionPolicyRegistry(
            Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>());
        var nextCalled = false;
        var middleware = Middleware(options, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = HttpContextBuilder.CreateHtmlContext("markdown=true");

        await middleware.InvokeAsync(context, registry, options);

        nextCalled.Should().BeTrue("a disabled override must not intercept the request");
        context.Items.ContainsKey(MarkdownQueryOverrideMiddleware.MarkerKey).Should().BeFalse();
    }

    [Fact]
    public async Task Unregistered_policy_fails_open_to_origin()
    {
        // The pack's policies are not registered (AddStyloExtractActionPolicies not called):
        // the override cannot be honoured and the request must pass through untouched.
        var registry = new ActionPolicyRegistry(
            Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>());
        var nextCalled = false;
        var middleware = Middleware(new StaticOptions(), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = HttpContextBuilder.CreateHtmlContext("markdown=true");

        await middleware.InvokeAsync(context, registry, new StaticOptions());

        nextCalled.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static (ActionPolicyRegistry Registry, IOptionsFactory<StyloExtractActionOptions> Options,
        ContentCacheTelemetry Telemetry) Build()
    {
        var registry = new ActionPolicyRegistry(
            Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>());
        var options = new StaticOptions(new StyloExtractActionOptions
        {
            EnableQueryOverride = true,
            QueryParamName = "markdown",
            QueryParamValue = "true"
        });
        return (registry, options, new ContentCacheTelemetry());
    }

    private static MarkdownQueryOverrideMiddleware Middleware(
        IOptionsFactory<StyloExtractActionOptions> options,
        RequestDelegate next)
        => new(next);

    private static void BuildPolicy(
        ActionPolicyRegistry registry,
        IOptionsFactory<StyloExtractActionOptions> options,
        IContentCacheTelemetry telemetry)
    {
        var opts = options.Create("extract-markdown-cache-ai");
        opts.TransformedContentCache.Enabled = true;
        var policy = new ExtractMarkdownCacheAiActionPolicy(
            new FakeExtractor { MarkdownToReturn = Markdown },
            options,
            NullLogger<ExtractMarkdownCacheAiActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter(),
            new MarkdownResponseCache(opts.TransformedContentCache),
            new CacheKeyBuilder(),
            new CacheabilityEvaluator(),
            telemetry);
        registry.RegisterPolicy(policy);
    }
}
