using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Tracks bot detection rates per country with time-decayed counters.
///     Uses exponential moving average (EMA) so a country's "bad reputation"
///     naturally fades if no new bot traffic arrives.
/// </summary>
public class CountryReputationTracker
{
    private readonly ConcurrentDictionary<string, CountryReputationEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CountryReputationTracker> _logger;
    private readonly CountryReputationOptions _options;

    public CountryReputationTracker(
        ILogger<CountryReputationTracker> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.CountryReputation;
    }

    /// <summary>
    ///     Record a detection result for a country.
    ///     Called by the orchestrator after every detection that has a geo.country_code signal.
    /// </summary>
    public void RecordDetection(string countryCode, string countryName, bool isBot, double botProbability)
    {
        if (string.IsNullOrEmpty(countryCode))
            return;

        var now = DateTimeOffset.UtcNow;

        _entries.AddOrUpdate(
            countryCode,
            _ => new CountryReputationEntry
            {
                CountryCode = countryCode,
                CountryName = countryName,
                DecayedBotCount = isBot ? 1.0 : 0.0,
                DecayedTotalCount = 1.0,
                RawBotCount = isBot ? 1 : 0,
                RawTotalCount = 1,
                FirstSeen = now,
                LastSeen = now,
                LastDecayTime = now
            },
            (_, existing) =>
            {
                // Apply time decay to existing counters before adding new observation
                var dt = (now - existing.LastDecayTime).TotalHours;
                var decayFactor = Math.Exp(-dt / _options.DecayTauHours);

                var decayedBot = existing.DecayedBotCount * decayFactor + (isBot ? 1.0 : 0.0);
                var decayedTotal = existing.DecayedTotalCount * decayFactor + 1.0;

                return existing with
                {
                    CountryName = !string.IsNullOrEmpty(countryName) ? countryName : existing.CountryName,
                    DecayedBotCount = decayedBot,
                    DecayedTotalCount = decayedTotal,
                    RawBotCount = existing.RawBotCount + (isBot ? 1 : 0),
                    RawTotalCount = existing.RawTotalCount + 1,
                    LastSeen = now,
                    LastDecayTime = now
                };
            });
    }

    /// <summary>
    ///     Get the current decayed bot rate for a specific country.
    ///     Returns 0 if country has insufficient data.
    /// </summary>
    public double GetCountryBotRate(string countryCode)
    {
        if (!_entries.TryGetValue(countryCode, out var entry))
            return 0.0;

        // Apply time decay to get current values
        var now = DateTimeOffset.UtcNow;
        var dt = (now - entry.LastDecayTime).TotalHours;
        var decayFactor = Math.Exp(-dt / _options.DecayTauHours);

        var decayedTotal = entry.DecayedTotalCount * decayFactor;

        // Require minimum sample size for meaningful rate
        if (decayedTotal < _options.MinSampleSize)
            return 0.0;

        var decayedBot = entry.DecayedBotCount * decayFactor;
        return decayedBot / decayedTotal;
    }

    /// <summary>
    ///     Get top N countries sorted by decayed bot rate (for UI display).
    /// </summary>
    public IReadOnlyList<CountryReputation> GetTopBotCountries(int count)
    {
        var now = DateTimeOffset.UtcNow;

        return _entries.Values
            .Select(e =>
            {
                var dt = (now - e.LastDecayTime).TotalHours;
                var decayFactor = Math.Exp(-dt / _options.DecayTauHours);
                var decayedBot = e.DecayedBotCount * decayFactor;
                var decayedTotal = e.DecayedTotalCount * decayFactor;
                var botRate = decayedTotal >= _options.MinSampleSize
                    ? decayedBot / decayedTotal
                    : 0.0;

                return new CountryReputation
                {
                    CountryCode = e.CountryCode,
                    CountryName = e.CountryName,
                    BotRate = botRate,
                    DecayedBotCount = decayedBot,
                    DecayedTotalCount = decayedTotal,
                    RawBotCount = e.RawBotCount,
                    RawTotalCount = e.RawTotalCount,
                    LastSeen = e.LastSeen,
                    FirstSeen = e.FirstSeen
                };
            })
            .Where(r => r.RawTotalCount >= _options.MinSampleSize)
            .OrderByDescending(r => r.BotRate)
            .Take(count)
            .ToList();
    }

    /// <summary>
    ///     Get all tracked countries with current stats (for diagnostics).
    /// </summary>
    public IReadOnlyList<CountryReputation> GetAllCountries()
    {
        var now = DateTimeOffset.UtcNow;

        return _entries.Values
            .Select(e =>
            {
                var dt = (now - e.LastDecayTime).TotalHours;
                var decayFactor = Math.Exp(-dt / _options.DecayTauHours);
                var decayedBot = e.DecayedBotCount * decayFactor;
                var decayedTotal = e.DecayedTotalCount * decayFactor;
                var botRate = decayedTotal >= 1.0
                    ? decayedBot / decayedTotal
                    : 0.0;

                return new CountryReputation
                {
                    CountryCode = e.CountryCode,
                    CountryName = e.CountryName,
                    BotRate = botRate,
                    DecayedBotCount = decayedBot,
                    DecayedTotalCount = decayedTotal,
                    RawBotCount = e.RawBotCount,
                    RawTotalCount = e.RawTotalCount,
                    LastSeen = e.LastSeen,
                    FirstSeen = e.FirstSeen
                };
            })
            .OrderByDescending(r => r.BotRate)
            .ToList();
    }

    /// <summary>
    ///     Internal mutable entry used inside ConcurrentDictionary.
    /// </summary>
    private record CountryReputationEntry
    {
        public required string CountryCode { get; init; }
        public required string CountryName { get; init; }
        public double DecayedBotCount { get; init; }
        public double DecayedTotalCount { get; init; }
        public int RawBotCount { get; init; }
        public int RawTotalCount { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public DateTimeOffset LastDecayTime { get; init; }
    }
}

/// <summary>
///     Immutable snapshot of a country's bot reputation (for UI/API).
/// </summary>
public record CountryReputation
{
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public double BotRate { get; init; }
    public double DecayedBotCount { get; init; }
    public double DecayedTotalCount { get; init; }
    public int RawBotCount { get; init; }
    public int RawTotalCount { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
}
