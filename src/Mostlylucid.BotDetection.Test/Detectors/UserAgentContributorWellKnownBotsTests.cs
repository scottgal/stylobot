using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Verifies that <see cref="UserAgentContributor"/> correctly uses
///     the <see cref="WellKnownBotIndex"/> as a fallback naming source
///     when the YAML-defined patterns do not produce a match.
///
///     Key invariants:
///     <list type="bullet">
///       <item>YAML patterns take priority over the well-known index.</item>
///       <item>When only the index matches, correct signals are written.</item>
///       <item>When the index is empty (not yet loaded), the contributor still
///           functions as before (no regression).</item>
///     </list>
/// </summary>
public sealed class UserAgentContributorWellKnownBotsTests
{
    private static WellKnownBotEntry MakeEntry(
        string id,
        string[] categories,
        string[] accepted) => new()
    {
        Id = id,
        Categories = categories,
        Pattern = new WellKnownBotPattern { Accepted = accepted, Forbidden = [] }
    };

    private static UserAgentContributor BuildContributor(
        WellKnownBotIndex? index = null)
    {
        return new UserAgentContributor(
            NullLogger<UserAgentContributor>.Instance,
            Options.Create(new BotDetectionOptions()),
            new NullDetectorConfigProvider(),
            wellKnownBots: index);
    }

    private static BlackboardState StateWithUa(string ua)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = ua;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        var signals = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    // ── No regression when index is absent ──────────────────────────────────

    [Fact]
    public async Task Contributes_without_well_known_index_injected()
    {
        var sut = BuildContributor(index: null);
        var contributions = await sut.ContributeAsync(StateWithUa("Googlebot/2.1 (+http://www.google.com/bot.html)"));
        contributions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Contributes_with_empty_index()
    {
        var sut = BuildContributor(index: new WellKnownBotIndex());
        var contributions = await sut.ContributeAsync(StateWithUa("Googlebot/2.1 (+http://www.google.com/bot.html)"));
        contributions.Should().NotBeEmpty();
    }

    // ── Fallback to well-known index ─────────────────────────────────────────

    [Fact]
    public async Task Uses_well_known_index_for_ua_not_in_yaml_patterns()
    {
        // "FictionalSpecialCrawler" is not in any YAML file, but is in the index
        var index = new WellKnownBotIndex();
        index.Replace([MakeEntry("fictional-special-crawler", ["tool"], ["FictionalSpecialCrawler"])]);

        var sut = BuildContributor(index);
        var contributions = await sut.ContributeAsync(StateWithUa("FictionalSpecialCrawler/1.0"));

        var first = contributions.First();
        first.ConfidenceDelta.Should().BeGreaterThan(0, "well-known bot should register as bot");
    }

    [Fact]
    public async Task Writes_correct_bot_name_signal_from_index_display_name()
    {
        var index = new WellKnownBotIndex();
        index.Replace([MakeEntry("test-crawler", ["tool"], ["TestCrawler"])]);

        var sut = BuildContributor(index);
        var state = StateWithUa("TestCrawler/2.0 (crawl)");
        await sut.ContributeAsync(state);

        // The signal written by the last contribution carries signals
        state.Signals.Should().ContainKey(SignalKeys.UserAgentBotName);
        state.Signals[SignalKeys.UserAgentBotName].Should().Be("Test Crawler");
    }

    [Fact]
    public async Task Writes_correct_bot_type_signal_from_index_category()
    {
        var index = new WellKnownBotIndex();
        index.Replace([MakeEntry("ai-crawler", ["ai"], ["AiTestCrawler"])]);

        var sut = BuildContributor(index);
        var state = StateWithUa("AiTestCrawler/1.0");
        await sut.ContributeAsync(state);

        state.Signals.Should().ContainKey(SignalKeys.UserAgentBotType);
        state.Signals[SignalKeys.UserAgentBotType].Should().Be(BotType.AiBot.ToString());
    }

    // ── YAML takes priority ──────────────────────────────────────────────────

    [Fact]
    public async Task Yaml_pattern_takes_priority_over_index()
    {
        // Googlebot IS in the YAML files; provide a conflicting index entry with
        // a different display name to confirm YAML wins.
        var index = new WellKnownBotIndex();
        index.Replace([MakeEntry("fake-googlebot", ["unknown"], ["Googlebot"])]);

        var sut = BuildContributor(index);
        var state = StateWithUa("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");
        await sut.ContributeAsync(state);

        // The YAML-derived name ("Googlebot") should appear, not the index name "Fake Googlebot"
        if (state.Signals.TryGetValue(SignalKeys.UserAgentBotName, out var name))
            name.ToString().Should().NotBe("Fake Googlebot");
    }

    // ── Signal completeness ──────────────────────────────────────────────────

    [Fact]
    public async Task All_expected_signals_are_present_for_index_match()
    {
        var index = new WellKnownBotIndex();
        index.Replace([MakeEntry("monitor-bot", ["monitor"], ["MonitorTestBot"])]);

        var sut = BuildContributor(index);
        var state = StateWithUa("MonitorTestBot/3.0");
        await sut.ContributeAsync(state);

        state.Signals.Should().ContainKey(SignalKeys.UserAgentBotName);
        state.Signals.Should().ContainKey(SignalKeys.UserAgentBotType);
    }
}

/// <summary>
///     Minimal no-op config provider so <see cref="UserAgentContributor"/>
///     can be instantiated in unit tests without a full DI container.
/// </summary>
file sealed class NullDetectorConfigProvider : IDetectorConfigProvider
{
    public DetectorManifest? GetManifest(string detectorName) => null;

    public DetectorDefaults GetDefaults(string detectorName) => new()
    {
        Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
        Confidence = new ConfidenceDefaults { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
        Parameters = new Dictionary<string, object>()
    };

    public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;

    public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
        ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
        => Task.FromResult(defaultValue);

    public void InvalidateCache(string? detectorName = null) { }

    public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
        => new Dictionary<string, DetectorManifest>();
}