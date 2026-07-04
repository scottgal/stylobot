using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects geographic drift for the
///     same visitor signature. Bots rotating proxies often stay in the same
///     country or exhibit unnaturally rapid country changes. Real users rarely
///     change country.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>GeoChangeContributor</c>. Also feeds
///         <see cref="CountryReputationTracker"/> with every detection result
///         so countries with high bot rates get decaying reputation scores.
///     </para>
///     <para>
///         Priority 16, RequiredSignals [<see cref="SignalKeys.GeoCountryCode"/>].
///     </para>
///     <para>
///         Per-signature history stored on an instance <see cref="ConcurrentDictionary{TKey, TValue}"/>
///         bounded at <c>max_history_entries</c> (default 10K) with periodic
///         pruning of stale entries. Instance-scoped instead of the legacy
///         static field -- the atom is a singleton so this is functionally
///         equivalent while remaining testable in isolation.
///     </para>
/// </remarks>
public sealed class GeoChangeAtom : DetectorAtomBase
{
    private readonly CountryReputationTracker _countryTracker;
    private readonly ILogger<GeoChangeAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    private readonly ConcurrentDictionary<string, GeoHistory> _signatureGeoHistory = new();

    public GeoChangeAtom(
        ILogger<GeoChangeAtom> logger,
        IDetectorConfigProvider configProvider,
        CountryReputationTracker countryTracker)
        : base(name: "GeoChange", category: "GeoChange")
    {
        _logger = logger;
        _countryTracker = countryTracker;
        _configProvider = configProvider;
    }

    public override int Priority => 16;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.GeoCountryCode };

    private double DriftConfidence => _configProvider.GetParameter(Name, "drift_confidence", 0.6);
    private double DriftWeight => _configProvider.GetParameter(Name, "drift_weight", 1.5);
    private double RapidDriftConfidence => _configProvider.GetParameter(Name, "rapid_drift_confidence", 0.8);
    private double RapidDriftWeight => _configProvider.GetParameter(Name, "rapid_drift_weight", 1.8);
    private double CountryReputationConfidence => _configProvider.GetParameter(Name, "country_reputation_confidence", 0.3);
    private double CountryReputationWeight => _configProvider.GetParameter(Name, "country_reputation_weight", 1.3);
    private double HighBotRateThreshold => _configProvider.GetParameter(Name, "high_bot_rate_threshold", 0.6);
    private double VeryHighBotRateThreshold => _configProvider.GetParameter(Name, "very_high_bot_rate_threshold", 0.85);
    private int RapidDriftThreshold => _configProvider.GetParameter(Name, "rapid_drift_threshold", 3);
    private int RapidDriftWindowMinutes => _configProvider.GetParameter(Name, "rapid_drift_window_minutes", 60);
    private int MaxHistoryEntries => _configProvider.GetParameter(Name, "max_history_entries", 10000);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var countryCode = sink.ReadHint(SignalKeys.GeoCountryCode);
            if (string.IsNullOrEmpty(countryCode))
                return Task.FromResult(None());

            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            var countryName = sink.ReadHint("geo.country_name") ?? countryCode;

            // In-wave running risk score (raised by the orchestrator between
            // waves as classifiers accumulate). Falls back to neutral (0.5) if
            // no upstream atom has raised it yet.
            var currentBotProb = sink.ReadDoubleHint("risk.current_score", 0.5);
            var isBot = currentBotProb >= 0.5;
            _countryTracker.RecordDetection(countryCode, countryName, isBot, currentBotProb);

            // Country reputation check
            var botRate = _countryTracker.GetCountryBotRate(countryCode);
            if (botRate > 0)
            {
                sink.Raise($"geo.change.country_bot_rate:{botRate.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise($"{SignalKeys.GeoCountryBotRate}:{botRate.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);

                if (botRate >= VeryHighBotRateThreshold)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "GeoReputation",
                        ConfidenceDelta = CountryReputationConfidence * 1.5,
                        Weight = CountryReputationWeight * 1.3,
                        Reason = $"Country {countryCode} has very high bot rate ({botRate:P0})",
                        BotType = BotType.Unknown.ToString()
                    });
                    sink.Raise("geo.change.reputation_level:very_high", sessionId);
                }
                else if (botRate >= HighBotRateThreshold)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "GeoReputation",
                        ConfidenceDelta = CountryReputationConfidence,
                        Weight = CountryReputationWeight,
                        Reason = $"Country {countryCode} has elevated bot rate ({botRate:P0})",
                        BotType = BotType.Unknown.ToString()
                    });
                    sink.Raise("geo.change.reputation_level:high", sessionId);
                }
            }

            // Drift detection requires a signature
            if (!string.IsNullOrEmpty(signature))
            {
                var now = DateTimeOffset.UtcNow;

                if (_signatureGeoHistory.Count > MaxHistoryEntries)
                    PruneHistory();

                var history = _signatureGeoHistory.AddOrUpdate(
                    signature,
                    _ => new GeoHistory
                    {
                        LastCountryCode = countryCode,
                        CountryChanges = 0,
                        FirstSeen = now,
                        LastSeen = now,
                        DistinctCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { countryCode },
                        RecentChangeTimes = new List<DateTimeOffset>()
                    },
                    (_, existing) =>
                    {
                        if (!string.Equals(existing.LastCountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                        {
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

                sink.Raise("geo.change.checked", sessionId);
                sink.Raise($"geo.change.distinct_countries:{history.DistinctCountries.Count}", sessionId);
                sink.Raise($"geo.change.total_changes:{history.CountryChanges}", sessionId);

                if (history.CountryChanges > 0)
                {
                    sink.Raise("geo.change.drift_detected", sessionId);
                    var previousCountry = history.DistinctCountries
                        .FirstOrDefault(c => !string.Equals(c, countryCode, StringComparison.OrdinalIgnoreCase)) ?? "";
                    sink.Raise($"geo.change.previous_country:{previousCountry}", sessionId);

                    var recentChangeCount = history.RecentChangeTimes.Count;

                    if (recentChangeCount >= RapidDriftThreshold)
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "GeoDrift",
                            ConfidenceDelta = RapidDriftConfidence,
                            Weight = RapidDriftWeight,
                            Reason = $"Rapid country switching: {recentChangeCount} changes in {RapidDriftWindowMinutes}min across {history.DistinctCountries.Count} countries",
                            BotType = BotType.Unknown.ToString()
                        });
                        sink.Raise("geo.change.rapid_drift", sessionId);
                    }
                    else if (history.DistinctCountries.Count >= 2)
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "GeoDrift",
                            ConfidenceDelta = DriftConfidence,
                            Weight = DriftWeight,
                            Reason = $"Country changed from {history.DistinctCountries.First()} → {countryCode} ({history.CountryChanges} total changes)",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                }
            }

            if (contributions.Count == 0)
                contributions.Add(DetectionContribution.Info(Name, Category, "Country geo check completed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GeoChange] Error during geo change detection");
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void PruneHistory()
    {
        // Phase 1: Remove expired entries (>24h since last seen)
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var kvp in _signatureGeoHistory)
            if (kvp.Value.LastSeen < cutoff)
                _signatureGeoHistory.TryRemove(kvp.Key, out _);

        // Phase 2: If still over limit, evict oldest by LastSeen (LRU)
        if (_signatureGeoHistory.Count > MaxHistoryEntries)
        {
            var toEvict = _signatureGeoHistory
                .OrderBy(kvp => kvp.Value.LastSeen)
                .Take(_signatureGeoHistory.Count - (MaxHistoryEntries * 3 / 4))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toEvict)
                _signatureGeoHistory.TryRemove(key, out _);
        }
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
