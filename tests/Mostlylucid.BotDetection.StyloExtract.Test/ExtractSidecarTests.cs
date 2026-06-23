using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class ExtractSidecarTests
{
    [Fact]
    public async Task ExecuteAsync_returns_Allowed()
    {
        var policy = PolicyFactory.Sidecar();
        var context = HttpContextBuilder.CreateHtmlContext();
        var result = await policy.ExecuteAsync(context, Evidence.Bot());

        result.Continue.Should().BeTrue();
    }

    [Fact]
    public void Name_is_extract_sidecar()
    {
        PolicyFactory.Sidecar().Name.Should().Be("extract-sidecar");
    }

    [Fact]
    public async Task Link_header_is_added()
    {
        var policy = PolicyFactory.Sidecar();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Request.Path = "/blog/my-post";

        await policy.ExecuteAsync(context, Evidence.Bot());

        context.Response.Headers.Should().ContainKey("Link");
    }

    [Fact]
    public async Task Link_header_contains_rel_alternate()
    {
        var policy = PolicyFactory.Sidecar();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Request.Path = "/blog/my-post";

        await policy.ExecuteAsync(context, Evidence.Bot());

        var linkHeader = context.Response.Headers["Link"].ToString();
        linkHeader.Should().Contain("rel=\"alternate\"");
        linkHeader.Should().Contain("type=\"text/markdown\"");
    }

    [Theory]
    [InlineData("/blog/my-post", "/{path}.md", "/blog/my-post.md")]
    [InlineData("/articles/2025/hello", "/{path}.md", "/articles/2025/hello.md")]
    [InlineData("/page", "/{slug}.md", "/page.md")]
    [InlineData("/articles/slug", "/{slug}.md", "/slug.md")]
    public void BuildSidecarUrl_interpolates_template_correctly(
        string requestPath, string template, string expectedUrl)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = requestPath;

        var url = ExtractSidecarActionPolicy.BuildSidecarUrl(request, template);

        url.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task Custom_route_template_is_used()
    {
        var opts = new StyloExtractActionOptions { SidecarRouteTemplate = "/{slug}-md" };
        var policy = PolicyFactory.Sidecar(opts);
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Request.Path = "/articles/hello";

        await policy.ExecuteAsync(context, Evidence.Bot());

        var linkHeader = context.Response.Headers["Link"].ToString();
        linkHeader.Should().Contain("hello-md");
    }
}
