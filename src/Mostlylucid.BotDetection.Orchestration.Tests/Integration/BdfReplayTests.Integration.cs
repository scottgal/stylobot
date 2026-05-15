using System.Net.Http.Json;
using Mostlylucid.BotDetection.Endpoints;
using Mostlylucid.BotDetection.Models;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Replays canonical BDF scenarios in <c>test-suites/</c> against the running Demo
///     via <c>/bot-detection/bdf-replay/replay</c> (which routes through the active
///     <see cref="IDetectionOrchestrator"/> under <c>DetectionPolicy.Default</c>) and
///     asserts on the read surface — bot name, risk band, signal-presence probes.
///
///     This rig exists because the failure class it catches (downstream consumers of
///     <c>ev.Signals</c> degrading silently when the orchestrator stops merging signals)
///     does not fail any unit test. See <c>docs/architecture/signal-contracts.md</c>.
/// </summary>
[Collection("DemoApp")]
public sealed class BdfReplayTests
{
    private static readonly string TestSuitesRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-suites"));

    private readonly DemoAppFactory _demo;
    private readonly ITestOutputHelper _output;

    public BdfReplayTests(DemoAppFactory demo, ITestOutputHelper output)
    {
        _demo = demo;
        _output = output;
    }

    public static IEnumerable<object[]> BotScenarios() => DiscoverScenarios("bots");
    public static IEnumerable<object[]> HumanScenarios() => DiscoverScenarios("humans");

    [Theory]
    [MemberData(nameof(BotScenarios))]
    public async Task BotScenario_DetectsAsBot_AndPipelineFeedsDownstreamSignals(string scenarioFile)
    {
        var response = await ReplayAsync(scenarioFile);
        Assert.NotNull(response);
        Assert.NotEmpty(response.Results);

        // Aggregate across the scenario: the LAST request reflects the matured verdict.
        var last = response.Results[^1];
        Assert.NotNull(last.Actual);

        Assert.True(last.Actual!.IsBot,
            $"{response.ScenarioName}: last request scored {last.Actual.BotProbability:F2}, expected bot");

        AssertSignalsFlowed(response.ScenarioName, last);

        _output.WriteLine(
            $"{response.ScenarioName}: bot={last.Actual.IsBot} prob={last.Actual.BotProbability:F2} " +
            $"band={last.Actual.RiskBand} name={last.Actual.BotName} signals={last.Actual.SignalCount}");
    }

    [Theory]
    [MemberData(nameof(HumanScenarios))]
    public async Task HumanScenario_DoesNotMisclassify_AndPipelineFeedsDownstreamSignals(string scenarioFile)
    {
        var response = await ReplayAsync(scenarioFile);
        Assert.NotNull(response);
        Assert.NotEmpty(response.Results);

        var last = response.Results[^1];
        Assert.NotNull(last.Actual);

        // Some heuristics legitimately escalate on outlier rates; assert majority human, not all.
        var humanCount = response.Results.Count(r => r.Actual is { IsBot: false });
        var botCount = response.Results.Count - humanCount;
        Assert.True(humanCount >= botCount,
            $"{response.ScenarioName}: {botCount}/{response.Results.Count} requests classified as bot, " +
            $"expected majority human. Last verdict: {last.Actual!.RiskBand} prob={last.Actual.BotProbability:F2}");

        AssertSignalsFlowed(response.ScenarioName, last);

        _output.WriteLine(
            $"{response.ScenarioName}: humans={humanCount}/{response.Results.Count} " +
            $"last band={last.Actual.RiskBand} signals={last.Actual.SignalCount}");
    }

    /// <summary>
    ///     The contract this rig actively defends: every detection must surface the signals
    ///     downstream display consumers read from <c>ev.Signals</c>. Asserting per-key (rather
    ///     than on a total signal count) keeps failures self-documenting — when this trips it
    ///     names the missing key and the consumer that breaks.
    /// </summary>
    private static void AssertSignalsFlowed(string scenarioName, BdfReplayResult last)
    {
        var probes = last.Actual!.SignalProbes;

        Assert.True(probes.TryGetValue(SignalKeys.PrimarySignature, out var hasSig) && hasSig,
            $"{scenarioName}: {SignalKeys.PrimarySignature} missing from ev.Signals — " +
            "RequestPersistenceService skips persistence, dashboard fingerprint table goes blank");

        Assert.True(probes.TryGetValue(SignalKeys.UserAgentFamily, out var hasUaFamily) && hasUaFamily,
            $"{scenarioName}: {SignalKeys.UserAgentFamily} missing from ev.Signals — " +
            "DeterministicBotNameSynthesizer falls back to 'analysing' placeholder");
    }

    private async Task<BdfReplayResponse?> ReplayAsync(string scenarioFile)
    {
        var bdfBody = await File.ReadAllBytesAsync(scenarioFile);

        using var client = new HttpClient { BaseAddress = new Uri(_demo.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        using var content = new ByteArrayContent(bdfBody);
        content.Headers.ContentType = new("application/json");

        using var resp = await client.PostAsync("/bot-detection/bdf-replay/replay", content);
        Assert.True(resp.IsSuccessStatusCode,
            $"Replay request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        return await resp.Content.ReadFromJsonAsync<BdfReplayResponse>(BdfReplayEndpoints.ReadOptions);
    }

    /// <summary>
    ///     Discovers BDF scenarios. Asserts the directory exists rather than yielding zero
    ///     theory cases — silent zero-coverage looks identical to "all green" in xUnit output.
    /// </summary>
    private static IEnumerable<object[]> DiscoverScenarios(string subdir)
    {
        var dir = Path.Combine(TestSuitesRoot, subdir);
        Assert.True(Directory.Exists(dir),
            $"BDF scenarios directory not found at {dir}. Expected to find test-suites/{subdir}/*.bdf.json " +
            "relative to repo root. Check that the test was launched from the repo workspace.");

        foreach (var file in Directory.EnumerateFiles(dir, "*.bdf.json").OrderBy(p => p))
            yield return new object[] { file };
    }
}