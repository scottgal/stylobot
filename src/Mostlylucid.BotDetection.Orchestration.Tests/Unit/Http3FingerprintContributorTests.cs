using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for Http3FingerprintContributor.
///     Tests QUIC transport parameter fingerprinting, version analysis, 0-RTT, connection migration, and more.
/// </summary>
public class Http3FingerprintContributorTests
{
    private readonly Mock<ILogger<Http3FingerprintContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;

    public Http3FingerprintContributorTests()
    {
        _loggerMock = new Mock<ILogger<Http3FingerprintContributor>>();
        _configProviderMock = new Mock<IDetectorConfigProvider>();

        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
    }

    private Http3FingerprintContributor CreateContributor()
    {
        return new Http3FingerprintContributor(_loggerMock.Object, _configProviderMock.Object);
    }

    private BlackboardState CreateState(string protocol, Dictionary<string, string>? headers = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Protocol = protocol;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
                httpContext.Request.Headers[key] = value;
        }

        var signalDict = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    // ==========================================
    // Properties Tests
    // ==========================================

    [Fact]
    public void Name_ReturnsHttp3Fingerprint()
    {
        var contributor = CreateContributor();
        Assert.Equal("Http3Fingerprint", contributor.Name);
    }

    [Fact]
    public void Priority_Is14()
    {
        var contributor = CreateContributor();
        Assert.Equal(14, contributor.Priority);
    }

    [Fact]
    public void TriggerConditions_IsEmpty_RunsInFirstWave()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }

    // ==========================================
    // Protocol Detection Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_NotHttp3_ReturnsInfoContribution()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("not HTTP/3", contributions[0].Reason);
        Assert.True(state.Signals.ContainsKey(SignalKeys.H3Protocol));
    }

    [Theory]
    [InlineData("HTTP/3")]
    [InlineData("HTTP/3.0")]
    public async Task ContributeAsync_Http3Protocol_DetectsAsHttp3(string protocol)
    {
        var contributor = CreateContributor();
        var state = CreateState(protocol);

        var contributions = await contributor.ContributeAsync(state);

        // Should have at least the HTTP/3 human signal (using QUIC is positive)
        Assert.True(contributions.Count >= 1);
        Assert.True(contributions[0].ConfidenceDelta < 0,
            "HTTP/3 usage should produce a human (negative) signal");
    }

    [Fact]
    public async Task ContributeAsync_Http1_ReturnsInfoContribution()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/1.1");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Contains("not HTTP/3", contributions[0].Reason);
    }

    // ==========================================
    // QUIC Transport Parameter Tests
    // ==========================================

    [Theory]
    [InlineData("initial_max_data=15728640", "Chrome_QUIC")]
    [InlineData("initial_max_data=10485760", "Firefox_QUIC")]
    [InlineData("initial_max_data=8388608", "Safari_QUIC")]
    public async Task ContributeAsync_BrowserTransportParams_IdentifiesBrowser(string transportParams,
        string expectedClient)
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = transportParams
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 2); // HTTP/3 human + browser fingerprint
        Assert.True(state.Signals.ContainsKey(SignalKeys.H3ClientType));
        Assert.Equal(expectedClient, state.Signals[SignalKeys.H3ClientType]);
    }

    [Theory]
    [InlineData("initial_max_data=1048576", "Go_QuicGo")]
    [InlineData("initial_max_data=2097152", "Python_Aioquic")]
    [InlineData("initial_max_data=10000000", "Curl_Quiche")]
    public async Task ContributeAsync_BotTransportParams_IdentifiesBot(string transportParams,
        string expectedClient)
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = transportParams
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0.5);
        Assert.NotNull(botContrib);
        Assert.Contains(expectedClient, botContrib!.Reason);
    }

    [Fact]
    public async Task ContributeAsync_UnknownTransportParams_SetsUnknownFlag()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = "initial_max_data=999999"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey("h3.transport_fingerprint_unknown"));
    }

    // ==========================================
    // QUIC Version Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_DraftVersion_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Version"] = "draft-29"
        });

        var contributions = await contributor.ContributeAsync(state);

        var draftContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("draft", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(draftContrib);
        Assert.True(draftContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_QuicV2_ProducesHumanSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Version"] = "v2"
        });

        var contributions = await contributor.ContributeAsync(state);

        var v2Contrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("QUIC v2", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(v2Contrib);
        Assert.True(v2Contrib!.ConfidenceDelta < 0);
    }

    // ==========================================
    // 0-RTT Resumption Tests
    // ==========================================

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public async Task ContributeAsync_ZeroRttUsed_ProducesHumanSignal(string value)
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-0RTT"] = value
        });

        var contributions = await contributor.ContributeAsync(state);

        var zeroRttContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("0-RTT", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(zeroRttContrib);
        Assert.True(zeroRttContrib!.ConfidenceDelta < 0, "0-RTT should produce human signal");

        Assert.True(state.Signals.ContainsKey(SignalKeys.H3ZeroRtt));
        Assert.True((bool)state.Signals[SignalKeys.H3ZeroRtt]);
    }

    // ==========================================
    // Connection Migration Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_ConnectionMigrated_ProducesHumanSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Connection-Migrated"] = "true"
        });

        var contributions = await contributor.ContributeAsync(state);

        var migrationContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("migration", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(migrationContrib);
        Assert.True(migrationContrib!.ConfidenceDelta < 0, "Connection migration should produce human signal");

        Assert.True(state.Signals.ContainsKey(SignalKeys.H3ConnectionMigrated));
    }

    // ==========================================
    // Spin Bit Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_SpinBitDisabled_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Spin-Bit"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        var spinContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("spin bit", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(spinContrib);
        Assert.True(spinContrib!.ConfidenceDelta > 0, "Disabled spin bit should produce bot signal");
    }

    // ==========================================
    // Alt-Svc Upgrade Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_AltSvcUpgrade_ProducesStrongHumanSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Alt-Svc-Used"] = "1"
        });

        var contributions = await contributor.ContributeAsync(state);

        var altSvcContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("Alt-Svc", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(altSvcContrib);
        Assert.True(altSvcContrib!.ConfidenceDelta < 0, "Alt-Svc upgrade should produce human signal");
    }

    // ==========================================
    // Combined Signal Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_AllBrowserSignals_ProducesMultipleHumanContributions()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = "initial_max_data=15728640",
            ["X-QUIC-Version"] = "v2",
            ["X-QUIC-0RTT"] = "1",
            ["X-QUIC-Connection-Migrated"] = "true",
            ["X-QUIC-Alt-Svc-Used"] = "1"
        });

        var contributions = await contributor.ContributeAsync(state);

        // Should have many contributions (HTTP/3 human + browser + v2 + 0-RTT + migration + Alt-Svc)
        Assert.True(contributions.Count >= 5);
        // Most should be human signals (negative confidence)
        var humanContribs = contributions.Count(c => c.ConfidenceDelta < 0);
        Assert.True(humanContribs >= 4);
    }

    [Fact]
    public async Task ContributeAsync_AllBotSignals_ProducesBotContributions()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = "initial_max_data=1048576",
            ["X-QUIC-Version"] = "draft-29",
            ["X-QUIC-Spin-Bit"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContribs = contributions.Count(c => c.ConfidenceDelta > 0);
        Assert.True(botContribs >= 2, "Should have bot signals from transport params + draft version");
    }

    // ==========================================
    // Signal Emission Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_EmitsProtocolSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3");

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.H3Protocol));
        Assert.Equal("HTTP/3", state.Signals[SignalKeys.H3Protocol]);
        Assert.True(state.Signals.ContainsKey("h3.is_http3"));
        Assert.True((bool)state.Signals["h3.is_http3"]);
    }

    [Fact]
    public async Task ContributeAsync_LastContributionHasAllSignals()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3", new Dictionary<string, string>
        {
            ["X-QUIC-Transport-Params"] = "initial_max_data=15728640",
            ["X-QUIC-0RTT"] = "1"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.H3Protocol));
        Assert.True(state.Signals.ContainsKey("h3.is_http3"));
        Assert.True(state.Signals.ContainsKey("h3.transport_params"));
        Assert.True(state.Signals.ContainsKey(SignalKeys.H3ZeroRtt));
    }
}
