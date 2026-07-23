using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Domains;

public sealed class BotDetectionMiddlewareRequestScopeTests
{
    private static DomainNormalizer Normalizer()
        => new(Options.Create(new DomainNormalizerOptions()), PublicSuffixList.LoadEmbedded());

    private sealed class FakeConnectionItems : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    }

    private static DefaultHttpContext WithConnectionItems(params (string key, object? value)[] items)
    {
        var ctx = new DefaultHttpContext();
        var feat = new FakeConnectionItems();
        foreach (var (k, v) in items) feat.Items[k] = v;
        ctx.Features.Set<IConnectionItemsFeature>(feat);
        return ctx;
    }

    [Fact]
    public void Resolve_prefers_validated_SNI_over_the_Host_header()
    {
        // Gateway served a cert for shop.example.com; the client's Host header claims something else.
        var ctx = WithConnectionItems(
            (TlsConnectionKeys.SniEvaluated, true),
            (TlsConnectionKeys.ValidatedSni, "shop.example.com"));
        ctx.Request.Host = new HostString("evil-spoof.attacker.test");

        var scope = Normalizer().Resolve(ctx);

        Assert.Equal("example.com", scope.Domain);      // from the validated SNI
        Assert.Equal("shop.example.com", scope.Host);
        Assert.False(ctx.Items.ContainsKey(HttpContextItemKeys.TlsSniNotServed));
    }

    [Fact]
    public void Resolve_evaluated_but_not_served_is_unknown_and_flags_the_signal()
    {
        // Gateway saw the handshake but served no matching cert (default cert / no-SNI / spoof).
        var ctx = WithConnectionItems((TlsConnectionKeys.SniEvaluated, true));
        ctx.Request.Host = new HostString("whatever.the.client.sent");

        var scope = Normalizer().Resolve(ctx);

        Assert.Equal("unknown", scope.Domain);          // never falls through to the Host header
        Assert.Equal(true, ctx.Items[HttpContextItemKeys.TlsSniNotServed]);
    }

    [Fact]
    public void Resolve_falls_back_to_Host_when_no_gateway_TLS_evaluation()
    {
        // Non-gateway topology (direct AddBotDetection / sidecar): no SNI feature at all.
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("direct.acme.com");

        var scope = Normalizer().Resolve(ctx);

        Assert.Equal("acme.com", scope.Domain);         // Host fallback preserved
        Assert.False(ctx.Items.ContainsKey(HttpContextItemKeys.TlsSniNotServed));
    }

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