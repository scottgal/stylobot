using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.EndpointPolicies;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

public class EndpointPolicyResolverTests
{
    private static ConfigEndpointPolicyResolver Build(params EndpointPolicyRule[] rules)
    {
        var opts = new EndpointPolicyOptions { Rules = rules.ToList() };
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance);
    }

    private static HttpContext Req(string method, string path, string? host = null,
        string? accept = null, string? contentType = null, string? upgrade = null,
        string? protocol = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (host is not null) ctx.Request.Host = new HostString(host);
        if (accept is not null) ctx.Request.Headers["Accept"] = accept;
        if (contentType is not null) ctx.Request.ContentType = contentType;
        if (upgrade is not null) ctx.Request.Headers["Upgrade"] = upgrade;
        if (protocol is not null) ctx.Request.Protocol = protocol;
        return ctx;
    }

    [Fact]
    public void NoRules_NoMatch()
    {
        var r = Build();
        Assert.Null(r.Match(Req("GET", "/anything")));
    }

    [Fact]
    public void MethodMatch_AnyHost_AnyPath()
    {
        var r = Build(new EndpointPolicyRule { Method = "DELETE", Action = "block" });
        Assert.NotNull(r.Match(Req("DELETE", "/anything")));
        Assert.Null(r.Match(Req("GET", "/anything")));
    }

    [Fact]
    public void PathGlob_MatchesPrefix()
    {
        var r = Build(new EndpointPolicyRule { Path = "/admin*", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/admin")));
        Assert.NotNull(r.Match(Req("GET", "/admin/dashboard")));
        Assert.NotNull(r.Match(Req("GET", "/administrator")));   // glob NOT segment-bound
        Assert.Null(r.Match(Req("GET", "/billing")));
    }

    [Fact]
    public void PathExact_RequiresSegmentBoundary()
    {
        var r = Build(new EndpointPolicyRule { Path = "/admin", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/admin")));
        Assert.NotNull(r.Match(Req("GET", "/admin/dashboard")));
        Assert.Null(r.Match(Req("GET", "/administrator")));      // no segment boundary -> no match
    }

    [Fact]
    public void HostMatch_Exact()
    {
        var r = Build(new EndpointPolicyRule { Host = "blog.example.com", Method = "POST", Path = "/x", Action = "block" });
        Assert.NotNull(r.Match(Req("POST", "/x", host: "blog.example.com")));
        Assert.Null(r.Match(Req("POST", "/x", host: "shop.example.com")));
    }

    [Fact]
    public void HostMatch_LeadingWildcard()
    {
        var r = Build(new EndpointPolicyRule { Host = "*.example.com", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/anything", host: "blog.example.com")));
        Assert.NotNull(r.Match(Req("GET", "/anything", host: "a.b.example.com")));
        Assert.Null(r.Match(Req("GET", "/anything", host: "other.org")));
    }

    [Fact]
    public void MethodAny_MatchesAllMethods()
    {
        var r = Build(new EndpointPolicyRule { Method = "ANY", Path = "/grpc*", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/grpc/svc")));
        Assert.NotNull(r.Match(Req("POST", "/grpc/svc")));
        Assert.NotNull(r.Match(Req("PUT", "/grpc/svc")));
    }

    [Fact]
    public void Transport_MatchesGrpc()
    {
        var r = Build(new EndpointPolicyRule { Transport = "grpc", Action = "block" });
        Assert.NotNull(r.Match(Req("POST", "/svc", contentType: "application/grpc")));
        Assert.Null(r.Match(Req("POST", "/svc")));
    }

    [Fact]
    public void Transport_MatchesWebSocket()
    {
        var r = Build(new EndpointPolicyRule { Transport = "websocket", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/ws", upgrade: "websocket")));
        Assert.Null(r.Match(Req("GET", "/ws")));
    }

    [Fact]
    public void ProtocolVersion_MatchesHttp11()
    {
        var r = Build(new EndpointPolicyRule { ProtocolVersion = "1.1", Path = "/admin*", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/admin/x", protocol: "HTTP/1.1")));
        Assert.Null(r.Match(Req("GET", "/admin/x", protocol: "HTTP/2")));
    }

    [Fact]
    public void FirstMatchWins()
    {
        var r = Build(
            new EndpointPolicyRule { Method = "POST", Path = "/api*", Action = "throttle-stealth" },
            new EndpointPolicyRule { Method = "POST", Path = "/api/admin*", Action = "block" });

        // /api/admin/x matches BOTH rules; first one wins.
        var match = r.Match(Req("POST", "/api/admin/x"));
        Assert.Equal("throttle-stealth", match?.ActionPolicyName);
    }

    [Fact]
    public void EmptyAction_RuleSkipped()
    {
        var r = Build(
            new EndpointPolicyRule { Method = "GET", Action = "" },   // skipped at compile time
            new EndpointPolicyRule { Method = "GET", Action = "block" });

        Assert.Equal("block", r.Match(Req("GET", "/any"))?.ActionPolicyName);
    }

    [Fact]
    public void Disabled_NoMatch()
    {
        var opts = new EndpointPolicyOptions
        {
            Enabled = false,
            Rules = [new EndpointPolicyRule { Action = "block" }]
        };
        var r = new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance);
        Assert.Null(r.Match(Req("GET", "/anything")));
    }

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
