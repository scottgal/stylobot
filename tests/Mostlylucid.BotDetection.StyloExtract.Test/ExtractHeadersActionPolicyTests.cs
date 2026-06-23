using System.Text;
using FluentAssertions;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class ExtractHeadersActionPolicyTests
{
    private const string Html = "<html><body><h1>Hello</h1></body></html>";

    [Fact]
    public async Task ExecuteAsync_returns_Allowed()
    {
        var policy = PolicyFactory.Headers();
        var context = HttpContextBuilder.CreateHtmlContext();
        var result = await policy.ExecuteAsync(context, Evidence.Bot());

        result.Continue.Should().BeTrue();
    }

    [Fact]
    public void Name_is_extract_headers()
    {
        var policy = PolicyFactory.Headers();
        policy.Name.Should().Be("extract-headers");
    }

    [Fact]
    public async Task X_StyloExtract_Match_Status_header_is_present()
    {
        var fake = new FakeExtractor();
        var policy = PolicyFactory.Headers(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        context.Response.Headers.Should().ContainKey("X-StyloExtract-Match-Status");
    }

    [Fact]
    public async Task X_StyloExtract_Markdown_Length_is_positive()
    {
        var fake = new FakeExtractor { MarkdownToReturn = "# Hello\n\nContent.\n" };
        var policy = PolicyFactory.Headers(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        var lengthHeader = context.Response.Headers["X-StyloExtract-Markdown-Length"].ToString();
        int.Parse(lengthHeader).Should().BePositive();
    }

    [Fact]
    public async Task Body_is_unchanged_html()
    {
        var policy = PolicyFactory.Headers();
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        var (body, _) = await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        // Body should contain the original HTML (unchanged).
        body.Should().Be(Html);
    }

    [Fact]
    public async Task X_StyloExtract_Title_header_is_present_when_title_available()
    {
        var fake = new FakeExtractor(); // returns Title = "Test Title"
        var policy = PolicyFactory.Headers(fake);
        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.Body = originalBody;

        await ActionPolicyRunner.RunAndFlushAsync(
            context,
            c => policy.ExecuteAsync(c, Evidence.Bot()),
            Html,
            originalBody);

        context.Response.Headers.Should().ContainKey("X-StyloExtract-Title");
        context.Response.Headers["X-StyloExtract-Title"].ToString().Should().Be("Test Title");
    }
}
