using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Domains;

public sealed class BotDetectionMiddlewareRequestScopeTests
{
    private static DomainNormalizer Normalizer()
        => new(Options.Create(new DomainNormalizerOptions()), PublicSuffixList.LoadEmbedded());

    [Fact]
    public void Resolve_stores_RequestScope_on_HttpContextItems()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("Auth.Stylo.Bot", 443);

        var n = Normalizer();
        var scope = n.Resolve(ctx);

        Assert.Equal("stylo.bot", scope.Domain);
        Assert.Equal("auth.stylo.bot", scope.Host);
        Assert.Equal("stylo.bot", ctx.Items[HttpContextItemKeys.Domain]);
        Assert.Equal("auth.stylo.bot", ctx.Items[HttpContextItemKeys.Host]);
    }

    [Fact]
    public void Resolve_caches_after_first_call()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("acme.com");

        var n = Normalizer();
        var first = n.Resolve(ctx);
        var cached = n.Resolve(ctx);

        Assert.Equal(first, cached);
        Assert.Equal(first, (RequestScope)ctx.Items[HttpContextItemKeys.RequestScope]!);
    }
}