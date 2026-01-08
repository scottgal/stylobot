using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects bots based on IP address analysis.
///     Uses pre-parsed CIDR ranges for optimal performance.
/// </summary>
public class IpDetector : IDetector
{
    private readonly ILogger<IpDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;

    // Pre-parsed static CIDR ranges (parsed once at construction)
    private readonly List<ParsedCidrRange> _staticCidrRanges;

    public IpDetector(
        ILogger<IpDetector> logger,
        IOptions<BotDetectionOptions> options,
        ICompiledPatternCache? patternCache = null,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _patternCache = patternCache;
        _metrics = metrics;

        // Pre-parse static CIDR ranges at construction time
        _staticCidrRanges = new List<ParsedCidrRange>();
        foreach (var prefix in _options.DatacenterIpPrefixes)
            if (ParsedCidrRange.TryParse(prefix, out var range) && range != null)
                _staticCidrRanges.Add(range);
            else
                _logger.LogWarning("Failed to parse static CIDR prefix: {Prefix}", prefix);
    }

    public string Name => "IP Detector";

    /// <summary>Stage 0: Raw signal extraction - no dependencies</summary>
    public DetectorStage Stage => DetectorStage.RawSignals;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();
        var ipAddress = GetClientIpAddress(context);

        try
        {
            if (ipAddress == null) return Task.FromResult(result);

            var confidence = 0.0;
            var reasons = new List<DetectionReason>();

            var cidrStartTime = Stopwatch.GetTimestamp();

            // Check downloaded cloud provider IP ranges first (most accurate)
            if (_patternCache != null && _patternCache.DownloadedCidrRanges.Count > 0)
                if (_patternCache.IsInAnyCidrRange(ipAddress, out var matchedRange))
                {
                    confidence += 0.5;
                    var provider = DetectProviderFromCidr(matchedRange);
                    reasons.Add(new DetectionReason
                    {
                        Category = "IP",
                        Detail = $"IP {ipAddress} matched cloud provider range: {provider ?? matchedRange}",
                        ConfidenceImpact = 0.5
                    });
                }

            // Check static datacenter ranges (pre-parsed for speed)
            if (confidence == 0 && IsDatacenterIp(ipAddress, out var datacenterRange))
            {
                confidence += 0.4;
                reasons.Add(new DetectionReason
                {
                    Category = "IP",
                    Detail = $"IP {ipAddress} is in datacenter range ({datacenterRange})",
                    ConfidenceImpact = 0.4
                });
            }

            // Check for known cloud providers by first octet (fast heuristic)
            if (confidence == 0)
            {
                var cloudProvider = GetCloudProviderByOctet(ipAddress);
                if (cloudProvider != null)
                {
                    confidence += 0.3;
                    reasons.Add(new DetectionReason
                    {
                        Category = "IP",
                        Detail = $"IP from cloud provider (heuristic): {cloudProvider}",
                        ConfidenceImpact = 0.3
                    });
                }
            }

            var cidrDuration = Stopwatch.GetElapsedTime(cidrStartTime);
            _metrics?.RecordCidrMatch(cidrDuration, confidence > 0);

            // Check if it's a Tor exit node
            if (IsTorExitNode(ipAddress))
            {
                confidence += 0.5;
                reasons.Add(new DetectionReason
                {
                    Category = "IP",
                    Detail = "IP is a Tor exit node",
                    ConfidenceImpact = 0.5
                });
                result.BotType = BotType.MaliciousBot;
            }

            result.Confidence = Math.Min(confidence, 1.0);
            result.Reasons = reasons;

            return Task.FromResult(result);
        }
        finally
        {
            stopwatch.Stop();
            _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
                Name);
        }
    }

    private IPAddress? GetClientIpAddress(HttpContext context)
    {
        // Check for forwarded IP first (behind proxy/load balancer)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ips.Length > 0 && IPAddress.TryParse(ips[0].Trim(), out var ip))
                return ip;
        }

        // Fall back to connection remote IP
        return context.Connection.RemoteIpAddress;
    }

    private bool IsDatacenterIp(IPAddress ipAddress, out string? matchedRange)
    {
        matchedRange = null;

        // Use pre-parsed ranges for fast matching
        foreach (var range in _staticCidrRanges)
            if (range.Contains(ipAddress))
            {
                matchedRange = range.OriginalCidr;
                return true;
            }

        return false;
    }

    private string? GetCloudProviderByOctet(IPAddress ipAddress)
    {
        var bytes = ipAddress.GetAddressBytes();
        if (bytes.Length != 4)
            return null; // Only check IPv4 for simplicity

        var firstOctet = bytes[0];

        // Simple heuristic based on first octet
        return firstOctet switch
        {
            3 or 13 or 18 or 52 => "AWS",
            20 or 40 or 104 => "Azure",
            34 or 35 => "Google Cloud",
            138 or 139 or 140 => "Oracle Cloud",
            _ => null
        };
    }

    private string? DetectProviderFromCidr(string? cidr)
    {
        if (string.IsNullOrEmpty(cidr))
            return null;

        // Based on typical ranges
        if (cidr.StartsWith("3.") || cidr.StartsWith("13.") || cidr.StartsWith("18.") || cidr.StartsWith("52."))
            return "AWS";
        if (cidr.StartsWith("34.") || cidr.StartsWith("35."))
            return "Google Cloud";
        if (cidr.StartsWith("104.") || cidr.StartsWith("173."))
            return "Cloudflare";
        if (cidr.StartsWith("20.") || cidr.StartsWith("40."))
            return "Azure";

        return "Cloud Provider";
    }

    private bool IsTorExitNode(IPAddress ipAddress)
    {
        // This is a placeholder - in production, you'd maintain a list of Tor exit nodes
        // You can get this from https://check.torproject.org/exit-addresses
        return false;
    }
}