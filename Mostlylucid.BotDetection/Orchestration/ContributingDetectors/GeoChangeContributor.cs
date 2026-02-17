using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects geographic drift for the same visitor signature.
///     Bots rotating proxies often stay in the same country or exhibit
///     unnaturally rapid country changes. Real users rarely change country.
///
///     Also feeds CountryReputationTracker with every detection result
///     so countries with high bot rates get decaying reputation scores.
///
///     Runs in Wave 1 (after GeoContributor emits geo.country_code).
///
///     Configuration loaded from: geochange.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:GeoChange:*
/// </summary>
public class GeoChangeContributor : ConfiguredContributorBase
{
    private readonly CountryReputationTracker _countryTracker;
    private readonly ILogger<GeoChangeContributor> _logger;

    // Per-signature country history: signature → (lastCountryCode, countryChanges, firstSeen)
    private static readonly ConcurrentDictionary<string, GeoHistory> SignatureGeoHistory = new();

    public GeoChangeContributor(
        ILogger<GeoChangeContributor> logger,
        IDetectorConfigProvider configProvider,
        CountryReputationTracker countryTracker)
        : base(configProvider)
    {
        _logger = logger;
        _countryTracker = countryTracker;
    }

    public override string Name => "GeoChange";
    public override int Priority => Manifest?.Priority ?? 16;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.GeoCountryCode)
    ];

    // Config-driven parameters
    private double DriftConfidence => GetParam("drift_confidence", 0.6);
    private double DriftWeight => GetParam("drift_weight", 1.5);
    private double RapidDriftConfidence => GetParam("rapid_drift_confidence", 0.8);
    private double RapidDriftWeight => GetParam("rapid_drift_weight", 1.8);
    private double CountryReputationConfidence => GetParam("country_reputation_confidence", 0.3);
    private double CountryReputationWeight => GetParam("country_reputation_weight", 1.3);
    private double HighBotRateThreshold => GetParam("high_bot_rate_threshold", 0.6);
    private double VeryHighBotRateThreshold => GetParam("very_high_bot_rate_threshold", 0.85);
    private int RapidDriftThreshold => GetParam("rapid_drift_threshold", 3);
    private int RapidDriftWindowMinutes => GetParam("rapid_drift_window_minutes", 60);
    private int MaxHistoryEntries => GetParam("max_history_entries", 10000);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var countryCode = state.GetSignal<string>(SignalKeys.GeoCountryCode);
            if (string.IsNullOrEmpty(countryCode))
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

            // Use waveform signature as the stable visitor identity for drift tracking
            var signature = state.GetSignal<string>(SignalKeys.WaveformSignature);

            // Always feed country reputation tracker
            var countryName = state.GetSignal<string>("geo.country_name") ?? countryCode;
            var currentBotProb = state.CurrentRiskScore;
            var isBot = currentBotProb >= 0.5;
            _countryTracker.RecordDetection(countryCode, countryName, isBot, currentBotProb);

            // Check country reputation
            var botRate = _countryTracker.GetCountryBotRate(countryCode);
            if (botRate > 0)
            {
                state.WriteSignal("geo.change.country_bot_rate", botRate);
                state.WriteSignal(SignalKeys.GeoCountryBotRate, botRate);

                if (botRate >= VeryHighBotRateThreshold)
                {
                    contributions.Add(BotContribution(
                        "GeoReputation",
                        $"Country {countryCode} has very high bot rate ({botRate:P0})",
                        confidenceOverride: CountryReputationConfidence * 1.5,
                        weightMultiplier: CountryReputationWeight * 1.3));
                    state.WriteSignal("geo.change.reputation_level", "very_high");
                }
                else if (botRate >= HighBotRateThreshold)
                {
                    contributions.Add(BotContribution(
                        "GeoReputation",
                        $"Country {countryCode} has elevated bot rate ({botRate:P0})",
                        confidenceOverride: CountryReputationConfidence,
                        weightMultiplier: CountryReputationWeight));
                    state.WriteSignal("geo.change.reputation_level", "high");
                }
            }

            // Drift detection requires a signature to track
            if (!string.IsNullOrEmpty(signature))
            {
                var now = DateTimeOffset.UtcNow;

                // Prune old entries if we're getting too large
                if (SignatureGeoHistory.Count > MaxHistoryEntries)
                    PruneHistory();

                var history = SignatureGeoHistory.AddOrUpdate(
                    signature,
                    _ => new GeoHistory
                    {
                        LastCountryCode = countryCode,
                        CountryChanges = 0,
                        FirstSeen = now,
                        LastSeen = now,
                        DistinctCountries = [countryCode],
                        RecentChangeTimes = []
                    },
                    (_, existing) =>
                    {
                        if (!string.Equals(existing.LastCountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                        {
                            // Country changed!
                            var recentChanges = existing.RecentChangeTimes
                                .Where(t => (now - t).TotalMinutes <= RapidDriftWindowMinutes)
                                .Append(now)
                                .ToList();

                            var distinct = existing.DistinctCountries.ToHashSet(StringComparer.OrdinalIgnoreCase);
                            distinct.Add(countryCode);

                            return existing with
                            {
                                LastCountryCode = countryCode,
                                CountryChanges = existing.CountryChanges + 1,
                                LastSeen = now,
                                DistinctCountries = distinct,
                                RecentChangeTimes = recentChanges
                            };
                        }

                        return existing with { LastSeen = now };
                    });

                state.WriteSignal("geo.change.checked", true);
                state.WriteSignal("geo.change.distinct_countries", history.DistinctCountries.Count);
                state.WriteSignal("geo.change.total_changes", history.CountryChanges);

                if (history.CountryChanges > 0)
                {
                    // Any country change is noteworthy
                    state.WriteSignal("geo.change.drift_detected", true);
                    state.WriteSignal("geo.change.previous_country",
                        history.DistinctCountries
                            .FirstOrDefault(c => !string.Equals(c, countryCode, StringComparison.OrdinalIgnoreCase)) ?? "");

                    var recentChangeCount = history.RecentChangeTimes.Count;

                    if (recentChangeCount >= RapidDriftThreshold)
                    {
                        // Rapid country switching — strong bot signal (proxy rotation)
                        contributions.Add(BotContribution(
                            "GeoDrift",
                            $"Rapid country switching: {recentChangeCount} changes in {RapidDriftWindowMinutes}min across {history.DistinctCountries.Count} countries",
                            confidenceOverride: RapidDriftConfidence,
                            weightMultiplier: RapidDriftWeight));
                        state.WriteSignal("geo.change.rapid_drift", true);
                    }
                    else if (history.DistinctCountries.Count >= 2)
                    {
                        // Regular drift — moderate signal
                        contributions.Add(BotContribution(
                            "GeoDrift",
                            $"Country changed from {history.DistinctCountries.First()} → {countryCode} ({history.CountryChanges} total changes)",
                            confidenceOverride: DriftConfidence,
                            weightMultiplier: DriftWeight));
                    }
                }
            }

            // Emit neutral contribution if no bot/human signal but we checked
            if (contributions.Count == 0)
            {
                contributions.Add(NeutralContribution("GeoChange", "Country geo check completed"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GeoChange] Error during geo change detection");
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void PruneHistory()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var stale = SignatureGeoHistory
            .Where(kvp => kvp.Value.LastSeen < cutoff)
            .Select(kvp => kvp.Key)
            .Take(MaxHistoryEntries / 4)
            .ToList();

        foreach (var key in stale)
            SignatureGeoHistory.TryRemove(key, out _);
    }

    private record GeoHistory
    {
        public required string LastCountryCode { get; init; }
        public int CountryChanges { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public required HashSet<string> DistinctCountries { get; init; }
        public required List<DateTimeOffset> RecentChangeTimes { get; init; }
    }
}
