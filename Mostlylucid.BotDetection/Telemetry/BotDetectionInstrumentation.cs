using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Central recording class called once per request after detection.
///     Reads <see cref="AggregatedEvidence" /> and emits span attributes,
///     span events (scoring journey), and meter recordings.
/// </summary>
public sealed class BotDetectionInstrumentation
{
    private readonly BotDetectionSignalMeter _meter;
    private readonly BotDetectionTelemetryOptions _options;
    private readonly HashSet<string> _allowlist;

    public BotDetectionInstrumentation(
        BotDetectionSignalMeter meter,
        IOptions<BotDetectionTelemetryOptions> options)
    {
        _meter = meter;
        _options = options.Value;
        _allowlist = _options.SignalAllowlist ?? SignalAllowlist.Default;
    }

    /// <summary>
    ///     Record detection results to spans and metrics.
    ///     Called by the middleware after <see cref="AggregatedEvidence" /> is available.
    /// </summary>
    public void Record(Activity? activity, AggregatedEvidence evidence, HttpContext httpContext)
    {
        // Extract country from signals or context
        var country = ExtractCountry(evidence, httpContext);

        // ── Metrics (always, if enabled) ──────────────────────────────
        if (_options.EnableMetrics)
        {
            _meter.RecordDetection(evidence, country);

            if (evidence.Contributions is { Count: > 0 })
                _meter.RecordDetectorRuns(evidence.Contributions);
        }

        if (activity == null)
            return;

        // ── Span attributes (selected signals from allowlist) ─────────
        if (_options.EnableTracing)
        {
            // Core detection attributes
            activity.SetTag("stylobot.risk_band", evidence.RiskBand.ToString());
            activity.SetTag("stylobot.bot_probability", evidence.BotProbability);
            activity.SetTag("stylobot.confidence", evidence.Confidence);
            activity.SetTag("stylobot.is_bot", evidence.BotProbability >= 0.5);
            activity.SetTag("stylobot.early_exit", evidence.EarlyExit);
            activity.SetTag("stylobot.processing_time_ms", evidence.TotalProcessingTimeMs);
            activity.SetTag("stylobot.detector_count", evidence.ContributingDetectors?.Count ?? 0);

            if (evidence.PrimaryBotType.HasValue)
                activity.SetTag("stylobot.bot_type", evidence.PrimaryBotType.Value.ToString());
            if (!string.IsNullOrEmpty(evidence.PrimaryBotName))
                activity.SetTag("stylobot.bot_name", evidence.PrimaryBotName);
            if (!string.IsNullOrEmpty(evidence.PolicyName))
                activity.SetTag("stylobot.policy", evidence.PolicyName);
            if (!string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
                activity.SetTag("stylobot.action_policy", evidence.TriggeredActionPolicyName);
            if (!string.IsNullOrEmpty(country))
                activity.SetTag("stylobot.country", country);

            // Promote allowlisted signals to span attributes
            if (evidence.Signals != null)
            {
                foreach (var (key, value) in evidence.Signals)
                {
                    if (_allowlist.Contains(key) && value != null)
                    {
                        activity.SetTag($"stylobot.signal.{key}", ConvertSignalValue(value));
                    }
                }
            }
        }

        // ── Span events: scoring journey ──────────────────────────────
        if (_options.EnableScoreJourney && evidence.Contributions is { Count: > 0 })
        {
            var cumulativeScore = 0.0;
            foreach (var contribution in evidence.Contributions)
            {
                var effective = contribution.ConfidenceDelta * contribution.Weight;
                cumulativeScore += effective;

                var tags = new ActivityTagsCollection
                {
                    ["detector"] = contribution.DetectorName,
                    ["delta"] = contribution.ConfidenceDelta,
                    ["weight"] = contribution.Weight,
                    ["effective"] = Math.Round(effective, 4),
                    ["cumulative_score"] = Math.Round(cumulativeScore, 4),
                    ["wave"] = contribution.Priority,
                    ["processing_time_ms"] = contribution.ProcessingTimeMs,
                };

                if (!string.IsNullOrEmpty(contribution.Reason))
                    tags["reason"] = contribution.Reason;

                activity.AddEvent(new ActivityEvent("detector.contributed", tags: tags));
            }
        }
    }

    private static string? ExtractCountry(AggregatedEvidence evidence, HttpContext httpContext)
    {
        // Try signals first
        if (evidence.Signals != null &&
            evidence.Signals.TryGetValue("geo.country_code", out var cc) &&
            cc is string countryCode && !string.IsNullOrEmpty(countryCode))
        {
            return countryCode;
        }

        // Fall back to upstream header
        var header = httpContext.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault();
        return !string.IsNullOrEmpty(header) && header != "LOCAL" ? header : null;
    }

    private static object ConvertSignalValue(object value)
    {
        return value switch
        {
            bool b => b,
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            string s => s,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
