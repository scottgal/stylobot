using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.EndpointPolicies;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

/// <summary>
///     Tests for the C4a external-matcher seam: <see cref="IEndpointPolicyRuleExtension"/>,
///     the resolver's extension iteration, and the config binder that recovers
///     unknown keys into <see cref="EndpointPolicyRule.Extensions"/>.
/// </summary>
public class EndpointPolicyExtensionSeamTests
{
    private static ConfigEndpointPolicyResolver Build(
        EndpointPolicyRule[] rules,
        params IEndpointPolicyRuleExtension[] extensions)
    {
        var opts = new EndpointPolicyOptions { Rules = rules.ToList() };
        return new ConfigEndpointPolicyResolver(
            new TestMonitor<EndpointPolicyOptions>(opts),
            NullLogger<ConfigEndpointPolicyResolver>.Instance,
            modes: null,
            botOptions: null,
            extensions: extensions);
    }

    private static HttpContext Req(string method, string path, string? testHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (testHeader is not null) ctx.Request.Headers["X-Test-Header"] = testHeader;
        return ctx;
    }

    private static EndpointPolicyRule RuleWithExtension(string key, object? payload)
    {
        var rule = new EndpointPolicyRule { Path = "/api/premium*", Action = "block" };
        rule.Extensions[key] = payload;
        return rule;
    }

    // ── resolver iteration ────────────────────────────────────────────────────

    [Fact]
    public void Extension_Votes_True_RuleMatches()
    {
        var rule = RuleWithExtension("requiresCapability", new Dictionary<string, object?> { ["value"] = "enterprise" });
        var ext = new StubExtension("requiresCapability", _ => true);

        var r = Build(new[] { rule }, ext);

        Assert.NotNull(r.Match(Req("GET", "/api/premium/report")));
    }

    [Fact]
    public void Extension_Votes_False_RuleDoesNotMatch()
    {
        var rule = RuleWithExtension("requiresCapability", new Dictionary<string, object?> { ["value"] = "enterprise" });
        var ext = new StubExtension("requiresCapability", _ => false);

        var r = Build(new[] { rule }, ext);

        Assert.Null(r.Match(Req("GET", "/api/premium/report")));
    }

    [Fact]
    public void Extension_That_Throws_FailsClosed_NoMatch_NoException()
    {
        var rule = RuleWithExtension("requiresCapability", new Dictionary<string, object?>());
        var ext = new StubExtension("requiresCapability", _ => throw new InvalidOperationException("boom"));

        var r = Build(new[] { rule }, ext);

        // Fail-closed: the throw is swallowed and treated as no-match, never a 500.
        var match = r.Match(Req("GET", "/api/premium/report"));
        Assert.Null(match);
    }

    [Fact]
    public void Extension_Reads_HttpContext_Header_And_Votes()
    {
        // Proves the HttpContext-only context is sufficient for a self-contained
        // matcher (no SignalSink needed).
        var rule = RuleWithExtension("requiresHeader", new Dictionary<string, object?> { ["value"] = "let-me-in" });
        var ext = new StubExtension("requiresHeader",
            ctx => ctx.HttpContext.Request.Headers["X-Test-Header"] == "let-me-in");

        var r = Build(new[] { rule }, ext);

        Assert.NotNull(r.Match(Req("GET", "/api/premium/x", testHeader: "let-me-in")));
        Assert.Null(r.Match(Req("GET", "/api/premium/x", testHeader: "nope")));
        Assert.Null(r.Match(Req("GET", "/api/premium/x")));
    }

    [Fact]
    public void Extension_Receives_The_RulePayload_SubTree()
    {
        IReadOnlyDictionary<string, object?>? seen = null;
        var payload = new Dictionary<string, object?> { ["claim"] = "stylobot.tier", ["value"] = "enterprise" };
        var rule = RuleWithExtension("requiresCapability", payload);
        var ext = new StubExtension("requiresCapability", ctx => { seen = ctx.RulePayload; return true; });

        var r = Build(new[] { rule }, ext);
        r.Match(Req("GET", "/api/premium/x"));

        Assert.NotNull(seen);
        Assert.Equal("stylobot.tier", seen!["claim"]);
        Assert.Equal("enterprise", seen["value"]);
    }

    [Fact]
    public void ZeroExtensions_RuleWithExtensionKey_MatchesOnBakedInFields()
    {
        // FOSS / back-compat: no extension registered for the key => the seam is
        // inert and the rule matches purely on its baked-in matchers.
        var rule = RuleWithExtension("requiresCapability", new Dictionary<string, object?>());

        var r = Build(new[] { rule }); // no extensions

        Assert.NotNull(r.Match(Req("GET", "/api/premium/report")));
    }

    [Fact]
    public void UnrelatedExtension_DoesNotConsult_RuleWithoutThatKey()
    {
        var rule = new EndpointPolicyRule { Path = "/api/premium*", Action = "block" }; // no Extensions
        var called = false;
        var ext = new StubExtension("requiresCapability", _ => { called = true; return false; });

        var r = Build(new[] { rule }, ext);

        // Rule has no matching Extensions key, so the extension is never consulted.
        Assert.NotNull(r.Match(Req("GET", "/api/premium/report")));
        Assert.False(called);
    }

    [Fact]
    public void ExistingRule_NoExtensions_Unaffected()
    {
        // Pins that the iteration site is dead code for ordinary rules.
        var rule = new EndpointPolicyRule { Method = "DELETE", Action = "block" };
        var ext = new StubExtension("requiresCapability", _ => false);

        var r = Build(new[] { rule }, ext);

        Assert.NotNull(r.Match(Req("DELETE", "/anything")));
        Assert.Null(r.Match(Req("GET", "/anything")));
    }

    // ── config binder ─────────────────────────────────────────────────────────

    [Fact]
    public void Binder_Collects_UnknownKey_As_Dictionary()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:EndpointPolicies:Rules:0:Path"] = "/api/premium/**",
                ["BotDetection:EndpointPolicies:Rules:0:Action"] = "block",
                ["BotDetection:EndpointPolicies:Rules:0:requiresCapability:claim"] = "stylobot.tier",
                ["BotDetection:EndpointPolicies:Rules:0:requiresCapability:value"] = "enterprise",
            })
            .Build();

        var opts = new EndpointPolicyOptions();
        config.GetSection(EndpointPolicyOptions.SectionName).Bind(opts);
        EndpointPolicyRuleExtensionsBinder.Collect(opts, config);

        var rule = Assert.Single(opts.Rules);
        // Baked-in fields still bound normally.
        Assert.Equal("/api/premium/**", rule.Path);
        Assert.Equal("block", rule.Action);
        // Unknown key recovered into Extensions as a sub-tree dictionary.
        Assert.True(rule.Extensions.ContainsKey("requiresCapability"));
        var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(rule.Extensions["requiresCapability"]);
        Assert.Equal("stylobot.tier", payload["claim"]);
        Assert.Equal("enterprise", payload["value"]);
    }

    [Fact]
    public void Binder_Collects_Array_As_List()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:EndpointPolicies:Rules:0:Path"] = "/x",
                ["BotDetection:EndpointPolicies:Rules:0:Action"] = "block",
                ["BotDetection:EndpointPolicies:Rules:0:anyOf:0"] = "a",
                ["BotDetection:EndpointPolicies:Rules:0:anyOf:1"] = "b",
            })
            .Build();

        var opts = new EndpointPolicyOptions();
        config.GetSection(EndpointPolicyOptions.SectionName).Bind(opts);
        EndpointPolicyRuleExtensionsBinder.Collect(opts, config);

        var rule = Assert.Single(opts.Rules);
        var list = Assert.IsType<List<object?>>(rule.Extensions["anyOf"]);
        Assert.Equal(new object?[] { "a", "b" }, list);
    }

    [Fact]
    public void Binder_DoesNotCollect_BakedInKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:EndpointPolicies:Rules:0:Host"] = "example.com",
                ["BotDetection:EndpointPolicies:Rules:0:Method"] = "POST",
                ["BotDetection:EndpointPolicies:Rules:0:Path"] = "/x",
                ["BotDetection:EndpointPolicies:Rules:0:Source"] = "external",
                ["BotDetection:EndpointPolicies:Rules:0:Action"] = "block",
                ["BotDetection:EndpointPolicies:Rules:0:StatusCode"] = "403",
                ["BotDetection:EndpointPolicies:Rules:0:Reason"] = "nope",
            })
            .Build();

        var opts = new EndpointPolicyOptions();
        config.GetSection(EndpointPolicyOptions.SectionName).Bind(opts);
        EndpointPolicyRuleExtensionsBinder.Collect(opts, config);

        var rule = Assert.Single(opts.Rules);
        Assert.Empty(rule.Extensions);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private sealed class StubExtension(string ruleName, Func<EndpointPolicyExtensionContext, bool> vote)
        : IEndpointPolicyRuleExtension
    {
        public string RuleName { get; } = ruleName;
        public bool Matches(EndpointPolicyExtensionContext context) => vote(context);
    }

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
