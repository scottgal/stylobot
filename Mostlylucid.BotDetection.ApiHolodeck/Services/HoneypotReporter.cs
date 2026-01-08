using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Background service that reports detected bots to Project Honeypot.
///     This contributes data back to the community, helping other sites block malicious IPs.
/// </summary>
/// <remarks>
///     <para>
///         Project Honeypot accepts reports via their http:BL API. We queue up reports
///         and submit them in batches to avoid overwhelming their service.
///     </para>
///     <para>
///         Note: Project Honeypot's submission API is different from their lookup API.
///         You need to host a honeypot script on your site to contribute data.
///         This service prepares data for submission when you have such a setup.
///     </para>
/// </remarks>
public class HoneypotReporter : BackgroundService
{
    // Queue of IPs to report
    private static readonly ConcurrentQueue<ReportEntry> _reportQueue = new();

    // Rate limiting
    private static int _reportsThisHour;
    private static DateTime _hourStart = DateTime.UtcNow;
    private readonly ILearningEventBus? _learningEventBus;
    private readonly ILogger<HoneypotReporter> _logger;
    private readonly HolodeckOptions _options;

    public HoneypotReporter(
        ILogger<HoneypotReporter> logger,
        IOptions<HolodeckOptions> options,
        ILearningEventBus? learningEventBus = null)
    {
        _logger = logger;
        _options = options.Value;
        _learningEventBus = learningEventBus;
    }

    /// <summary>
    ///     Get the current queue size.
    /// </summary>
    public static int QueueSize => _reportQueue.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ReportToProjectHoneypot)
        {
            _logger.LogInformation("Project Honeypot reporting is disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ProjectHoneypotAccessKey))
        {
            _logger.LogWarning("Project Honeypot reporting enabled but no access key configured");
            return;
        }

        _logger.LogInformation(
            "Honeypot reporter started. Reporting threshold: {Threshold}, Max reports/hour: {Max}",
            _options.MinRiskToReport,
            _options.MaxReportsPerHour);

        // Process learning events and queue for reporting
        if (_learningEventBus != null) _ = ProcessLearningEventsAsync(stoppingToken);

        // Process queue periodically
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessReportQueueAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task ProcessLearningEventsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var evt in _learningEventBus!.Reader.ReadAllAsync(stoppingToken))
                if (evt.Type == LearningEventType.HighConfidenceDetection ||
                    evt.Type == LearningEventType.FullDetection)
                    ProcessLearningEvent(evt);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private void ProcessLearningEvent(LearningEvent evt)
    {
        // Check if this detection meets our reporting criteria
        var confidence = evt.Confidence ?? 0;
        if (confidence < _options.MinRiskToReport)
            return;

        // Try to get IP from metadata
        var ip = evt.Metadata?.TryGetValue(SignalKeys.ClientIp, out var ipObj) == true
            ? ipObj?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(ip))
            return;

        // Skip local IPs
        if (IPAddress.TryParse(ip, out var ipAddr) && IsLocalIp(ipAddr))
            return;

        // Determine visitor type from metadata
        var visitorType = DetermineVisitorType(evt);
        if (!_options.ReportVisitorTypes.Contains(visitorType))
            return;

        // Get request path from metadata
        var requestPath = evt.Metadata?.TryGetValue("RequestPath", out var pathObj) == true
            ? pathObj?.ToString()
            : "/";

        // Get user agent from metadata
        var userAgent = evt.Metadata?.TryGetValue(SignalKeys.UserAgent, out var uaObj) == true
            ? uaObj?.ToString()
            : null;

        // Queue the report
        var entry = new ReportEntry
        {
            IpAddress = ip,
            VisitorType = visitorType,
            RiskScore = confidence,
            Timestamp = DateTime.UtcNow,
            RequestPath = requestPath ?? "/",
            UserAgent = userAgent
        };

        _reportQueue.Enqueue(entry);

        _logger.LogDebug(
            "Queued honeypot report: IP={Ip}, Type={Type}, Risk={Risk:F2}",
            ip, visitorType, confidence);
    }

    private async Task ProcessReportQueueAsync(CancellationToken cancellationToken)
    {
        // Reset hourly counter if needed
        if (DateTime.UtcNow - _hourStart > TimeSpan.FromHours(1))
        {
            _reportsThisHour = 0;
            _hourStart = DateTime.UtcNow;
        }

        var batch = new List<ReportEntry>();
        var maxBatchSize = Math.Min(10, _options.MaxReportsPerHour - _reportsThisHour);

        while (batch.Count < maxBatchSize && _reportQueue.TryDequeue(out var entry)) batch.Add(entry);

        if (batch.Count == 0)
            return;

        _logger.LogInformation(
            "Processing {Count} honeypot reports ({Remaining} remaining in queue)",
            batch.Count, _reportQueue.Count);

        foreach (var entry in batch)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await SubmitReportAsync(entry, cancellationToken);
                _reportsThisHour++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit honeypot report for {Ip}", entry.IpAddress);
            }
        }
    }

    private async Task SubmitReportAsync(ReportEntry entry, CancellationToken cancellationToken)
    {
        // Project Honeypot doesn't have a direct HTTP API for submissions.
        // Instead, you host their honeypot script and it collects data automatically.
        //
        // However, we can log the data for manual submission or integration with
        // other threat intelligence platforms.
        //
        // For automated submission, you would need to:
        // 1. Host a Project Honeypot script on your site
        // 2. The script collects visitor data
        // 3. Project Honeypot fetches the data from your script
        //
        // What we CAN do is format the data for:
        // - Local threat intelligence database
        // - AbuseIPDB submission
        // - Other threat sharing platforms

        _logger.LogInformation(
            "Honeypot report: IP={Ip}, Type={Type}, Risk={Risk:F2}, Path={Path}, UA={UA}",
            entry.IpAddress,
            entry.VisitorType,
            entry.RiskScore,
            entry.RequestPath,
            entry.UserAgent?.Substring(0, Math.Min(50, entry.UserAgent?.Length ?? 0)));

        // Store in local database for trend analysis
        // This could be extended to submit to other services

        // Placeholder for future: AbuseIPDB submission
        // await SubmitToAbuseIpDbAsync(entry, cancellationToken);

        // Placeholder for future: Custom threat intelligence webhook
        // await SubmitToThreatIntelWebhookAsync(entry, cancellationToken);

        await Task.CompletedTask;
    }

    private static ReportableVisitorType DetermineVisitorType(LearningEvent evt)
    {
        // Check metadata for type hints
        var metadata = evt.Metadata;
        if (metadata == null)
            return ReportableVisitorType.Suspicious;

        // Check for honeypot trigger (harvester behavior)
        if (metadata.Keys.Any(k => k.Contains("Honeypot", StringComparison.OrdinalIgnoreCase)))
            return ReportableVisitorType.Harvester;

        // Check for spam indicators
        if (metadata.Keys.Any(k => k.Contains("Spam", StringComparison.OrdinalIgnoreCase)))
            return ReportableVisitorType.CommentSpammer;

        // Check bot name from pattern
        var pattern = evt.Pattern;
        if (!string.IsNullOrEmpty(pattern))
            if (pattern.Contains("scraper", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("harvester", StringComparison.OrdinalIgnoreCase))
                return ReportableVisitorType.Harvester;

        return ReportableVisitorType.Suspicious;
    }

    private static bool IsLocalIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return false; // IPv6 handling could be added

        return bytes[0] switch
        {
            10 => true, // 10.0.0.0/8
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true, // 172.16.0.0/12
            192 when bytes[1] == 168 => true, // 192.168.0.0/16
            _ => false
        };
    }

    /// <summary>
    ///     Manually queue an IP for reporting (for testing or manual flagging).
    /// </summary>
    public static void QueueReport(string ipAddress, ReportableVisitorType type, double riskScore)
    {
        _reportQueue.Enqueue(new ReportEntry
        {
            IpAddress = ipAddress,
            VisitorType = type,
            RiskScore = riskScore,
            Timestamp = DateTime.UtcNow
        });
    }

    private class ReportEntry
    {
        public required string IpAddress { get; init; }
        public ReportableVisitorType VisitorType { get; init; }
        public double RiskScore { get; init; }
        public DateTime Timestamp { get; init; }
        public string? RequestPath { get; init; }
        public string? UserAgent { get; init; }
    }
}