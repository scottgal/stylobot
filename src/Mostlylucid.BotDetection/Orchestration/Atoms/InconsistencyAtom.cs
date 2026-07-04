using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that looks for cross-signal
///     mismatches AFTER raw signals are collected: browser UA claim vs
///     datacenter IP, Sec-CH-UA-Mobile vs non-mobile connection type, Chrome
///     UA vs missing Client Hints, Kameleo-style integer-only mouse
///     coordinates. Priority 50 -- runs after UA / IP / client-side sensors.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>InconsistencyContributor</c>. Preserves the full carve-out
///         ladder for legitimate no-Client-Hints requests (service worker /
///         WebSocket upgrade / same-origin fetch / plain HTTP/1.1) and the
///         damru / Kameleo / Redroid-specific composite checks.
///     </para>
/// </remarks>
public sealed partial class InconsistencyAtom : DetectorAtomBase
{
    private readonly ILogger<InconsistencyAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InconsistencyAtom(
        ILogger<InconsistencyAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Inconsistency", category: "Inconsistency")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 50;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    private double DatacenterBrowserConfidence => _configProvider.GetParameter(Name, "datacenter_browser_confidence", 0.7);
    private double MissingLanguageConfidence => _configProvider.GetParameter(Name, "missing_language_confidence", 0.5);
    private double MissingClientHintsConfidence => _configProvider.GetParameter(Name, "missing_client_hints_confidence", 0.2);
    private double OutdatedBrowserConfidence => _configProvider.GetParameter(Name, "outdated_browser_confidence", 0.3);
    private int MinChromeVersion => _configProvider.GetParameter(Name, "min_chrome_version", 90);
    private double MobileConnectionMismatchConfidence => _configProvider.GetParameter(Name, "mobile_connection_mismatch_confidence", 0.85);
    private double MobileConnectionMismatchWeight => _configProvider.GetParameter(Name, "mobile_connection_mismatch_weight", 1.5);
    private double MobileIceNoSrflxConfidence => _configProvider.GetParameter(Name, "mobile_ice_no_srflx_confidence", 0.7);
    private double MobileIceNoSrflxWeight => _configProvider.GetParameter(Name, "mobile_ice_no_srflx_weight", 1.3);
    private double AndroidEmptyVoicesConfidence => _configProvider.GetParameter(Name, "android_empty_voices_confidence", 0.5);
    private double AndroidEmptyVoicesWeight => _configProvider.GetParameter(Name, "android_empty_voices_weight", 1.2);
    private int KameleoMouseMinSamples => _configProvider.GetParameter(Name, "kameleo_mouse_min_samples", 10);
    private double KameleoMouseCvCeiling => _configProvider.GetParameter(Name, "kameleo_mouse_cv_ceiling", 0.3);
    private double KameleoMouseConfidence => _configProvider.GetParameter(Name, "kameleo_mouse_confidence", 0.7);
    private double KameleoMouseWeight => _configProvider.GetParameter(Name, "kameleo_mouse_weight", 1.3);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();
        var userAgent = sink.ReadHint(SignalKeys.UserAgent) ?? string.Empty;
        var isDatacenter = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);
        var headers = context.Request.Headers;

        if (isDatacenter && LooksLikeBrowser(userAgent))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = DatacenterBrowserConfidence,
                Weight = 1.5,
                Reason = "Browser User-Agent from datacenter IP",
                BotType = BotType.Unknown.ToString()
            });
        }

        var isWebSocketUpgrade = sink.ReadBoolHint("header.is_websocket_upgrade");
        var isSameOriginFetch = sink.ReadBoolHint(SignalKeys.HeaderSecFetchSameOrigin);

        if (LooksLikeBrowser(userAgent) && !headers.ContainsKey("Accept-Language")
            && !isWebSocketUpgrade && !isSameOriginFetch)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingLanguageConfidence,
                Weight = 1.0,
                Reason = "Browser User-Agent without Accept-Language header",
                BotType = BotType.Unknown.ToString()
            });
        }

        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var isHttp11 = context.Request.Protocol.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
        var isPlainHttp = isHttp11 && !context.Request.IsHttps;
        var isLegitimateNoHintsRequest = isWebSocketUpgrade || isSameOriginFetch || isPlainHttp
                                         || path.Contains("serviceworker")
                                         || path.Contains("sw.js")
                                         || path.Contains("worker");

        if (userAgent.Contains("Chrome/") && !headers.ContainsKey("sec-ch-ua") && !isLegitimateNoHintsRequest)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingClientHintsConfidence,
                Weight = 1.0,
                Reason = "Chrome User-Agent without Client Hints",
                BotType = BotType.Scraper.ToString()
            });
        }

        if (IsOutdatedBrowser(userAgent))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = OutdatedBrowserConfidence,
                Weight = 1.0,
                Reason = "Outdated browser version in User-Agent",
                BotType = BotType.Unknown.ToString()
            });
        }

        // Kameleo mouse-synthesis composite check
        var mouseSamplesHint = sink.ReadHint(SignalKeys.ClientMouseEvents);
        if (int.TryParse(mouseSamplesHint, out var mouseSamples) && mouseSamples >= KameleoMouseMinSamples)
        {
            var allInt = sink.ReadBoolHint(SignalKeys.ClientSideMouseAllIntegerCoords);
            var cvHint = sink.ReadHint(SignalKeys.ClientSideMouseTimingCv);
            if (allInt
                && double.TryParse(cvHint, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cv)
                && cv < KameleoMouseCvCeiling)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = KameleoMouseConfidence,
                    Weight = KameleoMouseWeight,
                    Reason = $"Mouse trace shows Kameleo-style synthesis (all-integer coords across {mouseSamples} samples, timing CV {cv:F2})",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }

        // Mobile UA-CH composite checks
        var claimsMobile = headers.TryGetValue("Sec-CH-UA-Mobile", out var mobileHeader)
                           && mobileHeader.ToString() == "?1";
        if (claimsMobile)
        {
            var connectionType = sink.ReadHint(SignalKeys.ClientSideConnectionType);
            if (!string.IsNullOrEmpty(connectionType) && IsNonMobileConnection(connectionType))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = MobileConnectionMismatchConfidence,
                    Weight = MobileConnectionMismatchWeight,
                    Reason = $"Mobile UA-CH with non-mobile connection class ({connectionType})",
                    BotType = BotType.Scraper.ToString()
                });
            }

            if (sink.ReadBoolHint(SignalKeys.ClientSideIceNoSrflx))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = MobileIceNoSrflxConfidence,
                    Weight = MobileIceNoSrflxWeight,
                    Reason = "Mobile UA-CH but WebRTC ICE probe produced no srflx candidate (UDP egress blocked)",
                    BotType = BotType.Scraper.ToString()
                });
            }

            // Presence check (as opposed to bool read) matters here: an absent
            // ClientSideTtsVoiceCount signal means "no beacon received" and must
            // NOT false-positive on every Android-UA request that hasn't beaconed.
            var ttsHint = sink.ReadHint(SignalKeys.ClientSideTtsVoiceCount);
            if (ttsHint is not null
                && int.TryParse(ttsHint, out var ttsCount)
                && ttsCount == 0
                && userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = AndroidEmptyVoicesConfidence,
                    Weight = AndroidEmptyVoicesWeight,
                    Reason = "Android UA with empty speechSynthesis voice list at first paint",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }

        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.15,
                Weight = 1.0,
                Reason = "No header/UA inconsistencies detected"
            });
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static bool LooksLikeBrowser(string userAgent)
    {
        return userAgent.Contains("Mozilla/")
               && (userAgent.Contains("Chrome") || userAgent.Contains("Firefox")
                   || userAgent.Contains("Safari") || userAgent.Contains("Edge"));
    }

    private bool IsOutdatedBrowser(string userAgent)
    {
        var chromeMatch = InconsistencyChromeVersionRegex().Match(userAgent);
        if (chromeMatch.Success && int.TryParse(chromeMatch.Groups[1].Value, out var version))
            return version < MinChromeVersion;
        return false;
    }

    private static bool IsNonMobileConnection(string connectionType) =>
        connectionType.Equals("ethernet", StringComparison.OrdinalIgnoreCase)
        || connectionType.Equals("wimax", StringComparison.OrdinalIgnoreCase)
        || connectionType.Equals("mixed", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex InconsistencyChromeVersionRegex();
}
