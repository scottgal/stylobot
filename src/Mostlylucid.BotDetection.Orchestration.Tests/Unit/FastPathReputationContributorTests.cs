using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Pins the Tool-family carve-out on the FastPath reputation early-exit branch
///     (see <see cref="FastPathReputationContributor"/>). Background:
///     <c>curl https://staging.stylobot.net/...</c> was returning
///     <c>X-Bot-Verdict: VerifiedBadBot</c> with risk 1.0 because the IP-pattern
///     reputation cache had latched <c>ConfirmedBad</c> after an earlier session's
///     curl probes hammered the same IP. The fast-path early-exit composed a
///     <c>VerifiedBot</c> contribution from raw repeat-count alone, with no
///     awareness that the contemporaneous UA was a developer tool. User principle:
///     <i>"No exemption rule; the detection is faulty. It should know a local ip
///     can use curl."</i>
///
///     These fixtures pin the four-arm fix:
///     - Fixture A: Tool-family UA + ConfirmedBad IP rep + no hostile signals => demote.
///     - Fixture C: SecurityToolContributor-class UAs (sqlmap etc.) still flag hostile.
///     - Fixture D: PatternReputationUpdater refuses to promote Tool-tagged centroids
///       past Suspect (the catalog ceiling).
/// </summary>
public sealed class FastPathReputationContributorTests
{
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;
    private readonly IPatternReputationCache _cache;

    public FastPathReputationContributorTests()
    {
        _configProviderMock = new Mock<IDetectorConfigProvider>();
        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);

        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance, options);
        _cache = new InMemoryPatternReputationCache(
            NullLogger<InMemoryPatternReputationCache>.Instance, updater);
    }

    private FastPathReputationContributor CreateContributor()
        => new(
            NullLogger<FastPathReputationContributor>.Instance,
            _cache,
            _configProviderMock.Object);

    private static BlackboardState CreateState(
        string userAgent,
        string clientIp,
        Dictionary<string, object>? extraSignals = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.UserAgent = userAgent;
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);

        var signals = new ConcurrentDictionary<string, object>();
        signals[SignalKeys.ClientIp] = clientIp;
        if (extraSignals is not null)
            foreach (var kv in extraSignals)
                signals[kv.Key] = kv.Value;

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    /// <summary>
    ///     Seed the in-memory cache with a ConfirmedBad IP-pattern equivalent to
    ///     the staging incident (support=116, score=1.0).
    /// </summary>
    private void SeedConfirmedBadIp(string clientIp)
    {
        var patternId = PatternNormalization.CreateIpPatternId(clientIp);
        var rep = new PatternReputation
        {
            PatternId = patternId,
            PatternType = "IP",
            Pattern = PatternNormalization.NormalizeIpToRange(clientIp),
            BotScore = 1.0,
            Support = 116,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5),
            StateChangedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        _cache.Update(rep);
    }

    // ============================================================
    // Fixture A — curl from PUBLIC IP, no hostile signals, IP rep
    // is ConfirmedBad. Demote: must NOT early-exit as VerifiedBadBot.
    // ============================================================

    [Fact]
    public async Task FixtureA_ToolUa_ConfirmedBadIpRep_NoHostileSignals_DoesNotEarlyExitAsVerifiedBadBot()
    {
        var clientIp = "1.2.3.4";
        SeedConfirmedBadIp(clientIp);

        var state = CreateState(
            userAgent: "curl/8.5.0",
            clientIp: clientIp,
            extraSignals: new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotName] = "curl",
                [SignalKeys.UserAgentBotType] = nameof(BotType.Tool),
                [SignalKeys.UserAgentIsBot] = true
            });

        var contributions = await CreateContributor().ContributeAsync(state);

        Assert.Single(contributions);
        var c = contributions[0];

        // The fix demotes the early-exit VerifiedBot contribution to a mild bias
        // when the contemporaneous UA catalog says Tool with no hostile signals.
        Assert.False(c.TriggerEarlyExit,
            $"Tool-family UA on ConfirmedBad IP must not trigger early-exit (got TriggerEarlyExit=true, verdict={c.EarlyExitVerdict})");
        Assert.NotEqual("VerifiedBadBot", c.EarlyExitVerdict);
    }

    // ============================================================
    // Fixture B (placed in Test/Risk per task spec — see
    // LanCurlClampTests.cs) — curl from RFC1918 currently short-
    // circuits BEFORE reaching FastPathReputation early-exit because
    // FastPathReputationContributor skips IP lookups for local IPs.
    // We still pin that behaviour here as a regression: a LAN curl
    // never receives a VerifiedBadBot from this contributor.
    // ============================================================

    [Fact]
    public async Task FixtureB_LanCurl_NeverEarlyExitsAsVerifiedBadBot()
    {
        // Even if a stale IP-rep latch ever existed for this LAN range,
        // the contributor's local-IP guard plus the new Tool-family demote
        // must keep the fast-abort early-exit off.
        var clientIp = "192.168.0.5";
        SeedConfirmedBadIp(clientIp);

        var state = CreateState(
            userAgent: "curl/8.5.0",
            clientIp: clientIp,
            extraSignals: new Dictionary<string, object>
            {
                [SignalKeys.IpIsLocal] = true,
                [SignalKeys.UserAgentBotName] = "curl",
                [SignalKeys.UserAgentBotType] = nameof(BotType.Tool),
                [SignalKeys.UserAgentIsBot] = true
            });

        var contributions = await CreateContributor().ContributeAsync(state);

        Assert.Single(contributions);
        var c = contributions[0];
        Assert.False(c.TriggerEarlyExit,
            $"LAN curl must never early-exit (got TriggerEarlyExit=true, verdict={c.EarlyExitVerdict})");
        Assert.NotEqual("VerifiedBadBot", c.EarlyExitVerdict);
    }

    // ============================================================
    // Fixture C — sqlmap from the same hostile public IP.
    // Regression guard: the FastPath early-exit DOES still fire here,
    // because the UA catalogue is NOT Tool-family (sqlmap is the
    // SecurityToolContributor's domain). FastPath contribution alone
    // is not the basis for the verdict on sqlmap — SecurityTool
    // contributes its own VerifiedBadBot. What this fixture pins is
    // that the FastPath Tool-family demote does NOT accidentally
    // weaken the IP-reputation early-exit for non-Tool UAs.
    // ============================================================

    [Fact]
    public async Task FixtureC_NonToolHostileUa_ConfirmedBadIp_StillEarlyExitsAsVerifiedBadBot()
    {
        var clientIp = "1.2.3.4";
        SeedConfirmedBadIp(clientIp);

        // No Tool-family signals here: sqlmap is classified as MaliciousBot
        // by the SecurityToolContributor, not as Tool. The FastPath demote
        // must NOT apply.
        var state = CreateState(
            userAgent: "sqlmap/1.7.2 (http://sqlmap.org)",
            clientIp: clientIp,
            extraSignals: new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotName] = "sqlmap",
                [SignalKeys.UserAgentBotType] = nameof(BotType.MaliciousBot),
                [SignalKeys.UserAgentIsBot] = true
            });

        var contributions = await CreateContributor().ContributeAsync(state);

        Assert.Single(contributions);
        var c = contributions[0];
        Assert.True(c.TriggerEarlyExit,
            "Non-Tool hostile UAs with ConfirmedBad IP must still early-exit as VerifiedBadBot");
        Assert.Equal("VerifiedBadBot", c.EarlyExitVerdict);
    }

    // ============================================================
    // Fixture D — PatternReputationUpdater promotion gate.
    // 200 successive ApplyEvidence calls tagged as Tool must NOT
    // promote the pattern past Suspect. Mirror test with no Tool
    // tag must promote to ConfirmedBad.
    // ============================================================

    [Fact]
    public void FixtureD_ToolTaggedEvidence_NeverPromotesPastSuspect()
    {
        var options = new BotDetectionOptions();
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance, Options.Create(options));

        PatternReputation? rep = null;
        for (var i = 0; i < 200; i++)
        {
            rep = updater.ApplyEvidence(
                rep,
                patternId: "ip:1.2.3.0/24",
                patternType: "IP",
                pattern: "1.2.3.0/24",
                label: 1.0,
                evidenceWeight: 1.0,
                botType: nameof(BotType.Tool));
        }

        Assert.NotNull(rep);
        Assert.NotEqual(ReputationState.ConfirmedBad, rep!.State);
        Assert.True(rep.State is ReputationState.Neutral or ReputationState.Suspect,
            $"Tool-tagged evidence must cap at Suspect; got {rep.State} (score={rep.BotScore:F2}, support={rep.Support:F0})");
    }

    [Fact]
    public void FixtureD_Mirror_NonToolEvidence_PromotesToConfirmedBad()
    {
        var options = new BotDetectionOptions();
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance, Options.Create(options));

        PatternReputation? rep = null;
        for (var i = 0; i < 200; i++)
        {
            rep = updater.ApplyEvidence(
                rep,
                patternId: "ip:1.2.3.0/24",
                patternType: "IP",
                pattern: "1.2.3.0/24",
                label: 1.0,
                evidenceWeight: 1.0,
                botType: null);
        }

        Assert.NotNull(rep);
        Assert.Equal(ReputationState.ConfirmedBad, rep!.State);
    }
}
