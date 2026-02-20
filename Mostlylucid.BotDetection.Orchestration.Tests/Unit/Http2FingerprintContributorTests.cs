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
///     Unit tests for Http2FingerprintContributor.
///     Tests HTTP/2 SETTINGS fingerprinting, pseudoheader order, stream priority,
///     window updates, push support, and reverse proxy (Caddy) protocol forwarding.
/// </summary>
public class Http2FingerprintContributorTests
{
    private readonly Mock<ILogger<Http2FingerprintContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;

    public Http2FingerprintContributorTests()
    {
        _loggerMock = new Mock<ILogger<Http2FingerprintContributor>>();
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

    private Http2FingerprintContributor CreateContributor()
    {
        return new Http2FingerprintContributor(_loggerMock.Object, _configProviderMock.Object);
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
    public void Name_ReturnsHttp2Fingerprint()
    {
        var contributor = CreateContributor();
        Assert.Equal("Http2Fingerprint", contributor.Name);
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

    [Theory]
    [InlineData("HTTP/2")]
    [InlineData("HTTP/2.0")]
    public async Task ContributeAsync_Http2Protocol_DetectsAsHttp2(string protocol)
    {
        var contributor = CreateContributor();
        var state = CreateState(protocol);

        var contributions = await contributor.ContributeAsync(state);

        Assert.True((bool)state.Signals["h2.is_http2"]);
        Assert.Equal(protocol, state.Signals[SignalKeys.H2Protocol]);
    }

    [Fact]
    public async Task ContributeAsync_Http1Protocol_AppliesPenalty()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/1.1");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.True(contributions[0].ConfidenceDelta > 0, "HTTP/1.1 should produce bot signal");
        Assert.Contains("HTTP/1.1", contributions[0].Reason);
    }

    [Fact]
    public async Task ContributeAsync_Http3Protocol_SkipsWithInfo()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/3");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("HTTP/3", contributions[0].Reason);
        Assert.True((bool)state.Signals["h2.is_http3"]);
    }

    // ==========================================
    // Reverse Proxy (Caddy) Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_BehindProxy_Http2ViaHeader_DetectsAsHttp2()
    {
        // Behind Caddy: Request.Protocol is HTTP/1.1, but X-HTTP-Protocol says HTTP/2
        var contributor = CreateContributor();
        var state = CreateState("HTTP/1.1", new Dictionary<string, string>
        {
            ["X-HTTP-Protocol"] = "HTTP/2"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True((bool)state.Signals["h2.is_http2"]);
        Assert.True((bool)state.Signals["h2.behind_proxy"]);
        Assert.Equal("HTTP/2", state.Signals[SignalKeys.H2Protocol]);
        // Should NOT have the HTTP/1 penalty
        Assert.DoesNotContain(contributions, c =>
            c.Reason?.Contains("instead of HTTP/2") == true);
    }

    [Fact]
    public async Task ContributeAsync_BehindProxy_Http1ViaHeader_StillAppliesPenalty()
    {
        // Client genuinely connected with HTTP/1.1 through proxy
        var contributor = CreateContributor();
        var state = CreateState("HTTP/1.1", new Dictionary<string, string>
        {
            ["X-HTTP-Protocol"] = "HTTP/1.1"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals["h2.is_http2"]);
        Assert.True((bool)state.Signals["h2.behind_proxy"]);
        Assert.Single(contributions);
        Assert.True(contributions[0].ConfidenceDelta > 0, "Genuine HTTP/1.1 client should still get penalty");
    }

    [Fact]
    public async Task ContributeAsync_BehindProxy_Http3ViaHeader_SkipsWithInfo()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/1.1", new Dictionary<string, string>
        {
            ["X-HTTP-Protocol"] = "HTTP/3"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Contains("HTTP/3", contributions[0].Reason);
        Assert.True((bool)state.Signals["h2.is_http3"]);
    }

    [Fact]
    public async Task ContributeAsync_NoProxyHeader_UsesRequestProtocol()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2");

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals["h2.behind_proxy"]);
        Assert.True((bool)state.Signals["h2.is_http2"]);
    }

    // ==========================================
    // HTTP/2 Settings Fingerprint Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_KnownBrowserFingerprint_ProducesHumanSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Settings"] = "1:65536,2:0,3:100,4:131072,5:16384"
        });

        var contributions = await contributor.ContributeAsync(state);

        var browserContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta < 0);
        Assert.NotNull(browserContrib);
        Assert.Contains("Firefox_Desktop", browserContrib!.Reason);
        Assert.Equal("Firefox_Desktop", state.Signals[SignalKeys.H2ClientType]);
    }

    [Fact]
    public async Task ContributeAsync_KnownBotFingerprint_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Settings"] = "3:100,4:65536"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0.5);
        Assert.NotNull(botContrib);
        Assert.Contains("Go_HTTP2_Client", botContrib!.Reason);
    }

    [Fact]
    public async Task ContributeAsync_UnknownFingerprint_SetsUnknownFlag()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Settings"] = "99:12345,100:67890"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey("h2.fingerprint_unknown"));
    }

    // ==========================================
    // Pseudoheader Order Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_StandardPseudoheaderOrder_NoPenalty()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Pseudoheader-Order"] = "method,path,authority,scheme"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason?.Contains("pseudoheader order") == true);
        Assert.Equal("method,path,authority,scheme", state.Signals["h2.pseudoheader_order"]);
    }

    [Fact]
    public async Task ContributeAsync_NonStandardPseudoheaderOrder_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Pseudoheader-Order"] = "path,method,scheme,authority"
        });

        var contributions = await contributor.ContributeAsync(state);

        var pseudoContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("pseudoheader order") == true);
        Assert.NotNull(pseudoContrib);
        Assert.True(pseudoContrib!.ConfidenceDelta > 0);
    }

    // ==========================================
    // Stream Priority Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_HasStreamPriority_NoPenalty()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Stream-Priority"] = "256"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.True((bool)state.Signals["h2.uses_priority"]);
        Assert.DoesNotContain(contributions, c =>
            c.Reason?.Contains("stream priority") == true);
    }

    [Fact]
    public async Task ContributeAsync_NoPriority_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2");

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals["h2.uses_priority"]);
        var prioContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("priority") == true);
        Assert.NotNull(prioContrib);
        Assert.True(prioContrib!.ConfidenceDelta > 0);
    }

    // ==========================================
    // Window Update Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_ZeroWindowUpdates_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Window-Updates"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        var windowContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("WINDOW_UPDATE") == true);
        Assert.NotNull(windowContrib);
        Assert.True(windowContrib!.ConfidenceDelta > 0);
    }

    // ==========================================
    // Push Support Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_PushDisabled_ProducesBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Push-Enabled"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals["h2.push_enabled"]);
        var pushContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("Push disabled") == true);
        Assert.NotNull(pushContrib);
        Assert.True(pushContrib!.ConfidenceDelta > 0);
    }

    // ==========================================
    // Invalid Preface Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_InvalidPreface_ProducesStrongBotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Preface-Valid"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals["h2.preface_valid"]);
        var prefaceContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("preface") == true);
        Assert.NotNull(prefaceContrib);
        Assert.True(prefaceContrib!.ConfidenceDelta >= 0.8, "Invalid preface should be a strong bot signal");
    }

    // ==========================================
    // Combined Signal Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_AllBotSignals_ProducesMultipleBotContributions()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Settings"] = "3:100,4:65536", // Go bot
            ["X-HTTP2-Pseudoheader-Order"] = "path,method,authority,scheme", // non-standard
            ["X-HTTP2-Push-Enabled"] = "0",
            ["X-HTTP2-Preface-Valid"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContribs = contributions.Count(c => c.ConfidenceDelta > 0);
        Assert.True(botContribs >= 3, $"Expected at least 3 bot signals, got {botContribs}");
    }

    [Fact]
    public async Task ContributeAsync_BrowserFingerprint_WithPriority_ProducesHumanSignals()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2", new Dictionary<string, string>
        {
            ["X-HTTP2-Settings"] = "1:65536,2:0,3:100,4:131072,5:16384", // Firefox
            ["X-HTTP2-Pseudoheader-Order"] = "method,path,authority,scheme", // standard
            ["X-HTTP2-Stream-Priority"] = "256",
            ["X-HTTP2-Push-Enabled"] = "1"
        });

        var contributions = await contributor.ContributeAsync(state);

        var humanContribs = contributions.Count(c => c.ConfidenceDelta < 0);
        Assert.True(humanContribs >= 1, "Should have at least 1 human signal from browser fingerprint");
        var botContribs = contributions.Count(c => c.ConfidenceDelta > 0);
        Assert.Equal(0, botContribs);
    }

    // ==========================================
    // No Anomalies Test
    // ==========================================

    [Fact]
    public async Task ContributeAsync_Http2NoHeaders_ReturnsNoPriorityPenaltyOnly()
    {
        var contributor = CreateContributor();
        var state = CreateState("HTTP/2");

        var contributions = await contributor.ContributeAsync(state);

        // With HTTP/2 but no X-HTTP2-* headers, should get the no-priority penalty
        // and possibly the fallback pseudoheader order check
        Assert.True(contributions.Count >= 1);
    }
}
