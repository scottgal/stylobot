using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detector that incorporates client-side browser fingerprint data.
///     Uses results from the JavaScript fingerprinting script to detect
///     headless browsers and automation frameworks.
/// </summary>
public class ClientSideDetector : IDetector
{
    private readonly ILogger<ClientSideDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly IBrowserFingerprintStore _store;

    public ClientSideDetector(
        ILogger<ClientSideDetector> logger,
        IOptions<BotDetectionOptions> options,
        IBrowserFingerprintStore store,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _store = store;
        _metrics = metrics;
    }

    public string Name => "Client-Side Detector";

    /// <summary>Stage 0: Raw signal extraction - no dependencies</summary>
    public DetectorStage Stage => DetectorStage.RawSignals;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();

        if (!_options.ClientSide.Enabled) return Task.FromResult(result);

        try
        {
            // Get IP hash to look up fingerprint
            var ipHash = HashIp(GetClientIp(context));
            var fingerprint = _store.Get(ipHash);

            if (fingerprint == null)
            {
                // No fingerprint available - might be first request, JS not executed,
                // privacy tool, or API call. This is NOT suspicious by itself.
                // Missing data ≠ malicious - treat as neutral.
                if (_options.ClientSide.Enabled)
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "ClientSide",
                        Detail = "No browser fingerprint available (awaiting JS execution)",
                        ConfidenceImpact = 0 // Neutral - absence of data is not evidence
                    });
                // No confidence adjustment - neutral signal
                stopwatch.Stop();
                return Task.FromResult(result);
            }

            // Use fingerprint data for detection
            var opts = _options.ClientSide;

            // Check headless likelihood
            if (fingerprint.HeadlessLikelihood >= opts.HeadlessThreshold)
            {
                result.Confidence += fingerprint.HeadlessLikelihood * 0.8; // Weight headless highly
                result.BotType = BotType.Scraper;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "ClientSide",
                    Detail = $"Headless browser detected (likelihood: {fingerprint.HeadlessLikelihood:F2})",
                    ConfidenceImpact = fingerprint.HeadlessLikelihood * 0.8
                });

                if (!string.IsNullOrEmpty(fingerprint.DetectedAutomation))
                {
                    result.BotName = fingerprint.DetectedAutomation;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "ClientSide",
                        Detail = $"Automation framework detected: {fingerprint.DetectedAutomation}",
                        ConfidenceImpact = 0.5
                    });
                }
            }

            // Check browser integrity score
            if (fingerprint.BrowserIntegrityScore < opts.MinIntegrityScore)
            {
                var integrityImpact = (opts.MinIntegrityScore - fingerprint.BrowserIntegrityScore) / 100.0 * 0.5;
                result.Confidence += integrityImpact;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "ClientSide",
                    Detail = $"Low browser integrity score: {fingerprint.BrowserIntegrityScore}/100",
                    ConfidenceImpact = integrityImpact
                });
            }

            // Check fingerprint consistency
            if (fingerprint.FingerprintConsistencyScore < 80)
            {
                var consistencyImpact = (80 - fingerprint.FingerprintConsistencyScore) / 100.0 * 0.3;
                result.Confidence += consistencyImpact;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "ClientSide",
                    Detail =
                        $"Fingerprint inconsistencies detected (score: {fingerprint.FingerprintConsistencyScore}/100)",
                    ConfidenceImpact = consistencyImpact
                });
            }

            // Add specific reasons from fingerprint analysis
            foreach (var reason in fingerprint.Reasons.Take(3)) // Limit to top 3
                result.Reasons.Add(new DetectionReason
                {
                    Category = "ClientSide",
                    Detail = reason,
                    ConfidenceImpact = 0.1
                });

            // Cap confidence at 1.0
            result.Confidence = Math.Min(1.0, result.Confidence);

            stopwatch.Stop();
            _metrics?.RecordDetection(
                result.Confidence,
                result.Confidence > _options.BotThreshold,
                stopwatch.Elapsed,
                Name);

            _logger.LogDebug(
                "ClientSide detection: Confidence={Confidence:F2}, FingerprintHash={Hash}",
                result.Confidence, fingerprint.FingerprintHash);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "ClientSide detection failed");
            _metrics?.RecordError(Name, ex.GetType().Name);
        }

        return Task.FromResult(result);
    }

    private static string HashIp(string ip)
    {
        // Fast XxHash64 - MUST match BrowserTokenService.HashIp
        var ipBytes = Encoding.UTF8.GetBytes(ip + ":MLBotD-IP-Salt");
        var hash = XxHash64.Hash(ipBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetClientIp(HttpContext context)
    {
        var headers = context.Request.Headers;

        if (headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
            return cfIp.ToString();

        if (headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
        {
            var firstIp = xff.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
                return firstIp;
        }

        if (headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrEmpty(realIp))
            return realIp.ToString();

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}