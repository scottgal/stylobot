using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Coverage for the remote config-baseline read-through: a thin-client dashboard renders the
///     GATEWAY's config baseline (BotTypeActionPolicies -> action) instead of skipping it, via
///     <see cref="IConfigBaselineProvider"/> (local composer OR remote reader) and the
///     <c>GET /api/v1/policies/config-baseline</c> endpoint.
/// </summary>
public class ConfigBaselineReadThroughTests
{
    // --- AOT: the endpoint's response type must round-trip through the source-gen context ---------
    // (same class of guard as ComposeBatchJsonContractTests -- the AOT gateway is source-gen-only).
    [Fact]
    public void ConfigBaseline_response_roundtrips_through_source_gen_context()
    {
        StyloBotJsonContext.Default.GetTypeInfo(typeof(SingleResponse<IReadOnlyList<ConfigPolicyRowViewModel>>))
            .Should().NotBeNull("the config-baseline endpoint serves this on the AOT gateway");

        var payload = new SingleResponse<IReadOnlyList<ConfigPolicyRowViewModel>>
        {
            Data = new[]
            {
                new ConfigPolicyRowViewModel("Scraper", "throttle-aggressive", "verdict-warning",
                    "BotDetection:BotTypeActionPolicies:Scraper", EffectivePolicyConfigSource.BotTypeActionPolicies)
            },
            Meta = new ResponseMeta()
        };

        var json = JsonSerializer.Serialize(
            payload, typeof(SingleResponse<IReadOnlyList<ConfigPolicyRowViewModel>>), StyloBotJsonContext.Default);
        var back = (SingleResponse<IReadOnlyList<ConfigPolicyRowViewModel>>?)JsonSerializer.Deserialize(
            json, typeof(SingleResponse<IReadOnlyList<ConfigPolicyRowViewModel>>), StyloBotJsonContext.Default);

        back.Should().NotBeNull();
        back!.Data.Should().ContainSingle();
        back.Data[0].Target.Should().Be("Scraper");
        back.Data[0].ResultingAction.Should().Be("throttle-aggressive");
    }

    // --- Local: the composer IS the provider; the seam returns what ComposeConfigRows returns ------
    [Fact]
    public async Task Local_composer_satisfies_the_provider_seam()
    {
        var opts = new BotDetectionOptions
        {
            DefaultActionPolicyName = "throttle-status",
            BotTypeActionPolicies = new() { ["Scraper"] = "throttle-aggressive" }
        };
        IConfigBaselineProvider provider = new EffectivePolicyComposer(
            OptionsMonitor(opts),
            Options.Create(new Mostlylucid.BotDetection.EndpointPolicies.DetectionPolicyOptions()),
            Registry(("throttle-aggressive", ActionType.Throttle), ("throttle-status", ActionType.Throttle)),
            new PassthroughEffectivePolicyConfigOverlay());

        var rows = await provider.GetConfigRowsAsync(canEdit: false);

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Source == EffectivePolicyRowSource.Config && r.ConfigRow != null);
        rows.Select(r => r.ConfigRow!.Target).Should().Contain("Scraper");
    }

    // --- Remote: reads the gateway envelope and re-wraps each row as a config-source row -----------
    [Fact]
    public async Task Remote_provider_reads_gateway_envelope_and_wraps_config_rows()
    {
        const string body = """
            {"data":[
              {"target":"Scraper","resultingAction":"throttle-aggressive","actionColorClass":"verdict-warning",
               "configKey":"BotDetection:BotTypeActionPolicies:Scraper","source":0,"supersededByRuleId":null,"canEdit":false}
            ],"meta":{}}
            """;
        var api = new GatewayApiClient(FakeHttp(body), NullLogger<GatewayApiClient>.Instance);
        var provider = new RemoteConfigBaselineProvider(api);

        var rows = await provider.GetConfigRowsAsync(canEdit: false);

        rows.Should().ContainSingle();
        rows[0].Source.Should().Be(EffectivePolicyRowSource.Config);
        rows[0].ConfigRow!.Target.Should().Be("Scraper");
        rows[0].ConfigRow!.ResultingAction.Should().Be("throttle-aggressive");
    }

    [Fact]
    public async Task Remote_provider_returns_empty_on_gateway_failure_never_fabricates()
    {
        var api = new GatewayApiClient(FakeHttp("", HttpStatusCode.ServiceUnavailable), NullLogger<GatewayApiClient>.Instance);
        var provider = new RemoteConfigBaselineProvider(api);

        (await provider.GetConfigRowsAsync(canEdit: false)).Should().BeEmpty(
            "a gateway failure renders nothing, never a fabricated baseline");
    }

    // --- helpers ----------------------------------------------------------------------------------
    private static IOptions<BotDetectionOptions> OptionsMonitor(BotDetectionOptions opts)
        => Microsoft.Extensions.Options.Options.Create(opts);

    private static IActionPolicyRegistry Registry(params (string Name, ActionType Type)[] policies)
    {
        var m = new Mock<IActionPolicyRegistry>();
        foreach (var (name, type) in policies)
        {
            var p = new Mock<IActionPolicy>();
            p.SetupGet(x => x.Name).Returns(name);
            p.SetupGet(x => x.ActionType).Returns(type);
            m.Setup(r => r.GetPolicy(name)).Returns(p.Object);
        }
        return m.Object;
    }

    private static HttpClient FakeHttp(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(body, status)) { BaseAddress = new Uri("http://gateway.test") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public StubHandler(string body, HttpStatusCode status) { _body = body; _status = status; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
