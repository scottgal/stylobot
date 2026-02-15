using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Historical reputation contributor using TimescaleDB 90-day history.
///     Priority 15: after FastPath (3) but before ReputationBias (45).
///     Gracefully no-ops when TimescaleDB is not configured (nullable injection).
///
///     Configuration loaded from: timescale.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:TimescaleReputationContributor:*
/// </summary>
public class TimescaleReputationContributor : ConfiguredContributorBase
{
    private readonly ILogger<TimescaleReputationContributor> _logger;
    private readonly PiiHasher _piiHasher;
    private readonly ITimescaleReputationProvider? _reputationProvider;

    public TimescaleReputationContributor(
        ILogger<TimescaleReputationContributor> logger,
        IDetectorConfigProvider configProvider,
        PiiHasher piiHasher,
        ITimescaleReputationProvider? reputationProvider = null)
        : base(configProvider)
    {
        _logger = logger;
        _piiHasher = piiHasher;
        _reputationProvider = reputationProvider;
    }

    public override string Name => "TimescaleReputation";
    public override int Priority => Manifest?.Priority ?? 15;

    // No triggers - runs in Wave 0 alongside fast-path
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters
    private double HighBotRatioThreshold => GetParam("high_bot_ratio", 0.8);
    private double LowBotRatioThreshold => GetParam("low_bot_ratio", 0.2);
    private int MinHitsForConclusive => GetParam("min_hits_conclusive", 3);
    private int HighVelocityPerHour => GetParam("high_velocity_per_hour", 50);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Graceful no-op if TimescaleDB is not configured
        if (_reputationProvider == null)
        {
            return new[]
            {
                DetectionContribution.Info(Name, "Reputation",
                    "TimescaleDB reputation not configured")
            };
        }

        // Compute signature directly from request (same HMAC-SHA256 as BlackboardOrchestrator)
        var clientIp = state.HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = state.HttpContext.Request.Headers.UserAgent.ToString();

        if (string.IsNullOrEmpty(clientIp) && string.IsNullOrEmpty(userAgent))
        {
            return new[]
            {
                DetectionContribution.Info(Name, "Reputation", "No IP/UA available for reputation lookup")
            };
        }

        var primarySignature = _piiHasher.ComputeSignature(clientIp, userAgent);

        try
        {
            var reputation = await _reputationProvider.GetReputationAsync(primarySignature, cancellationToken);

            if (reputation == null)
            {
                // New signature - no historical data (zero-weight contribution so it appears in detector list)
                var newSignals = ImmutableDictionary<string, object>.Empty
                    .Add("ts.is_new", true)
                    .Add("ts.hit_count", 0)
                    .Add("ts.days_active", 0);

                return new[]
                {
                    new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "Reputation",
                        Reason = "New visitor - no historical reputation data",
                        Weight = 0,
                        ConfidenceDelta = 0,
                        Signals = newSignals
                    }
                };
            }

            // Build signals from historical data
            var signals = ImmutableDictionary<string, object>.Empty
                .Add("ts.bot_ratio", reputation.BotRatio)
                .Add("ts.hit_count", reputation.TotalHitCount)
                .Add("ts.days_active", reputation.DaysActive)
                .Add("ts.velocity", reputation.RecentHourHitCount)
                .Add("ts.is_new", false)
                .Add("ts.is_conclusive", reputation.IsConclusive)
                .Add("ts.avg_bot_prob", reputation.AverageBotProbability);

            // High bot ratio with enough hits → bot contribution
            if (reputation.BotRatio >= HighBotRatioThreshold && reputation.TotalHitCount >= MinHitsForConclusive)
            {
                _logger.LogDebug("TimescaleReputation: high bot ratio {Ratio:F2} for {Sig}",
                    reputation.BotRatio, primarySignature[..Math.Min(8, primarySignature.Length)]);

                return new[]
                {
                    BotContribution("Reputation", $"Historical reputation: {reputation.BotRatio:P0} bot ratio over {reputation.TotalHitCount} requests ({reputation.DaysActive} days)") with
                    {
                        Signals = signals
                    }
                };
            }

            // Low bot ratio with enough hits → human contribution
            if (reputation.BotRatio <= LowBotRatioThreshold && reputation.TotalHitCount >= MinHitsForConclusive)
            {
                return new[]
                {
                    HumanContribution("Reputation", $"Historical reputation: {1 - reputation.BotRatio:P0} human ratio over {reputation.TotalHitCount} requests ({reputation.DaysActive} days)") with
                    {
                        Signals = signals
                    }
                };
            }

            // High velocity (burst) → bot indication
            if (reputation.RecentHourHitCount > HighVelocityPerHour)
            {
                return new[]
                {
                    BotContribution("Reputation", $"High request velocity: {reputation.RecentHourHitCount} requests in last hour") with
                    {
                        Signals = signals
                    }
                };
            }

            // Inconclusive - zero-weight contribution with signals for other detectors
            return new[]
            {
                new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Reputation",
                    Reason = $"Seen {reputation.TotalHitCount} times over {reputation.DaysActive} days, {reputation.BotRatio:P0} classified as bot",
                    Weight = 0,
                    ConfidenceDelta = 0,
                    Signals = signals
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TimescaleReputation lookup failed for {Sig}", primarySignature);
            return new[]
            {
                DetectionContribution.Info(Name, "Reputation", "TimescaleDB lookup failed")
            };
        }
    }
}
