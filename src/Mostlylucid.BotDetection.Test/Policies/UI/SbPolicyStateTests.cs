using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Real-render coverage for the <c>sb-policy-state</c> card on the Policies tab. Drives the
///     Razor pipeline via a TestServer one-shot controller, mirroring the SbPolicyStackTests
///     harness. Asserts the spec's dashboard contract: a registered content-cache policy renders
///     ENABLED with its contributed params; a policy configured under <c>StyloExtract:Actions</c>
///     whose implementation is NOT registered renders NOT ENABLED.
/// </summary>
public sealed class SbPolicyStateTests
{
    [Fact]
    public async Task RegisteredPolicy_RendersEnabledWithContributedParams()
    {
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync("/_test/policy-state");

        Assert.Contains("data-policy-state", html);
        Assert.Contains("data-policy-state-row=\"content-cache-search\"", html);
        Assert.Contains("verdict-success", html);
        Assert.Contains("representation=Html", html);
        Assert.Contains("hits=0", html);
    }

    [Fact]
    public async Task ConfiguredButUnregisteredPolicy_RendersNotEnabled()
    {
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync("/_test/policy-state");

        // ghost-policy exists under StyloExtract:Actions but no implementation is registered.
        Assert.Contains("data-policy-state-row=\"ghost-policy\"", html);
        Assert.Contains("verdict-warning", html);
        Assert.Contains("configured but no implementation registered", html);
    }

    [Fact]
    public async Task MissingProvider_FailsLoudInsteadOfFabricating()
    {
        // No IPolicyStateProvider registered (the _Policies.cshtml gate would skip the card; the
        // one-shot controller bypasses the gate and invokes the component directly). The
        // component must fail loudly -- never fabricate an empty/blank registry baseline.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbPolicyStateTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStateViewComponent).Assembly);
        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        var client = app.GetTestClient();

        // The TestServer surfaces the activation failure directly (no exception handler is
        // registered) -- exactly the fail-loud contract: never a fabricated baseline.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("/_test/policy-state"));
    }

    // ---------------------------------------------------------------------
    // TestServer harness (mirrors SbPolicyStackTests)
    // ---------------------------------------------------------------------

    private static async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbPolicyStateTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStateViewComponent).Assembly);

        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StyloExtract:Actions:content-cache-search:TransformedContentCache:Enabled"] = "true",
                ["StyloExtract:Actions:ghost-policy:Profile"] = "RagFull",
            })
            .Build());

        builder.Services.AddSingleton(Options.Create(new BotDetectionOptions()));
        builder.Services.AddSingleton<IActionPolicyRegistry>(_ => new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            new IActionPolicy[] { new CachePolicyStub("content-cache-search") }));
        builder.Services.AddSingleton<IPolicyStateProvider, RegistryPolicyStateProvider>();

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        return app.GetTestClient();
    }

    /// <summary>Stand-in for a pack content-cache policy: contributes representation + counters.</summary>
    private sealed class CachePolicyStub : IActionPolicy, IPolicyStateContributor
    {
        public CachePolicyStub(string name) => Name = name;

        public string Name { get; }
        public ActionType ActionType => ActionType.Custom;
        public PolicyIntent Intent => PolicyIntent.Pass;

        public Task<Mostlylucid.BotDetection.Actions.ActionResult> ExecuteAsync(
            HttpContext context,
            AggregatedEvidence evidence,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Mostlylucid.BotDetection.Actions.ActionResult.Allowed("stub"));

        public IReadOnlyDictionary<string, object> EffectiveParams => new Dictionary<string, object>
        {
            ["representation"] = "Html",
            ["match"] = "all traffic routed to this policy",
            ["cacheMode"] = "enabled",
            ["hits"] = 0L,
        };

        public PolicyFiringStats? FiringStats => null;
    }
}

/// <summary>One-shot controller rendering the sb-policy-state component (lives here so the test assembly owns it).</summary>
[Route("/_test/policy-state")]
public sealed class PolicyStateTestController : Controller
{
    [HttpGet]
    public IActionResult Get() => ViewComponent("SbPolicyState");
}
