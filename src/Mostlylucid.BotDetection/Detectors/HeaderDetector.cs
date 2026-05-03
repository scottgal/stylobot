using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects bots based on HTTP header analysis
/// </summary>
public class HeaderDetector : IDetector
{
    // Common headers sent by real browsers
    private static readonly string[] CommonBrowserHeaders =
    [
        "Accept", "Accept-Encoding", "Accept-Language", "Cache-Control",
        "Connection", "Upgrade-Insecure-Requests"
    ];

    private readonly ILogger<HeaderDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;

    public HeaderDetector(
        ILogger<HeaderDetector> logger,
        IOptions<BotDetectionOptions> options,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _metrics = metrics;
    }

    public string Name => "Header Detector";

    /// <summary>Stage 0: Raw signal extraction - no dependencies</summary>
    public DetectorStage Stage => DetectorStage.RawSignals;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();
        var headers = context.Request.Headers;
        var confidence = 0.0;
        var reasons = new List<DetectionReason>();

        // Check for missing common browser headers
        var missingHeaders = CommonBrowserHeaders.Where(h => !headers.ContainsKey(h)).ToList();
        if (missingHeaders.Any())
        {
            var impact = Math.Min(missingHeaders.Count * 0.15, 0.6);
            confidence += impact;
            reasons.Add(new DetectionReason
            {
                Category = "Headers",
                Detail = $"Missing common browser headers: {string.Join(", ", missingHeaders)}",
                ConfidenceImpact = impact
            });
        }

        // Check Accept-Language (bots often omit or use generic values)
        if (!headers.ContainsKey("Accept-Language"))
        {
            confidence += 0.2;
            reasons.Add(new DetectionReason
            {
                Category = "Headers",
                Detail = "Missing Accept-Language header",
                ConfidenceImpact = 0.2
            });
        }
        else
        {
            var acceptLanguage = headers.AcceptLanguage.ToString();
            // Very generic or missing quality values
            if (acceptLanguage == "*" || acceptLanguage.Length < 5)
            {
                confidence += 0.15;
                reasons.Add(new DetectionReason
                {
                    Category = "Headers",
                    Detail = $"Suspicious Accept-Language value: {acceptLanguage}",
                    ConfidenceImpact = 0.15
                });
            }
        }

        // Check Accept header
        if (headers.ContainsKey("Accept"))
        {
            var accept = headers.Accept.ToString();
            // Generic "*/*" is suspicious if coming alone
            if (accept == "*/*" && !headers.ContainsKey("Accept-Language"))
            {
                confidence += 0.2;
                reasons.Add(new DetectionReason
                {
                    Category = "Headers",
                    Detail = "Generic Accept header with no Accept-Language",
                    ConfidenceImpact = 0.2
                });
            }
        }

        // Check for suspicious header values
        if (headers.ContainsKey("Connection"))
        {
            var connection = headers.Connection.ToString().ToLowerInvariant();
            if (connection == "close" && !headers.ContainsKey("Accept-Language"))
            {
                confidence += 0.15;
                reasons.Add(new DetectionReason
                {
                    Category = "Headers",
                    Detail = "Connection: close without Accept-Language",
                    ConfidenceImpact = 0.15
                });
            }
        }

        // Check for automation headers
        var automationHeaders = new[] { "X-Requested-With", "X-Automation", "X-Bot" };
        foreach (var header in automationHeaders)
            if (headers.ContainsKey(header))
            {
                confidence += 0.4;
                reasons.Add(new DetectionReason
                {
                    Category = "Headers",
                    Detail = $"Automation header present: {header}",
                    ConfidenceImpact = 0.4
                });
            }

        // Selenium/WebDriver detection
        if (headers.ContainsKey("Sec-Fetch-Site"))
        {
            // This is actually a good sign (modern browser feature)
            // Its absence with modern User-Agent is suspicious
            // We don't penalize here, but absence is noted elsewhere
        }

        // Check for header order anomalies (advanced)
        // Most browsers send headers in a consistent order
        var headerOrder = headers.Keys.ToList();
        if (headerOrder.Count > 0)
        {
            // If User-Agent is not in the first few headers, it's suspicious
            var userAgentIndex = headerOrder.IndexOf("User-Agent");
            if (userAgentIndex > 5)
            {
                confidence += 0.1;
                reasons.Add(new DetectionReason
                {
                    Category = "Headers",
                    Detail = "Unusual header ordering",
                    ConfidenceImpact = 0.1
                });
            }
        }

        // Too few headers overall
        if (headerOrder.Count < 4)
        {
            confidence += 0.3;
            reasons.Add(new DetectionReason
            {
                Category = "Headers",
                Detail = $"Very few headers present ({headerOrder.Count})",
                ConfidenceImpact = 0.3
            });
        }

        result.Confidence = Math.Min(confidence, 1.0);
        result.Reasons = reasons;

        stopwatch.Stop();
        _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
            Name);

        return Task.FromResult(result);
    }
}