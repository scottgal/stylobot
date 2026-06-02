using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.ClientSide;

/// <summary>
///     End-to-end coverage for the adblocker-probe flow built in ebfe7a11.
///     Three pieces it pins:
///       1. <see cref="BrowserFingerprintAnalyzer"/> recognises an adblocker-only
///          beacon (Adblocker=true + Timestamp=0) and short-circuits the integrity
///          analysis instead of writing zero-valued fields that downstream code
///          would mis-classify as a low-integrity bot.
///       2. <see cref="ClientSideDetector"/> emits a single
///          <c>ClientSideReasons.AdblockerDetected</c> reason for the next request
///          when the stored fingerprint flag is set -- skips headless / integrity /
///          consistency checks that would otherwise drag a real human user into
///          bot territory.
///       3. The /bot-detection/fingerprint endpoint accepts the token via the body
///          (`t` field) when the X-ML-BotD-Token header is absent -- the
///          adblocker probe uses navigator.sendBeacon which can't set headers.
/// </summary>
public class AdblockerProbeFlowTests
{
    // ── 1. analyzer ──────────────────────────────────────────────────────────

    [Fact]
    public void Analyzer_AdblockerOnlyBeacon_SkipsIntegrityAnalysisAndFlagsAdblocker()
    {
        var analyzer = new BrowserFingerprintAnalyzer(NullLogger<BrowserFingerprintAnalyzer>.Instance);

        // The probe beacon contains adblocker=true + provider; everything else is
        // default because the main fingerprint script was blocked. Critically,
        // Timestamp stays at 0 -- the trigger the analyzer keys on.
        var data = new BrowserFingerprintData
        {
            Adblocker = true,
            AdblockerProvider = "adsense",
            Timestamp = 0,
        };

        var result = analyzer.Analyze(data, requestId: "req-test-1");

        // Adblocker fields propagated.
        Assert.True(result.Adblocker);
        Assert.Equal("adsense", result.AdblockerProvider);
        // Integrity NOT analysed (would be 0 if the analyzer had run, which would
        // mark a real human as low-integrity downstream). 50 = neutral sentinel
        // the short-circuit writes.
        Assert.Equal(50, result.BrowserIntegrityScore);
        Assert.Equal(50, result.FingerprintConsistencyScore);
        Assert.Equal(0, result.HeadlessLikelihood);
        Assert.Equal("adblocker-only", result.FingerprintHash);
        // Single descriptive reason; no integrity-deduction noise from the main
        // analysis path.
        Assert.Single(result.Reasons);
        Assert.Contains("Adblocker", result.Reasons[0]);
    }

    [Fact]
    public void Analyzer_NormalFingerprintWithAdblockerTrue_DoesNotShortCircuit()
    {
        // A page where the fingerprint script DID run and the probe ALSO reported
        // adblocker=true. The Timestamp is non-zero, so the analyzer runs the
        // full integrity check. The adblocker flag still propagates.
        var analyzer = new BrowserFingerprintAnalyzer(NullLogger<BrowserFingerprintAnalyzer>.Instance);

        var data = new BrowserFingerprintData
        {
            Adblocker = true,
            AdblockerProvider = "adsense",
            Timestamp = 1700000000,
            Platform = "Win32",
            HardwareConcurrency = 8,
            Languages = "en-US,en",
            ScreenResolution = "1920x1080",
        };

        var result = analyzer.Analyze(data, requestId: "req-test-2");

        Assert.True(result.Adblocker);
        Assert.NotEqual("adblocker-only", result.FingerprintHash); // ran full analysis
    }

    // ── 2. detector ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Detector_AdblockerOnlyFingerprint_EmitsAdblockerDetectedReason()
    {
        var stored = new BrowserFingerprintResult
        {
            RequestId = "req-stored-1",
            Adblocker = true,
            AdblockerProvider = "adsense",
            FingerprintHash = "adblocker-only",
            BrowserIntegrityScore = 50,
            FingerprintConsistencyScore = 50,
            HeadlessLikelihood = 0,
        };

        var fingerprintStore = new Mock<IBrowserFingerprintStore>();
        fingerprintStore.Setup(s => s.Get(It.IsAny<string>())).Returns(stored);

        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            Options.Create(new BotDetectionOptions
            {
                ClientSide = new ClientSideOptions { Enabled = true }
            }),
            fingerprintStore.Object);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)";

        var result = await detector.DetectAsync(context);

        // Single AdblockerDetected reason; NO bot-tilting reasons from the
        // headless / integrity / consistency branches that come AFTER the
        // early-return. Confidence stays at zero (no bot signal contributed
        // -- the contributor handles the human-affinity bias upstream).
        Assert.Single(result.Reasons);
        Assert.Equal(ClientSideReasons.AdblockerDetected, result.Reasons[0].Detail);
        Assert.Equal(0, result.Reasons[0].ConfidenceImpact);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task Detector_FingerprintWithAdblockerFalse_RunsNormalAnalysisPath()
    {
        // The reverse case: stored fingerprint has Adblocker=false. The detector
        // must NOT take the adblocker-only branch -- it runs the integrity /
        // headless / consistency checks. This pins the gate.
        var stored = new BrowserFingerprintResult
        {
            RequestId = "req-stored-2",
            Adblocker = false,
            FingerprintHash = "normal-fingerprint-hash",
            BrowserIntegrityScore = 80,
            FingerprintConsistencyScore = 90,
            HeadlessLikelihood = 0.1,
        };

        var fingerprintStore = new Mock<IBrowserFingerprintStore>();
        fingerprintStore.Setup(s => s.Get(It.IsAny<string>())).Returns(stored);

        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            Options.Create(new BotDetectionOptions
            {
                ClientSide = new ClientSideOptions { Enabled = true }
            }),
            fingerprintStore.Object);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)";

        var result = await detector.DetectAsync(context);

        // FingerprintFoundClean sentinel = clean fingerprint, full analysis ran,
        // no adblocker early-return.
        Assert.DoesNotContain(result.Reasons, r => r.Detail == ClientSideReasons.AdblockerDetected);
        Assert.Single(result.Reasons);
        Assert.Equal(ClientSideReasons.FingerprintFoundClean, result.Reasons[0].Detail);
    }

    // ── 3. lineage of the FingerprintData fields the wire shape relies on ────

    [Fact]
    public void BrowserFingerprintData_BodyToken_SerialisesUnderShortName()
    {
        // The endpoint reads body.t as the sendBeacon token-fallback. The JSON
        // property attribute must be literally "t" -- if anyone renames the
        // property without thinking, the wire contract breaks silently.
        var prop = typeof(BrowserFingerprintData).GetProperty(nameof(BrowserFingerprintData.BodyToken));
        Assert.NotNull(prop);
        var attr = (System.Text.Json.Serialization.JsonPropertyNameAttribute?)
            Attribute.GetCustomAttribute(prop!, typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute));
        Assert.NotNull(attr);
        Assert.Equal("t", attr!.Name);
    }
}
