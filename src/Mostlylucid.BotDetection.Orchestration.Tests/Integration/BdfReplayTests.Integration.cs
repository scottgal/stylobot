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
[Trait("Category", "Integration")]
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
        AssertFingerprintStableWithinScenario(response.ScenarioName, response);

        _output.WriteLine(
            $"{response.ScenarioName}: bot={last.Actual.IsBot} prob={last.Actual.BotProbability:F2} " +
            $"band={last.Actual.RiskBand} name={last.Actual.BotName} signals={last.Actual.SignalCount} " +
            $"fp={last.Actual.IdentityFingerprintId?[..Math.Min(8, last.Actual.IdentityFingerprintId.Length)]} " +
            $"client={last.Actual.IdentityClientType}");
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
        AssertFingerprintStableWithinScenario(response.ScenarioName, response);

        _output.WriteLine(
            $"{response.ScenarioName}: humans={humanCount}/{response.Results.Count} " +
            $"last band={last.Actual.RiskBand} signals={last.Actual.SignalCount} " +
            $"fp={last.Actual.IdentityFingerprintId?[..Math.Min(8, last.Actual.IdentityFingerprintId.Length)]} " +
            $"client={last.Actual.IdentityClientType}");
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

    /// <summary>
    ///     The metastable-fingerprint contract this rig defends, in two parts:
    ///
    ///     1. Every request emits an identity.fingerprint_id. Holes mean the matcher
    ///        returned without emitting a fingerprint — a real silent-failure bug.
    ///
    ///     2. The matcher converges within the scenario. Bounded by ceil(N/2) distinct
    ///        fingerprints for N requests — first request always allocates; subsequent
    ///        requests should mostly match the same fp via L1 confirm or Pass 2. We
    ///        tolerate up to N/2 allocations because vector composition includes
    ///        session.* dims (path entropy, session age) that drift per request; the
    ///        matcher's LooseThreshold band catches most of this but occasional
    ///        allocation under high path variance is acceptable, not a regression.
    ///
    ///     Skipped silently when Identity is disabled (the response carries no fingerprint id).
    /// </summary>
    private static void AssertFingerprintStableWithinScenario(string scenarioName, BdfReplayResponse response)
    {
        var withFingerprints = response.Results
            .Where(r => r.Actual is { IdentityFingerprintId: not null })
            .ToList();
        if (withFingerprints.Count == 0) return; // Identity disabled in the host

        var withoutIds = response.Results.Count(r => r.Actual is { IdentityFingerprintId: null });
        Assert.True(withoutIds == 0,
            $"{scenarioName}: {withoutIds}/{response.Results.Count} requests had no identity.fingerprint_id. " +
            "FingerprintMatchContributor returned without emitting a fingerprint — check vector composition.");

        var distinctFps = withFingerprints
            .Select(r => r.Actual!.IdentityFingerprintId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var allowed = Math.Max(1, (int)Math.Ceiling(response.Results.Count / 2.0));
        Assert.True(distinctFps <= allowed,
            $"{scenarioName}: {distinctFps} distinct fingerprints across {response.Results.Count} requests " +
            $"(allowed {allowed}). The matcher isn't converging — every request is allocating new, suggesting " +
            "vector composition is unstable or LooseThreshold is unreachable.");
    }

    private async Task<BdfReplayResponse?> ReplayAsync(string scenarioFile)
    {
        var bdfBody = await File.ReadAllBytesAsync(scenarioFile);

        using var client = new HttpClient { BaseAddress = new Uri(_demo.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

        // Reset the identity store before each scenario. The Demo persists fingerprints
        // across requests by design; without a reset, scenario N inherits the fingerprints
        // scenarios 1..N-1 created and the per-scenario stability assertions become
        // ordering-dependent. Truncating gives every scenario a clean slate.
        using (var resetResp = await client.PostAsync("/bot-detection/bdf-replay/reset-identity", new StringContent("")))
        {
            Assert.True(resetResp.IsSuccessStatusCode,
                $"Identity reset failed: {(int)resetResp.StatusCode} {resetResp.ReasonPhrase}");
        }

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