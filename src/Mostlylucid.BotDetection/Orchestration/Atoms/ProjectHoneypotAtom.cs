using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that queries Project Honeypot HTTP:BL
///     via <see cref="ProjectHoneypotLookupService"/> for third-party IP
///     reputation. Priority 15 -- after IP resolution.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ProjectHoneypotContributor</c>. Preserves the test-mode UA
///         simulation (<c>&lt;test-honeypot:type&gt;</c>) that operators use
///         in learning / demo policies, and the honeypot signal bag
///         (checked / listed / threat score / visitor type / days since
///         last activity).
///     </para>
///     <para>
///         Reuses the legacy contributor's <see cref="HoneypotResult"/>
///         and <see cref="HoneypotVisitorType"/> types (declared inside
///         the contributor file) so the shared shapes need no duplication.
///     </para>
///     <para>
///         IsOptional = true, Timeout = 1 second -- DNS should be fast and
///         we never fail the pack because HTTP:BL was slow.
///     </para>
/// </remarks>
public sealed class ProjectHoneypotAtom : DetectorAtomBase
{
    private readonly ILogger<ProjectHoneypotAtom> _logger;
    private readonly ProjectHoneypotLookupService _lookupService;
    private readonly BotDetectionOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ProjectHoneypotAtom(
        ILogger<ProjectHoneypotAtom> logger,
        IOptions<BotDetectionOptions> options,
        ProjectHoneypotLookupService lookupService,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "ProjectHoneypot", category: "ProjectHoneypot")
    {
        _logger = logger;
        _options = options.Value;
        _lookupService = lookupService;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 15;
    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(1000);
    public override bool IsOptional => true;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.ClientIp };

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        sink.Raise("honeypot.ran", sessionId);

        if (_options.EnableTestMode)
        {
            var userAgent = _httpContextAccessor.HttpContext?.Request.Headers["User-Agent"].FirstOrDefault() ?? string.Empty;
            var testResult = CheckTestModeSimulation(sink, sessionId, userAgent);
            if (testResult is not null) return testResult;
        }

        if (!_lookupService.IsConfigured)
        {
            var reason = !_options.ProjectHoneypot.Enabled
                ? "Skipped: ProjectHoneypot is disabled in configuration"
                : "Skipped: ProjectHoneypot AccessKey not configured (get free key at projecthoneypot.org)";
            return Single(DetectionContribution.Info(Name, Category, reason));
        }

        var clientIp = sink.ReadHint(SignalKeys.ClientIp);
        if (string.IsNullOrWhiteSpace(clientIp)) return None();

        if (sink.ReadBoolHint(SignalKeys.IpIsLocal))
            return Single(DetectionContribution.Info(Name, Category, "Skipped: localhost/private IP"));

        if (!IPAddress.TryParse(clientIp, out var ipAddress)) return None();
        if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            _logger.LogDebug("Skipping Project Honeypot lookup for IPv6 address: {Ip}",
                ProjectHoneypotLookupService.MaskIp(clientIp));
            return None();
        }

        try
        {
            var result = await _lookupService.LookupIpAsync(clientIp, ct).ConfigureAwait(false);

            if (result is null || !result.IsListed)
            {
                sink.Raise($"{SignalKeys.HoneypotChecked}:true", sessionId);
                sink.Raise($"{SignalKeys.HoneypotListed}:false", sessionId);
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = -0.05,
                    Weight = 0.8,
                    Reason = "IP not listed in Project Honeypot database"
                });
            }

            EmitHoneypotSignals(sink, sessionId, result, testMode: false);

            if (result.VisitorType == HoneypotVisitorType.SearchEngine)
            {
                _logger.LogDebug("IP {Ip} identified as search engine by Project Honeypot",
                    ProjectHoneypotLookupService.MaskIp(clientIp));
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = -0.4,
                    Weight = 1.5,
                    Reason = "IP classified as search engine by Project Honeypot HTTP:BL"
                });
            }

            var confidence = CalculateConfidence(result);
            var botType = DetermineBotType(result);
            var reason = BuildReason(result);

            _logger.LogWarning(
                "IP {Ip} listed in Project Honeypot: Type={Type}, ThreatScore={Score}, DaysAgo={Days}",
                ProjectHoneypotLookupService.MaskIp(clientIp), result.VisitorType, result.ThreatScore,
                result.DaysSinceLastActivity);

            return BuildContribution(result, confidence, botType, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Project Honeypot lookup failed for IP: {Ip}",
                ProjectHoneypotLookupService.MaskIp(clientIp));
            return None();
        }
    }

    private void EmitHoneypotSignals(SignalSink sink, string sessionId, HoneypotResult result, bool testMode)
    {
        sink.Raise($"{SignalKeys.HoneypotChecked}:true", sessionId);
        sink.Raise($"{SignalKeys.HoneypotListed}:true", sessionId);
        sink.Raise($"{SignalKeys.HoneypotThreatScore}:{result.ThreatScore}", sessionId);
        sink.Raise($"{SignalKeys.HoneypotVisitorType}:{result.VisitorType}", sessionId);
        sink.Raise($"{SignalKeys.HoneypotDaysSinceLastActivity}:{result.DaysSinceLastActivity}", sessionId);
        if (testMode) sink.Raise("honeypot.test_mode:true", sessionId);
    }

    private IReadOnlyList<DetectionContribution> BuildContribution(
        HoneypotResult result, double confidence, BotType botType, string reason)
    {
        if (result.ThreatScore >= _options.ProjectHoneypot.HighThreatThreshold)
        {
            return Single(DetectionContribution.VerifiedBot(
                    Name, reason,
                    botType: botType.ToString(),
                    botName: $"Honeypot Threat ({result.VisitorType})")
                with
                {
                    ConfidenceDelta = confidence,
                    Weight = 1.8
                });
        }

        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = confidence,
            Weight = 1.5,
            Reason = reason,
            BotType = botType.ToString()
        });
    }

    private IReadOnlyList<DetectionContribution>? CheckTestModeSimulation(
        SignalSink sink, string sessionId, string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;

        const string prefix = "<test-honeypot:";
        var startIdx = userAgent.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return null;

        var typeStart = startIdx + prefix.Length;
        var endIdx = userAgent.IndexOf('>', typeStart);
        if (endIdx < 0) return null;

        var testType = userAgent.Substring(typeStart, endIdx - typeStart).ToLowerInvariant();
        _logger.LogInformation("Project Honeypot test mode simulation: {Type}", testType);

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

        if (result is null)
        {
            _logger.LogWarning("Unknown honeypot test type: {Type}", testType);
            return null;
        }

        EmitHoneypotSignals(sink, sessionId, result, testMode: true);
        var confidence = CalculateConfidence(result);
        var botType = DetermineBotType(result);
        var reason = $"[TEST MODE] {BuildReason(result)}";
        return BuildContribution(result, confidence, botType, reason);
    }

    private static double CalculateConfidence(HoneypotResult result)
    {
        var baseConfidence = result.ThreatScore switch
        {
            >= 100 => 0.95,
            >= 50 => 0.85,
            >= 25 => 0.70,
            >= 10 => 0.55,
            >= 5 => 0.40,
            _ => 0.30
        };

        var ageFactor = result.DaysSinceLastActivity switch
        {
            0 => 1.0,
            <= 7 => 0.95,
            <= 30 => 0.85,
            <= 90 => 0.70,
            <= 180 => 0.50,
            _ => 0.30
        };

        var typeFactor = 1.0;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer)) typeFactor *= 1.1;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester)) typeFactor *= 1.15;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious)) typeFactor *= 1.05;

        return Math.Min(baseConfidence * ageFactor * typeFactor, 0.99);
    }

    private static BotType DetermineBotType(HoneypotResult result)
    {
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer)) return BotType.MaliciousBot;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester)) return BotType.Scraper;
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious)) return BotType.Unknown;
        return BotType.Unknown;
    }

    private static string BuildReason(HoneypotResult result)
    {
        var types = new List<string>();
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Suspicious)) types.Add("Suspicious");
        if (result.VisitorType.HasFlag(HoneypotVisitorType.Harvester)) types.Add("Harvester");
        if (result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer)) types.Add("CommentSpammer");
        var typeStr = types.Count > 0 ? string.Join(", ", types) : "Unknown";
        return $"IP listed in Project Honeypot: {typeStr} (Threat: {result.ThreatScore}, Last seen: {result.DaysSinceLastActivity} days ago)";
    }
}
