using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Project Honeypot HTTP:BL integration for IP reputation checking.
///     Uses DNS lookups to check visitor IP addresses against Project Honeypot's database.
///     See: https://www.projecthoneypot.org/httpbl_api.php
///     Query Format: [AccessKey].[Reversed IP].dnsbl.httpbl.org
///     Response: 127.[Days].[ThreatScore].[Type]
///     Requires a free API key from Project Honeypot.
///     This contributor runs in Wave 2 (after initial IP analysis).
/// </summary>
public class ProjectHoneypotContributor : ContributingDetectorBase
{
    // Cache for DNS lookups to avoid repeated queries
    private static readonly ConcurrentDictionary<string, (HoneypotResult Result, DateTime Expires)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private readonly ILogger<ProjectHoneypotContributor> _logger;
    private readonly BotDetectionOptions _options;

    public ProjectHoneypotContributor(
        ILogger<ProjectHoneypotContributor> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public override string Name => "ProjectHoneypot";
    public override int Priority => 15; // Run after basic IP analysis
    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(1000); // DNS should be fast
    public override bool IsOptional => true; // Don't fail detection if DNS lookup fails

    // Run when we have an IP address
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.ClientIp)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Check for test mode simulation via User-Agent (only when test mode is enabled)
        if (_options.EnableTestMode)
        {
            var userAgent = state.HttpContext?.Request.Headers["User-Agent"].FirstOrDefault() ?? "";
            var testResult = CheckTestModeSimulation(userAgent);
            if (testResult != null) return testResult;
        }

        // Check if Project Honeypot is enabled and configured
        if (!_options.ProjectHoneypot.Enabled ||
            string.IsNullOrWhiteSpace(_options.ProjectHoneypot.AccessKey))
        {
            var reason = !_options.ProjectHoneypot.Enabled
                ? "Skipped: ProjectHoneypot is disabled in configuration"
                : "Skipped: ProjectHoneypot AccessKey not configured (get free key at projecthoneypot.org)";
            return Single(DetectionContribution.Info(Name, "ProjectHoneypot", reason));
        }

        var clientIp = state.GetSignal<string>(SignalKeys.ClientIp);
        if (string.IsNullOrWhiteSpace(clientIp)) return None();

        // Skip local/private IPs - they won't be in Project Honeypot
        var isLocal = state.GetSignal<bool>(SignalKeys.IpIsLocal);
        if (isLocal) return Single(DetectionContribution.Info(Name, "ProjectHoneypot", "Skipped: localhost/private IP"));

        // Parse the IP address
        if (!IPAddress.TryParse(clientIp, out var ipAddress)) return None();

        // Only IPv4 is supported by HTTP:BL
        if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            _logger.LogDebug("Skipping Project Honeypot lookup for IPv6 address: {Ip}", MaskIp(clientIp));
            return None();
        }

        try
        {
            var result = await LookupIpAsync(clientIp, cancellationToken);

            if (result == null || !result.IsListed)
                // IP not found in database - slight positive signal
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ProjectHoneypot",
                    ConfidenceDelta = -0.05,
                    Weight = 0.8,
                    Reason = "IP not listed in Project Honeypot database",
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.HoneypotChecked, true)
                        .Add(SignalKeys.HoneypotListed, false)
                });

            // IP is listed - determine severity based on threat score and type
            var contributions = new List<DetectionContribution>();
            var signals = ImmutableDictionary.CreateBuilder<string, object>();

            signals.Add(SignalKeys.HoneypotChecked, true);
            signals.Add(SignalKeys.HoneypotListed, true);
            signals.Add(SignalKeys.HoneypotThreatScore, result.ThreatScore);
            signals.Add(SignalKeys.HoneypotVisitorType, result.VisitorType.ToString());
            signals.Add(SignalKeys.HoneypotDaysSinceLastActivity, result.DaysSinceLastActivity);

            // Search engines get a pass (type 0)
            if (result.VisitorType == HoneypotVisitorType.SearchEngine)
            {
                _logger.LogDebug("IP {Ip} identified as search engine by Project Honeypot", MaskIp(clientIp));
                contributions.Add(DetectionContribution.VerifiedGoodBot(
                        Name,
                        "IP verified as search engine by Project Honeypot",
                        "Search Engine (Project Honeypot)")
                    with
                    {
                        Signals = signals.ToImmutable()
                    });
                return contributions;
            }

            // Calculate confidence based on threat score and recency
            var confidence = CalculateConfidence(result);
            var botType = DetermineBoType(result);
            var reason = BuildReason(result);

            _logger.LogWarning(
                "IP {Ip} listed in Project Honeypot: Type={Type}, ThreatScore={Score}, DaysAgo={Days}",
                MaskIp(clientIp), result.VisitorType, result.ThreatScore, result.DaysSinceLastActivity);

            // High threat scores trigger early exit
            if (result.ThreatScore >= _options.ProjectHoneypot.HighThreatThreshold)
                contributions.Add(DetectionContribution.VerifiedBot(
                        Name,
                        reason,
                        botType: botType.ToString(),
                        botName: $"Honeypot Threat ({result.VisitorType})")
                    with
                    {
                        ConfidenceDelta = confidence,
                        Weight = 1.8, // High weight for verified threats
                        Signals = signals.ToImmutable()
                    });
            else
                contributions.Add(DetectionContribution.Bot(
                        Name,
                        "ProjectHoneypot",
                        confidence,
                        reason,
                        weight: 1.5,
                        botType: botType.ToString())
                    with
                    {
                        Signals = signals.ToImmutable()
                    });

            return contributions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Project Honeypot lookup failed for IP: {Ip}", clientIp);
            return None();
        }
    }

    private async Task<HoneypotResult?> LookupIpAsync(string ip, CancellationToken cancellationToken)
    {
        // Check cache first
        if (_cache.TryGetValue(ip, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Result;

        // Build DNS query: [key].[reversed-ip].dnsbl.httpbl.org
        var parts = ip.Split('.');
        Array.Reverse(parts);
        var reversedIp = string.Join(".", parts);
        var query = $"{_options.ProjectHoneypot.AccessKey}.{reversedIp}.dnsbl.httpbl.org";

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(query, cancellationToken);

            if (addresses.Length == 0)
            {
                // Cache the "not found" result
                _cache[ip] = (new HoneypotResult { IsListed = false }, DateTime.UtcNow.Add(CacheDuration));
                return null;
            }

            // Parse the response: 127.[days].[threat].[type]
            var response = addresses[0].GetAddressBytes();

            // First octet must be 127 for valid response
            if (response[0] != 127)
            {
                _logger.LogWarning("Invalid Project Honeypot response for {Ip}: first octet is {Octet}", ip,
                    response[0]);
                return null;
            }

            var result = new HoneypotResult
            {
                IsListed = true,
                DaysSinceLastActivity = response[1],
                ThreatScore = response[2],
                VisitorType = ParseVisitorType(response[3])
            };

            // Cache the result
            _cache[ip] = (result, DateTime.UtcNow.Add(CacheDuration));

            return result;
        }
        catch (SocketException)
        {
            // NXDOMAIN means IP is not in the database
            _cache[ip] = (new HoneypotResult { IsListed = false }, DateTime.UtcNow.Add(CacheDuration));
            return null;
        }
    }

    private static HoneypotVisitorType ParseVisitorType(byte typeByte)
    {
        // The type byte is a bitset:
        // 0 = Search Engine
        // 1 = Suspicious
        // 2 = Harvester
        // 4 = Comment Spammer
        // Combinations possible (e.g., 3 = Suspicious + Harvester)

        if (typeByte == 0)
            return HoneypotVisitorType.SearchEngine;

        var type = HoneypotVisitorType.None;

        if ((typeByte & 1) != 0)
            type |= HoneypotVisitorType.Suspicious;
        if ((typeByte & 2) != 0)
            type |= HoneypotVisitorType.Harvester;
        if ((typeByte & 4) != 0)
            type |= HoneypotVisitorType.CommentSpammer;

        return type;
    }

    private double CalculateConfidence(HoneypotResult result)
    {
        // Base confidence on threat score (0-255, logarithmic scale)
        var baseConfidence = result.ThreatScore switch
        {
            >= 100 => 0.95,
            >= 50 => 0.85,
            >= 25 => 0.70,
            >= 10 => 0.55,
            >= 5 => 0.40,
            _ => 0.30
        };

        // Reduce confidence for older entries
        var ageFactor = result.DaysSinceLastActivity switch
        {
            0 => 1.0, // Today
            <= 7 => 0.95, // Last week
            <= 30 => 0.85, // Last month
            <= 90 => 0.70, // Last quarter
            <= 180 => 0.50, // Last 6 months
            _ => 0.30 // Older
        };

        // Increase confidence for more serious visitor types
        var typeFactor = 1.0;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer))
            typeFactor *= 1.1;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester))
            typeFactor *= 1.15;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious))
            typeFactor *= 1.05;

        return Math.Min(baseConfidence * ageFactor * typeFactor, 0.99);
    }

    private static BotType DetermineBoType(HoneypotResult result)
    {
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer))
            return BotType.MaliciousBot;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester))
            return BotType.Scraper;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious))
            return BotType.Unknown;

        return BotType.Unknown;
    }

    private static string BuildReason(HoneypotResult result)
    {
        var types = new List<string>();
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious))
            types.Add("Suspicious");
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester))
            types.Add("Harvester");
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer))
            types.Add("CommentSpammer");

        var typeStr = types.Count > 0 ? string.Join(", ", types) : "Unknown";
        return
            $"IP listed in Project Honeypot: {typeStr} (Threat: {result.ThreatScore}, Last seen: {result.DaysSinceLastActivity} days ago)";
    }

    /// <summary>
    ///     Check for test mode simulation markers in User-Agent.
    ///     Format: &lt;test-honeypot:type&gt; where type is harvester, spammer, or suspicious
    /// </summary>
    private IReadOnlyList<DetectionContribution>? CheckTestModeSimulation(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return null;

        // Check for test-honeypot markers
        const string prefix = "<test-honeypot:";
        var startIdx = userAgent.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
            return null;

        var typeStart = startIdx + prefix.Length;
        var endIdx = userAgent.IndexOf('>', typeStart);
        if (endIdx < 0)
            return null;

        var testType = userAgent.Substring(typeStart, endIdx - typeStart).ToLowerInvariant();

        _logger.LogInformation("Project Honeypot test mode simulation: {Type}", testType);

        // Create simulated honeypot result based on test type
        var result = testType switch
        {
            "harvester" => new HoneypotResult
            {
                IsListed = true,
                DaysSinceLastActivity = 3,
                ThreatScore = 75,
                VisitorType = HoneypotVisitorType.Harvester
            },
            "spammer" => new HoneypotResult
            {
                IsListed = true,
                DaysSinceLastActivity = 1,
                ThreatScore = 100,
                VisitorType = HoneypotVisitorType.CommentSpammer
            },
            "suspicious" => new HoneypotResult
            {
                IsListed = true,
                DaysSinceLastActivity = 14,
                ThreatScore = 35,
                VisitorType = HoneypotVisitorType.Suspicious
            },
            _ => null
        };

        if (result == null)
        {
            _logger.LogWarning("Unknown honeypot test type: {Type}", testType);
            return null;
        }

        // Generate contributions using the same logic as real lookups
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        signals.Add(SignalKeys.HoneypotChecked, true);
        signals.Add(SignalKeys.HoneypotListed, true);
        signals.Add(SignalKeys.HoneypotThreatScore, result.ThreatScore);
        signals.Add(SignalKeys.HoneypotVisitorType, result.VisitorType.ToString());
        signals.Add(SignalKeys.HoneypotDaysSinceLastActivity, result.DaysSinceLastActivity);
        signals.Add("HoneypotTestMode", true);

        var confidence = CalculateConfidence(result);
        var botType = DetermineBoType(result);
        var reason = $"[TEST MODE] {BuildReason(result)}";

        // High threat scores trigger verified bad bot
        if (result.ThreatScore >= _options.ProjectHoneypot.HighThreatThreshold)
            contributions.Add(DetectionContribution.VerifiedBot(
                    Name,
                    reason,
                    botType: botType.ToString(),
                    botName: $"Honeypot Threat ({result.VisitorType})")
                with
                {
                    ConfidenceDelta = confidence,
                    Weight = 1.8,
                    Signals = signals.ToImmutable()
                });
        else
            contributions.Add(DetectionContribution.Bot(
                    Name,
                    "ProjectHoneypot",
                    confidence,
                    reason,
                    weight: 1.5,
                    botType: botType.ToString())
                with
                {
                    Signals = signals.ToImmutable()
                });

        return contributions;
    }

    private static string MaskIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";
        return ip.Length > 10 ? ip[..10] + "..." : ip;
    }
}

/// <summary>
///     Result from Project Honeypot HTTP:BL lookup.
/// </summary>
public class HoneypotResult
{
    public bool IsListed { get; set; }
    public int DaysSinceLastActivity { get; set; }
    public int ThreatScore { get; set; }
    public HoneypotVisitorType VisitorType { get; set; }
}

/// <summary>
///     Visitor types from Project Honeypot HTTP:BL API.
///     The type byte is a bitfield where:
///     - 0 = Search Engine (special case, not a bitflag)
///     - Bit 0 (value 1) = Suspicious
///     - Bit 1 (value 2) = Harvester
///     - Bit 2 (value 4) = Comment Spammer
///     Combinations are possible (e.g., 3 = Suspicious + Harvester, 7 = all three).
/// </summary>
[Flags]
public enum HoneypotVisitorType
{
    /// <summary>No type assigned</summary>
    None = 0,

    /// <summary>Bit 0: IP has exhibited suspicious behavior</summary>
    Suspicious = 1,

    /// <summary>Bit 1: IP has been caught harvesting email addresses</summary>
    Harvester = 2,

    /// <summary>Bit 2: IP has been caught posting spam comments</summary>
    CommentSpammer = 4,

    /// <summary>Special type: IP belongs to a known search engine (type byte = 0)</summary>
    SearchEngine = 256 // Out of the byte range, used as sentinel
}