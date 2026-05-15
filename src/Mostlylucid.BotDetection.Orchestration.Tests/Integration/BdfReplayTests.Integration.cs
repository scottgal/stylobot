using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.Endpoints;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Plays the canonical BDF scenarios in <c>test-suites/</c> against a running Demo instance
///     via the <c>/bot-detection/bdf-replay/replay</c> endpoint, then asserts on the rich response
///     surface (bot name, risk band, signal-presence probes).
///
///     Why this exists: the EphemeralDetectionOrchestrator regression (premergedSignals dropped
///     when the active orchestrator was swapped) only manifested in display-side fields fed by
///     <c>AggregatedEvidence.Signals</c>. Existing unit tests asserted on bot probability and
///     pass. UI scrape tests need a running app. This rig sits in between: real orchestrator,
///     real signal pipeline, asserts on what the dashboard would render.
///
///     Once the BDF endpoint started routing through <see cref="IDetectionOrchestrator"/> instead
///     of the concrete BlackboardOrchestrator, this rig actively defends the active path.
/// </summary>
[Collection("DemoApp")]
public sealed class BdfReplayTests
{
    private static readonly string TestSuitesRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-suites"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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

        // Aggregate across the scenario: the LAST request should reflect the matured verdict.
        var last = response.Results[^1];
        Assert.NotNull(last.Actual);

        // Detection sanity: the scenario was hand-built to be bot-shaped.
        Assert.True(last.Actual!.IsBot,
            $"{response.ScenarioName}: last request scored {last.Actual.BotProbability:F2}, expected bot");

        // The display pipeline must have what it needs. These signals are the concrete failure
        // surfaces of the premergedSignals regression — checking each makes the failure mode
        // unambiguous when something breaks in the future.
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

        // Human scenarios shouldn't all flip to bot. We assert "majority human" rather than
        // "every request human" because some heuristics legitimately escalate on outlier rates.
        var humanCount = response.Results.Count(r => r.Actual is { IsBot: false });
        var botCount = response.Results.Count - humanCount;
        Assert.True(humanCount >= botCount,
            $"{response.ScenarioName}: {botCount}/{response.Results.Count} requests classified as bot, " +
            $"expected majority human. Last verdict: {last.Actual!.RiskBand} prob={last.Actual.BotProbability:F2}");

        // The signal pipeline must still be intact even on human traffic — the synthesizer
        // and dashboard depend on UA family / signature.primary regardless of verdict.
        AssertSignalsFlowed(response.ScenarioName, last);

        _output.WriteLine(
            $"{response.ScenarioName}: humans={humanCount}/{response.Results.Count} " +
            $"last band={last.Actual.RiskBand} signals={last.Actual.SignalCount}");
    }

    /// <summary>
    ///     The contract this rig actively defends: every detection must surface the signals that
    ///     downstream display consumers (DeterministicBotNameSynthesizer, RequestPersistenceService,
    ///     CLI Top Fingerprints, fingerprint-prior delta) read from <c>ev.Signals</c>.
    ///
    ///     If <c>signature.primary</c> is missing the dashboard's fingerprint table goes blank
    ///     and SQLite persists nothing. If <c>ua.bot_name</c> / <c>ua.family</c> are missing
    ///     the deterministic name synthesizer falls through to "analysing" placeholder text.
    /// </summary>
    private static void AssertSignalsFlowed(string scenarioName, BdfReplayResult last)
    {
        var probes = last.Actual!.SignalProbes;

        Assert.True(probes.TryGetValue("signature.primary", out var hasSig) && hasSig,
            $"{scenarioName}: signature.primary missing from ev.Signals — " +
            "RequestPersistenceService will skip persistence, dashboard fingerprint table goes blank");

        Assert.True(probes.TryGetValue("ua.family", out var hasUaFamily) && hasUaFamily,
            $"{scenarioName}: ua.family missing from ev.Signals — " +
            "DeterministicBotNameSynthesizer will fall back to 'analysing' placeholder");

        // signal count > 4 means we got the per-state SignalWriter contents, not just the
        // 1-2 signals that contribution.Signals carries on its own. Empirically a healthy
        // detection writes 30-60 signals; anything under 10 means premergedSignals broke.
        Assert.True(last.Actual.SignalCount > 10,
            $"{scenarioName}: only {last.Actual.SignalCount} signals reached ev.Signals — " +
            "EphemeralDetectionOrchestrator is not propagating BlackboardState.SignalWriter (premergedSignals dropped?)");
    }

    private async Task<BdfReplayResponse?> ReplayAsync(string scenarioFile)
    {
        if (!File.Exists(scenarioFile))
            throw new FileNotFoundException($"BDF scenario not found: {scenarioFile}", scenarioFile);

        var bdfBody = await File.ReadAllBytesAsync(scenarioFile);

        using var client = new HttpClient { BaseAddress = new Uri(_demo.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        using var content = new ByteArrayContent(bdfBody);
        content.Headers.ContentType = new("application/json");

        using var resp = await client.PostAsync("/bot-detection/bdf-replay/replay", content);
        Assert.True(resp.IsSuccessStatusCode,
            $"Replay request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        return await resp.Content.ReadFromJsonAsync<BdfReplayResponse>(JsonOptions);
    }

    private static IEnumerable<object[]> DiscoverScenarios(string subdir)
    {
        var dir = Path.Combine(TestSuitesRoot, subdir);
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.bdf.json").OrderBy(p => p))
            yield return new object[] { file };
    }
}
