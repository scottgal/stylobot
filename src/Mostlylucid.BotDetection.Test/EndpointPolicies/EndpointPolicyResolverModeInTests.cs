using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.EndpointPolicies;
using Mostlylucid.BotDetection.Identity.BrowserModes;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

/// <summary>
///     Composite-spec step 5: <see cref="EndpointPolicyRule.ModeIn"/>
///     allowlist evaluation. Rules with <c>mode_in</c> match only when the
///     classified browser mode is in the list; the classification is lazy
///     and request-cached so the resolver can call <see cref="IBrowserModeResolver"/>
///     once per request regardless of how many rules reference it.
///
///     Rules with <c>mode_in</c> declared but no <see cref="IBrowserModeResolver"/>
///     in DI fail closed (skip the rule + log loud at compile time) so a
///     misconfigured environment doesn't silently let mode-gated rules apply
///     to every request.
/// </summary>
public class EndpointPolicyResolverModeInTests
{
    private static ConfigEndpointPolicyResolver Build(
        IBrowserModeResolver? modes, params EndpointPolicyRule[] rules)
    {
        var opts = new EndpointPolicyOptions { Rules = rules.ToList() };
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance,
            modes);
    }

    private static HttpContext Req(string method, string path, Dictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (headers is not null)
            foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        return ctx;
    }

    private static IBrowserModeResolver MakeModeResolver() =>
        new CachingBrowserModeResolver(
            new BrowserModeRegistry(
                NullLogger<BrowserModeRegistry>.Instance, fallbackModeId: "unknown"));

    [Fact]
    public void ModeIn_Navigation_MatchesNavigationRequest()
    {
        var r = Build(MakeModeResolver(),
            new EndpointPolicyRule
            {
                Path = "/admin*",
                ModeIn = new List<string> { "navigation" },
                Action = "block",
                Reason = "admin path requires real navigation",
            });

        // Top-level navigation: Sec-Fetch-Dest+Mode+Site+User=15, UIR=1,
        // Accept includes text/html -> classified as `navigation`.
        var nav = Req("GET", "/admin", new()
        {
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9",
        });
        Assert.NotNull(r.Match(nav));
    }

    [Fact]
    public void ModeIn_Navigation_DoesNotMatchXhrToSamePath()
    {
        var r = Build(MakeModeResolver(),
            new EndpointPolicyRule
            {
                Path = "/admin*",
                ModeIn = new List<string> { "navigation" },
                Action = "block",
            });

        // XHR shape: Sec-Fetch-Dest=empty, Mode=cors, Site=same-origin (no User),
        // no UIR, Accept=*/* -> classified as `xhr`. Mode allowlist contains only
        // navigation, so the rule must NOT match.
        var xhr = Req("GET", "/admin/api/users", new()
        {
            ["Sec-Fetch-Dest"] = "empty",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Site"] = "same-origin",
            ["Accept"] = "*/*",
        });
        Assert.Null(r.Match(xhr));
    }

    [Fact]
    public void ModeIn_MultipleModes_MatchesAnyListed()
    {
        var r = Build(MakeModeResolver(),
            new EndpointPolicyRule
            {
                Path = "/api*",
                ModeIn = new List<string> { "xhr", "signalr-negotiate" },
                Action = "logonly",
            });

        var xhr = Req("GET", "/api/me", new()
        {
            ["Sec-Fetch-Dest"] = "empty",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Site"] = "same-origin",
            ["Accept"] = "*/*",
        });
        Assert.NotNull(r.Match(xhr));

        // navigation to /api/* (e.g. direct address-bar paste) -> not in the
        // allowlist -> the rule does not fire.
        var nav = Req("GET", "/api/me", new()
        {
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Accept"] = "text/html",
        });
        Assert.Null(r.Match(nav));
    }

    [Fact]
    public void ModeIn_NoResolverInDI_RuleFailsClosed()
    {
        // No IBrowserModeResolver registered. The rule has mode_in declared;
        // the resolver must fail closed (the rule never matches) so a config
        // drift doesn't silently turn off the gate.
        var r = Build(modes: null,
            new EndpointPolicyRule
            {
                Path = "/admin*",
                ModeIn = new List<string> { "navigation" },
                Action = "block",
            });

        var nav = Req("GET", "/admin", new()
        {
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Accept"] = "text/html",
        });
        Assert.Null(r.Match(nav));
    }

    [Fact]
    public void ModeIn_LazyAndCached_ResolverCalledOncePerRequest()
    {
        // A single request, two rules that both reference mode_in. The
        // resolver caches the classification in HttpContext.Items, so any
        // number of mode_in rules pay only one classification per request.
        var resolver = new CountingBrowserModeResolver();
        var r = Build(resolver,
            new EndpointPolicyRule { Path = "/never-matches", ModeIn = new List<string> { "xhr" }, Action = "block" },
            new EndpointPolicyRule { Path = "/admin*",        ModeIn = new List<string> { "navigation" }, Action = "block" });

        var ctx = Req("GET", "/admin", new()
        {
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Accept"] = "text/html",
        });
        Assert.NotNull(r.Match(ctx));

        // The path-mismatch rule short-circuits before reaching mode_in, so
        // the resolver should be called exactly once (for the /admin rule).
        Assert.Equal(1, resolver.Calls);
    }

    private sealed class CountingBrowserModeResolver : IBrowserModeResolver
    {
        public int Calls { get; private set; }
        public string Resolve(HttpContext context)
        {
            Calls++;
            return "navigation";
        }
    }

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}