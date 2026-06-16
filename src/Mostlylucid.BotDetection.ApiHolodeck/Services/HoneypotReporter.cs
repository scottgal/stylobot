using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Reports detected bots to Project Honeypot.
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
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B pilot).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that ran two
///         parallel loops: an <c>await foreach</c> on
///         <see cref="ILearningEventBus.Reader"/> for fan-in plus a 1-minute
///         <c>Task.Delay</c> loop that drained an in-memory
///         <see cref="ConcurrentQueue{ReportEntry}"/> and POSTed batches. Now subscribes
///         to <see cref="TickCadence.Tick1m"/>; each tick first drains the learning-event
///         channel non-blocking via <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>
///         and then runs the existing batched report-queue submission. Trade-off: a
///         learning event may sit in the channel for up to one tick interval (~60s) before
///         it is converted to a queued report. That is well within the original 60s
///         report-drain cadence and well below the
///         <see cref="HolodeckOptions.MaxReportsPerHour"/> throttle (100 reports/hour
///         default × batch cap 10/tick × 60 ticks/hour leaves plenty of slack).
///     </para>
///     <para>
///         The in-memory <see cref="ConcurrentQueue{T}"/> stays per
///         <c>feedback_no_inmemory_stores</c> -- Project Honeypot reporting is explicitly
///         "best effort", queued reports lost on restart are acceptable. Persistence
///         would be the right move if/when this service grows beyond best-effort.
///     </para>
/// </remarks>
public sealed class HoneypotReporter : IDisposable
{
    // Queue of IPs to report. Per-instance (not static) so a singleton DI lifetime is
    // the source of truth; the previous static field was incidentally a single global
    // because AddHostedService<T> registered the service as a singleton anyway.
    private readonly ConcurrentQueue<ReportEntry> _reportQueue = new();

    // Rate limiting state -- per-instance for the same reason as _reportQueue.
    private int _reportsThisHour;
    private DateTime _hourStart = DateTime.UtcNow;

    private readonly ILearningEventBus? _learningEventBus;
    private readonly ILogger<HoneypotReporter> _logger;
    private readonly HolodeckOptions _options;
    private readonly IDisposable? _subscription;

    private bool _startupLogged;
    private bool _disabledLogged;
    private int _disposed;

    public HoneypotReporter(
        ILogger<HoneypotReporter> logger,
        IOptions<HolodeckOptions> options,
        ILearningEventBus? learningEventBus = null,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _options = options.Value;
        _learningEventBus = learningEventBus;

        // Optional so existing direct-construction tests keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m,
                "HoneypotReporter",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     Get the current queue size.
    /// </summary>
    public int QueueSize => _reportQueue.Count;

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain any available learning
    ///     events from the bus channel, classify them, enqueue matching detections;
    ///     then process up to <c>min(10, MaxReportsPerHour - reportsThisHour)</c>
    ///     queued reports against the rate-limit budget. Public so tests can drive a
    ///     single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (!_options.ReportToProjectHoneypot)
        {
            if (!_disabledLogged)
            {
                _logger.LogInformation("Project Honeypot reporting is disabled");
                _disabledLogged = true;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ProjectHoneypotAccessKey))
        {
            if (!_disabledLogged)
            {
                _logger.LogWarning("Project Honeypot reporting enabled but no access key configured");
                _disabledLogged = true;
            }
            return;
        }

        if (!_startupLogged)
        {
            _logger.LogInformation(
                "Honeypot reporter started. Reporting threshold: {Threshold}, Max reports/hour: {Max}",
                _options.MinRiskToReport,
                _options.MaxReportsPerHour);
            _startupLogged = true;
        }

        // 1) Drain any pending learning events into the report queue. This replaces
        //    the legacy fire-and-forget `await foreach (... ReadAllAsync(ct))` loop.
        //    TryRead is non-blocking; we read whatever is available right now.
        if (_learningEventBus is not null)
        {
            while (_learningEventBus.Reader.TryRead(out var evt))
            {
                if (ct.IsCancellationRequested) break;
                if (evt.Type == LearningEventType.HighConfidenceDetection ||
                    evt.Type == LearningEventType.FullDetection)
                {
                    ProcessLearningEvent(evt);
                }
            }
        }

        // 2) Process the bounded batch from the report queue (existing logic).
        await ProcessReportQueueAsync(ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
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

        // IPv6 link-local (fe80::/10)
        if (ip.IsIPv6LinkLocal)
            return true;

        // IPv6 site-local (fec0::/10 - deprecated but still used)
        if (ip.IsIPv6SiteLocal)
            return true;

        // IPv6 unique local address (fc00::/7 - ULA, equivalent to RFC 1918)
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        // IPv4-mapped IPv6 (::ffff:10.x.x.x etc.)
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // IPv4 private ranges
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                    // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31,       // 172.16.0.0/12
                192 => bytes[1] == 168,                        // 192.168.0.0/16
                169 => bytes[1] == 254,                        // 169.254.0.0/16 (link-local)
                127 => true,                                   // 127.0.0.0/8
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    ///     Manually queue an IP for reporting (for testing or manual flagging).
    /// </summary>
    public void QueueReport(string ipAddress, ReportableVisitorType type, double riskScore)
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