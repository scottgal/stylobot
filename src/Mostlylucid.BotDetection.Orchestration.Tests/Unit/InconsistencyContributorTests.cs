using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class InconsistencyContributorTests
{
    private const string AndroidChromeUa =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Mobile Safari/537.36";

    private readonly Mock<ILogger<InconsistencyContributor>> _loggerMock = new();
    private readonly Mock<IDetectorConfigProvider> _configProviderMock = new();

    public InconsistencyContributorTests()
    {
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
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string _, string def) => def);
    }

    private InconsistencyContributor CreateContributor() =>
        new(_loggerMock.Object, _configProviderMock.Object);

    private static BlackboardState CreateState(
        string userAgent,
        string? secChUaMobile = null,
        string? connectionType = null,
        bool? iceNoSrflx = null,
        int? ttsVoiceCount = null,
        bool addLanguageHeader = true,
        bool addClientHints = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.UserAgent = userAgent;
        if (addLanguageHeader) httpContext.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        if (addClientHints) httpContext.Request.Headers["sec-ch-ua"] = "\"Chromium\";v=\"145\", \"Google Chrome\";v=\"145\"";
        if (secChUaMobile != null) httpContext.Request.Headers["Sec-CH-UA-Mobile"] = secChUaMobile;

        var signalDict = new ConcurrentDictionary<string, object>();
        signalDict[SignalKeys.UserAgent] = userAgent;
        if (connectionType != null) signalDict[SignalKeys.ClientSideConnectionType] = connectionType;
        if (iceNoSrflx.HasValue) signalDict[SignalKeys.ClientSideIceNoSrflx] = iceNoSrflx.Value;
        if (ttsVoiceCount.HasValue) signalDict[SignalKeys.ClientSideTtsVoiceCount] = ttsVoiceCount.Value;

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

    [Fact]
    public async Task ContributeAsync_MobileClientHintWithEthernetConnection_EmitsStrongBotContribution()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", connectionType: "ethernet");

        var contributions = await contributor.ContributeAsync(state);

        var mismatch = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("connection class", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mismatch);
        Assert.True(mismatch.ConfidenceDelta > 0.5,
            $"Expected confidence > 0.5 for mobile+ethernet damru pattern, got {mismatch.ConfidenceDelta}");
        Assert.Equal("Inconsistency", mismatch.Category);
    }

    [Fact]
    public async Task ContributeAsync_MobileClientHintWithCellularConnection_NoMismatch()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", connectionType: "cellular");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("connection class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_MobileClientHintWithWifiConnection_NoMismatch()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", connectionType: "wifi");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("connection class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_DesktopClientHintWithEthernetConnection_NoMismatch()
    {
        var desktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
        var contributor = CreateContributor();
        var state = CreateState(desktopUa, secChUaMobile: "?0", connectionType: "ethernet");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("connection class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_MobileClaimWithoutConnectionSignal_NoMismatch()
    {
        // No beacon arrived - the connection signal isn't set. Don't infer the mismatch.
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", connectionType: null);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("connection class", StringComparison.OrdinalIgnoreCase));
    }

    // ===== Plan 3a: WebRTC ICE probe =====================================
    // damru blocks UDP egress on Chrome's UID via iptables, so RTCPeerConnection
    // constructs fine and createOffer() succeeds, but no srflx candidate ever
    // materialises. Real mobile networks produce at least one. The probe runs
    // a ~2.5s timer client-side and reports whether srflx was seen. The check
    // fires when mobile UA-CH says ?1 AND the probe completed (no error) AND
    // no srflx candidate was observed.

    [Fact]
    public async Task ContributeAsync_MobileClaimWithIceNoSrflx_EmitsContribution()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", iceNoSrflx: true);

        var contributions = await contributor.ContributeAsync(state);

        var mismatch = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("WebRTC", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mismatch);
        Assert.True(mismatch.ConfidenceDelta > 0.4,
            $"Expected confidence > 0.4 for mobile+ice-no-srflx, got {mismatch.ConfidenceDelta}");
        Assert.Equal("Inconsistency", mismatch.Category);
    }

    [Fact]
    public async Task ContributeAsync_MobileClaimWithIceSrflxPresent_NoMismatch()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", iceNoSrflx: false);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("WebRTC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_DesktopWithIceNoSrflx_NoMismatch()
    {
        // ICE check is mobile-gated -- desktop probe behaviour is too variable
        // (corporate networks, restrictive NATs, VPNs) to fire on its own.
        var desktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
        var contributor = CreateContributor();
        var state = CreateState(desktopUa, secChUaMobile: "?0", iceNoSrflx: true);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("WebRTC", StringComparison.OrdinalIgnoreCase));
    }

    // ===== Plan 3b: TTS voice list at first paint ========================
    // damru runs a fresh Redroid container per session; Android TTS engines
    // boot lazily and getVoices().length stays 0 until the user gestures.
    // Real Android Chrome populates voices before first paint. iOS Safari has
    // different voice behaviour so the check is gated on "UA contains Android".

    [Fact]
    public async Task ContributeAsync_AndroidUaWithEmptyVoiceList_EmitsContribution()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", ttsVoiceCount: 0);

        var contributions = await contributor.ContributeAsync(state);

        var mismatch = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("voice", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mismatch);
        Assert.True(mismatch.ConfidenceDelta > 0.3,
            $"Expected confidence > 0.3 for android+empty-voices, got {mismatch.ConfidenceDelta}");
        Assert.Equal("Inconsistency", mismatch.Category);
    }

    [Fact]
    public async Task ContributeAsync_AndroidUaWithVoiceList_NoMismatch()
    {
        var contributor = CreateContributor();
        var state = CreateState(AndroidChromeUa, secChUaMobile: "?1", ttsVoiceCount: 12);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_IPhoneUaWithEmptyVoiceList_NoMismatch()
    {
        // TTS check is gated on UA contains "Android" -- iOS Safari has its
        // own TTS behaviour and zero voices is not bot-indicative there.
        var iPhoneUa = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
        var contributor = CreateContributor();
        var state = CreateState(iPhoneUa, secChUaMobile: "?1", ttsVoiceCount: 0);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }
}
