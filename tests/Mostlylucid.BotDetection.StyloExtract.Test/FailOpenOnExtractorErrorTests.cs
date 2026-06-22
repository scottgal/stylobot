using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

/// <summary>
/// Verifies that any exception thrown by the extractor results in the original HTML
/// being returned unchanged (fail-open) with a logged Warning.
/// </summary>
public sealed class FailOpenOnExtractorErrorTests
{
    private const string Html = "<html><body><p>Original content</p></body></html>";

    [Fact]
    public async Task Extractor_throw_on_markdown_policy_returns_original_html()
    {
        var throwing = new ThrowingExtractor();
        var policy = PolicyFactory.Markdown(throwing);

        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        var actionResult = await policy.ExecuteAsync(context, Evidence.Bot());
        actionResult.Continue.Should().BeTrue();

        // Write HTML downstream.
        var bytes = Encoding.UTF8.GetBytes(Html);
        await context.Response.Body.WriteAsync(bytes);
        await context.Response.Body.FlushAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var body = Encoding.UTF8.GetString(originalBody.ToArray());

        body.Should().Be(Html);
    }

    [Fact]
    public async Task Extractor_throw_on_headers_policy_returns_original_html_no_headers()
    {
        var throwing = new ThrowingExtractor();
        var policy = PolicyFactory.Headers(throwing);

        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        var actionResult = await policy.ExecuteAsync(context, Evidence.Bot());
        actionResult.Continue.Should().BeTrue();

        var bytes = Encoding.UTF8.GetBytes(Html);
        await context.Response.Body.WriteAsync(bytes);
        await context.Response.Body.FlushAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var body = Encoding.UTF8.GetString(originalBody.ToArray());

        // Original body preserved.
        body.Should().Be(Html);
        // No X-StyloExtract-* headers.
        context.Response.Headers.Should().NotContainKey("X-StyloExtract-Match-Status");
    }

    [Fact]
    public async Task Extractor_throw_does_not_propagate_exception_to_caller()
    {
        var throwing = new ThrowingExtractor();
        var policy = PolicyFactory.Markdown(throwing);
        var context = HttpContextBuilder.CreateHtmlContext();

        // ExecuteAsync itself must not throw even when extractor is broken.
        var act = async () => await policy.ExecuteAsync(context, Evidence.Bot());

        await act.Should().NotThrowAsync();
    }
}
