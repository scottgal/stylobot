using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.EndpointPolicies;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

/// <summary>
///     Tests for the <see cref="EndpointPolicyRule.Source"/> matcher added in the
///     health-endpoint feature (Task 5). Rules with <c>source: internal</c> match
///     only loopback/RFC-1918 callers; <c>source: external</c> matches only
///     public-origin callers; <c>source: any</c> or null restores the prior
///     wildcard behaviour.
/// </summary>
public class EndpointPolicySourceMatcherTests
{
    // ---- helpers -------------------------------------------------------

    private static ConfigEndpointPolicyResolver Build(params EndpointPolicyRule[] rules)
    {
        var opts = new EndpointPolicyOptions { Rules = rules.ToList() };
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance);
    }

    /// <summary>
    ///     Simulates a default-rule-seeding scenario: operator rules come first
    ///     (as config binding would provide them), then default health rules are
    ///     appended last so first-match-wins lets operator rules override.
    /// </summary>
    private static ConfigEndpointPolicyResolver BuildWithDefaultHealthRules(
        params EndpointPolicyRule[] operatorRules)
    {
        var rules = new List<EndpointPolicyRule>(operatorRules);

        // Append the same defaults that PostConfigure would add.
        foreach (var path in HealthEndpointOptions.DefaultPaths)
        {
            rules.Add(new EndpointPolicyRule
            {
                Path = path,
                Source = "internal",
                Action = "allow",
                Reason = "health-probe-default"
            });
        }

        var opts = new EndpointPolicyOptions { Rules = rules };
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance);
    }

    private static HttpContext Req(string method, string path, bool loopback = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = loopback
            ? IPAddress.Loopback
            : IPAddress.Parse("203.0.113.42"); // TEST-NET-3 public IP
        return ctx;
    }

    // ---- Source = internal ---------------------------------------------

    [Fact]
    public void Source_Internal_MatchesLoopback()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: true)));
    }

    [Fact]
    public void Source_Internal_MatchesLoopbackSubPath()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health/liveness", loopback: true)));
    }

    [Fact]
    public void Source_Internal_DoesNotMatchPublicIp()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });
        Assert.Null(r.Match(Req("GET", "/health", loopback: false)));
    }

    [Fact]
    public void Source_Internal_MatchesPrivateRfc1918()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/health";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        Assert.NotNull(r.Match(ctx));
    }

    // ---- Source = external ---------------------------------------------

    [Fact]
    public void Source_External_MatchesPublicIp()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "external", Action = "block" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: false)));
    }

    [Fact]
    public void Source_External_DoesNotMatchLoopback()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "external", Action = "block" });
        Assert.Null(r.Match(Req("GET", "/health", loopback: true)));
    }

    // ---- Source = any / null (back-compat) -----------------------------

    [Fact]
    public void Source_Any_MatchesLoopback()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "any", Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: true)));
    }

    [Fact]
    public void Source_Any_MatchesPublicIp()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = "any", Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: false)));
    }

    [Fact]
    public void Source_Null_MatchesLoopback()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = null, Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: true)));
    }

    [Fact]
    public void Source_Null_MatchesPublicIp()
    {
        var r = Build(new EndpointPolicyRule { Path = "/health*", Source = null, Action = "allow" });
        Assert.NotNull(r.Match(Req("GET", "/health", loopback: false)));
    }

    // ---- Default health rule + operator override -----------------------

    [Fact]
    public void DefaultHealthRule_AllowsInternalCallerOnHealthPath()
    {
        var r = BuildWithDefaultHealthRules();
        var match = r.Match(Req("GET", "/health", loopback: true));
        Assert.NotNull(match);
        Assert.Equal("allow", match!.ActionPolicyName);
    }

    [Fact]
    public void DefaultHealthRule_DoesNotMatchExternalCallerOnHealthPath()
    {
        var r = BuildWithDefaultHealthRules();
        // No rule should match an external caller hitting /health
        // (the default rule is Source=internal only).
        var match = r.Match(Req("GET", "/health", loopback: false));
        Assert.Null(match);
    }

    [Fact]
    public void OperatorRuleOverridesDefaultHealthRule()
    {
        // Operator declares a stricter rule first; first-match-wins means
        // the operator rule fires before the default allow rule.
        var r = BuildWithDefaultHealthRules(
            new EndpointPolicyRule { Path = "/health*", Action = "block" });

        var match = r.Match(Req("GET", "/health", loopback: true));
        Assert.Equal("block", match?.ActionPolicyName);
    }

    [Fact]
    public void OperatorExternalBlockRuleOverridesDefault()
    {
        // Operator wants to block external health checks.
        var r = BuildWithDefaultHealthRules(
            new EndpointPolicyRule { Path = "/health*", Source = "external", Action = "block" });

        // External caller: operator block rule fires.
        var extMatch = r.Match(Req("GET", "/health", loopback: false));
        Assert.Equal("block", extMatch?.ActionPolicyName);

        // Internal caller: operator rule doesn't match (Source=external),
        // falls through to the default allow rule.
        var intMatch = r.Match(Req("GET", "/health", loopback: true));
        Assert.Equal("allow", intMatch?.ActionPolicyName);
    }

    // ---- Source = internal + TrustedProxyIps CIDR matching ----------------

    private static ConfigEndpointPolicyResolver BuildWithTrustedIps(
        IEnumerable<string> trustedProxyIps,
        params EndpointPolicyRule[] rules)
    {
        var policyOpts = new EndpointPolicyOptions { Rules = rules.ToList() };
        var botOpts = new BotDetectionOptions();
        botOpts.TransportTrust.TrustedProxyIps.AddRange(trustedProxyIps);
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(policyOpts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance,
            modes: null,
            botOptions: new TestMonitor<BotDetectionOptions>(botOpts));
    }

    [Fact]
    public void Source_Internal_MatchesTrustedProxyCidr()
    {
        // Peer 10.100.0.5 falls inside the /24 CIDR configured as a trusted proxy.
        var r = BuildWithTrustedIps(
            ["10.100.0.0/24"],
            new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/health";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.100.0.5");
        Assert.NotNull(r.Match(ctx));
    }

    [Fact]
    public void Source_Internal_DoesNotMatchOutsideTrustedCidr()
    {
        // Peer 10.100.1.5 is outside the /24 and is not loopback or RFC-1918 private
        // (NetworkHelper.IsLocalIp would still catch RFC-1918 — use a public IP for clarity).
        var r = BuildWithTrustedIps(
            ["10.100.0.0/24"],
            new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/health";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.99"); // TEST-NET-3 public
        Assert.Null(r.Match(ctx));
    }

    [Fact]
    public void Source_Internal_MatchesMappedIpv4PeerAgainstIpv4Entry()
    {
        // Kestrel dual-stack may present 10.0.0.1 as ::ffff:10.0.0.1.
        // The CIDR check must unmap before comparing.
        var r = BuildWithTrustedIps(
            ["10.0.0.1"],
            new EndpointPolicyRule { Path = "/health*", Source = "internal", Action = "allow" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/health";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.1");
        Assert.NotNull(r.Match(ctx));
    }

    // ---- Helpers -------------------------------------------------------

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
