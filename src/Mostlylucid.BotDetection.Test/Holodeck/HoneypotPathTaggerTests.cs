using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Middleware;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HoneypotPathTaggerTests
{
    private static HoneypotPathTagger CreateTagger(
        List<string>? paths = null,
        RequestDelegate? next = null)
    {
        var options = Options.Create(new HolodeckOptions
        {
            HoneypotPaths = paths ?? ["/wp-login.php", "/.env", "/phpmyadmin", "/wp-admin"]
        });
        return new HoneypotPathTagger(next ?? (_ => Task.CompletedTask), options);
    }

    [Fact]
    public async Task ExactMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-login.php";
        await tagger.InvokeAsync(context);
        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
        Assert.Equal("/wp-login.php", context.Items["Holodeck.MatchedPath"]);
    }

    [Fact]
    public async Task CaseInsensitiveMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/WP-LOGIN.PHP";
        await tagger.InvokeAsync(context);
        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
    }

    [Fact]
    public async Task PrefixMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-admin/post.php";
        await tagger.InvokeAsync(context);
        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
    }

    [Fact]
    public async Task NoMatch_DoesNotTag()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/products/123";
        await tagger.InvokeAsync(context);
        Assert.False(context.Items.ContainsKey("Holodeck.IsHoneypotPath"));
    }

    [Fact]
    public async Task CallsNext()
    {
        var nextCalled = false;
        var tagger = CreateTagger(next: _ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = "/anything";
        await tagger.InvokeAsync(context);
        Assert.True(nextCalled);
    }
}
