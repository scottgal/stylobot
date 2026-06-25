using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.Policies.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Task 18: end-to-end coverage for the <see cref="IPolicyCanEditPolicy"/>
///     gate driving the view component. The FOSS default
///     (<see cref="AlwaysReadOnlyPolicyCanEditPolicy"/>) MUST render zero
///     edit affordances; replacing the binding with a stub that grants
///     edit MUST surface the drag handle (and the rest of the edit
///     toolkit gated by <c>Model.CanEdit</c>).
/// </summary>
public sealed class PolicyCanEditGateTests : IAsyncDisposable
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    private const string DomainAcme = "acme.com";
    private const string SubDocs = "docs.acme.com";
    private const string EpUpload = "GET /api/upload";

    private static readonly PolicyScope EndpointScope = PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task FOSS_default_gate_renders_no_edit_affordances()
    {
        // No override on the test controller -> view component consults
        // IPolicyCanEditPolicy -> FOSS default returns false everywhere.
        var client = await BuildClientAsync(canEditPolicy: new AlwaysReadOnlyPolicyCanEditPolicy());

        var html = await GetHtmlAsync(client);

        // No drag handle (Task 19 / SortableJS), no pencil button, no
        // "+ Add rule" buttons, no promote-observe controls. The gate
        // suppresses every edit surface in one swing.
        Assert.DoesNotContain("data-policy-stack-drag-handle", html);
        Assert.DoesNotContain("sb-policy-stack-drag-handle", html);
        Assert.DoesNotContain("data-policy-stack-edit-rule", html);
        Assert.DoesNotContain("data-policy-stack-add-rule", html);
        Assert.DoesNotContain("data-policy-stack-promote-observe", html);
    }

    [Fact]
    public async Task Replaced_gate_grants_edit_renders_drag_handle()
    {
        // Stub gate that says yes regardless of principal. The test
        // controller passes no canEdit override, so the service decides.
        var client = await BuildClientAsync(canEditPolicy: new AlwaysCanEditPolicy());

        var html = await GetHtmlAsync(client);

        // Endpoint rule sits at the queried scope so its drag handle
        // appears exactly once when the gate grants edit.
        Assert.Contains("data-policy-stack-drag-handle", html);
        var handleCount = new Regex("data-policy-stack-drag-handle").Matches(html).Count;
        Assert.Equal(1, handleCount);
    }

    [Fact]
    public async Task Override_param_wins_over_gate_decision()
    {
        // Gate says yes, but the controller pins canEdit:false. The
        // override path is the documented escape hatch for tests and
        // snapshot views.
        var client = await BuildClientAsync(canEditPolicy: new AlwaysCanEditPolicy());

        var html = await GetHtmlAsync(client, canEditOverride: false);

        Assert.DoesNotContain("data-policy-stack-drag-handle", html);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    // -------- Helpers --------

    private async Task<HttpClient> BuildClientAsync(IPolicyCanEditPolicy canEditPolicy)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(PolicyCanEditGateTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStackViewComponent).Assembly);

        builder.Services.AddSingleton<ISignalCatalog>(_ =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
#pragma warning disable IL2026
            return SignalCatalog.LoadAsync(asm).GetAwaiter().GetResult();
#pragma warning restore IL2026
        });
        builder.Services.AddSingleton<IPolicyRuleStore>(_ =>
        {
            var asm = typeof(PolicyRule).Assembly;
            var store = YamlPolicyRuleStore.FromEmbeddedResources(asm, SeedPrefix);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });
        builder.Services.AddSingleton<IPolicyResolver, DefaultPolicyResolver>();
        builder.Services.AddSingleton<IPolicyDecisionLog, InMemoryPolicyDecisionLog>();
        builder.Services.AddSingleton<IPolicyEffectivenessCache>(sp =>
            new PolicyEffectivenessCache(
                sp.GetRequiredService<IPolicyDecisionLog>(),
                Options.Create(new PolicyEffectivenessOptions()),
                NullLogger<PolicyEffectivenessCache>.Instance));
        builder.Services.AddSingleton<PolicyConflictAnalyzer>();
        builder.Services.AddSingleton<PolicyExplainerPresenter>();
        builder.Services.AddSingleton<PolicyStackPresenter>();
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IFacetPickerCatalog, Mostlylucid.BotDetection.UI.Services.FacetPickerCatalog>();
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.FacetPillRenderer>();
        builder.Services.AddSingleton(canEditPolicy);
        // Resolver-driven dashboard URLs replaced hard-coded /dashboard prefixes
        // in the policy-stack partials; tests that render those partials must
        // resolve IDashboardLinkResolver or @inject fails to activate the page.
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IDashboardLinkResolver>(
            new Mostlylucid.BotDetection.UI.Services.DashboardLinkResolver(
                new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions { BasePath = "/stylobot" }));

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, bool? canEditOverride = null)
    {
        var query =
            $"embed=Full&scopeKind=endpoint&domain={DomainAcme}&sub={SubDocs}&template={Uri.EscapeDataString(EpUpload)}";
        if (canEditOverride is not null)
            query += $"&canEdit={(canEditOverride.Value ? "true" : "false")}";
        var resp = await client.GetAsync($"/_test/policy-stack?{query}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private sealed class AlwaysCanEditPolicy : IPolicyCanEditPolicy
    {
        public bool CanEdit(ClaimsPrincipal? user) => true;
    }
}