using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;
using Mostlylucid.BotDetection.RateLimit;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

/// <summary>
///     End-to-end middleware integration via TestServer. Stands up a minimal
///     ASP.NET pipeline that mirrors the production wiring pattern: the
///     dispatcher runs before a stub "legacy enforcement" middleware. Asserts
///     that the dispatcher's Handled / FallThrough contract drives the right
///     downstream behaviour (short-circuit vs continue).
/// </summary>
public sealed class PolicyDispatchMiddlewareIntegrationTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    /// <summary>
    ///     Seeded Live Block rule -> response is the dispatcher's 403 envelope
    ///     and the legacy enforcement middleware never ran (no double-handling).
    /// </summary>
    [Fact]
    public async Task Seeded_live_block_rule_returns_403_and_legacy_path_does_not_run()
    {
        var rule = NewRule(new PolicyAction.Block());
        var (app, legacyCounter, terminalCounter) = await BuildAppAsync(rule);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/anything");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.True(resp.Headers.Contains(BlockActionHandler.PolicyHeader),
            $"Expected {BlockActionHandler.PolicyHeader} header on dispatched block response");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);

        // Legacy enforcement middleware MUST NOT have run -- the dispatcher
        // returned Handled and the middleware short-circuited.
        Assert.Equal(0, legacyCounter.Count);
        // Terminal "200 OK" MUST NOT have run either.
        Assert.Equal(0, terminalCounter.Count);
    }

    /// <summary>
    ///     Seeded Live Allow rule -> dispatcher falls through, the legacy
    ///     "enforce" middleware ALSO sees the request but treats the marker
    ///     as a directive to skip its block branch. Terminal 200 still wins.
    /// </summary>
    [Fact]
    public async Task Allow_rule_falls_through_and_marker_suppresses_legacy_block()
    {
        var rule = NewRule(new PolicyAction.Allow());
        var (app, legacyCounter, terminalCounter) = await BuildAppAsync(rule, legacyAlwaysBlocks: true);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/anything");

        Assert.Equal(1, legacyCounter.Count);
        Assert.Equal(1, terminalCounter.Count);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    ///     No rules at all -> dispatcher returns FallThrough; legacy path runs;
    ///     terminal middleware runs.
    /// </summary>
    [Fact]
    public async Task No_rules_dispatcher_falls_through_to_legacy_and_terminal()
    {
        var (app, legacyCounter, terminalCounter) = await BuildAppAsync(rule: null);
        var client = app.GetTestClient();

        var resp = await client.GetAsync("/anything");

        Assert.Equal(1, legacyCounter.Count);
        Assert.Equal(1, terminalCounter.Count);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- Test host wiring -------------------------------------------------

    private sealed class CallCounter { public int Count; }

    private async Task<(WebApplication App, CallCounter Legacy, CallCounter Terminal)> BuildAppAsync(
        PolicyRule? rule,
        bool legacyAlwaysBlocks = false)
    {
        var legacyCounter = new CallCounter();
        var terminalCounter = new CallCounter();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IPolicyRuleStore>(_ => new SingleRuleStore(rule));
        builder.Services.AddSingleton<ArmedRuleRegistry>();
        builder.Services.AddSingleton<ITokenBucketStore, InMemoryTokenBucketStore>();
        builder.Services.AddSingleton<IPolicyResolver>(sp =>
            new DefaultPolicyResolver(
                sp.GetRequiredService<IPolicyRuleStore>(),
                contributors: null,
                sp.GetRequiredService<ArmedRuleRegistry>()));

        builder.Services.AddSingleton<IPolicyActionHandler, AllowActionHandler>();
        builder.Services.AddSingleton<IPolicyActionHandler, BlockActionHandler>();
        builder.Services.AddSingleton<IPolicyActionHandler, ObserveActionHandler>();
        builder.Services.AddSingleton<IPolicyActionHandler, TagActionHandler>();
        builder.Services.AddSingleton<IPolicyActionHandler, ChallengeActionHandler>();
        builder.Services.AddSingleton<IPolicyActionHandler>(_ => new RateLimitActionHandler(store: null));
        builder.Services.AddSingleton<IPolicyActionHandler>(sp =>
            new ThrottleActionHandler(sp.GetRequiredService<ITokenBucketStore>()));
        builder.Services.AddSingleton<PolicyActionDispatcher>();

        var app = builder.Build();

        // STEP 1: dispatcher middleware -- mirrors the production wiring.
        app.Use(async (ctx, next) =>
        {
            var dispatcher = ctx.RequestServices.GetRequiredService<PolicyActionDispatcher>();
            var scope = new PolicyScope(Host: new HostScope.Endpoint(
                "example.com", string.Empty, ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : "/"));
            var signals = new Dictionary<string, object?>();
            var result = await dispatcher.DispatchAsync(ctx, scope, signals, ctx.RequestAborted);
            if (result == PolicyDispatchResult.Handled) return;
            await next();
        });

        // STEP 2: stub "legacy enforcement" -- counts invocations and
        // (optionally) writes a 403 unless the Allow marker is set. Mirrors
        // the BotDetectionMiddleware change.
        app.Use(async (ctx, next) =>
        {
            legacyCounter.Count++;
            var allowed = ctx.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey);
            if (legacyAlwaysBlocks && !allowed)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });

        // STEP 3: terminal 200 OK
        app.Run(ctx =>
        {
            terminalCounter.Count++;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await app.StartAsync();
        _apps.Add(app);
        return (app, legacyCounter, terminalCounter);
    }

    private static PolicyRule NewRule(PolicyAction action) => new(
        Id: Guid.NewGuid(),
        Scope: PolicyScope.Wildcard(),
        Priority: 100,
        Predicate: new PredicateNode.And(Array.Empty<PredicateNode>()),
        Action: action,
        Mode: PolicyMode.Live,
        Notes: "test",
        Source: "test",
        CreatedAt: DateTimeOffset.UtcNow,
        RevisionId: Guid.NewGuid());

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); } catch { /* tear-down race -- ignore */ }
            try { await app.DisposeAsync(); } catch { /* tear-down race -- ignore */ }
        }
    }

    /// <summary>
    ///     One-rule IPolicyRuleStore double for the integration test. Accepts
    ///     a null rule (acts as an empty corpus).
    /// </summary>
    private sealed class SingleRuleStore : IPolicyRuleStore
    {
        private readonly PolicyRule? _rule;
        public SingleRuleStore(PolicyRule? rule) => _rule = rule;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(_rule is null ? Array.Empty<PolicyRule>() : new[] { _rule });

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(
            IReadOnlyList<PolicyScope> scopePath,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(_rule is null ? Array.Empty<PolicyRule>() : new[] { _rule });

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rule?.Id == id ? _rule : null);

#pragma warning disable CS0067
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }
}
