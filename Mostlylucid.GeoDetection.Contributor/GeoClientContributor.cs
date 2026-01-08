using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Contributing detector that analyzes client-side geolocation data.
///     Works with <client-geo /> tag helper to detect timezone/locale/coordinate mismatches.
/// </summary>
public class GeoClientContributor : ContributingDetectorBase
{
    private readonly ILogger<GeoClientContributor> _logger;
    private readonly GeoContributorOptions _options;

    public GeoClientContributor(
        IOptions<GeoContributorOptions> options,
        ILogger<GeoClientContributor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "GeoClient";
    public override int Priority => _options.Priority + 10;  // Run after server-side Geo

    // Trigger after server-side geo data is available
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(GeoSignalKeys.GeoCountryCode),
        new SignalExistsTrigger(ClientGeoSignalKeys.ClientGeoTimezone)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask; // Sync logic

        var contributions = new List<DetectionContribution>();

        // Get server-side geo data
        var serverCountry = state.GetSignal<string>(GeoSignalKeys.GeoCountryCode);
        var serverTimezone = state.GetSignal<string>(GeoSignalKeys.GeoTimezone);

        // Get client-side geo data
        var clientTimezone = state.GetSignal<string>(ClientGeoSignalKeys.ClientGeoTimezone);
        var clientLocale = state.GetSignal<string>(ClientGeoSignalKeys.ClientGeoLocale);
        var clientCoords = state.GetSignal<ClientCoordinates>(ClientGeoSignalKeys.ClientGeoCoords);

        if (string.IsNullOrEmpty(clientTimezone))
        {
            // No client geo data - not suspicious, just unavailable
            return None();
        }

        // Check timezone mismatch
        if (!string.IsNullOrEmpty(serverTimezone) &&
            !string.IsNullOrEmpty(clientTimezone) &&
            !TimezonesMatch(serverTimezone, clientTimezone))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "GeoClient-Timezone",
                ConfidenceDelta = 0.25,
                Weight = 1.2,
                Reason = $"Timezone mismatch: Server={serverTimezone}, Client={clientTimezone}",
                Signals = ImmutableDictionary<string, object>.Empty
                    .Add(ClientGeoSignalKeys.ClientGeoTimezoneMismatch, true)
            });
        }

        // Check if client coords drastically differ from server IP location
        if (clientCoords != null && !string.IsNullOrEmpty(serverCountry))
        {
            var serverLat = state.GetSignal<double?>(GeoSignalKeys.GeoLatitude);
            var serverLon = state.GetSignal<double?>(GeoSignalKeys.GeoLongitude);

            if (serverLat.HasValue && serverLon.HasValue)
            {
                var distance = CalculateDistance(
                    serverLat.Value, serverLon.Value,
                    clientCoords.Latitude, clientCoords.Longitude);

                // If coordinates are >500km apart, suspicious
                if (distance > 500)
                {
                    contributions.Add(DetectionContribution.Bot(
                        Name, "GeoClient-Coordinates",
                        0.6,
                        $"Client coords {distance:F0}km from server IP location",
                        1.8,
                        nameof(BotType.Unknown)
                    ) with
                    {
                        Signals = ImmutableDictionary<string, object>.Empty
                            .Add(ClientGeoSignalKeys.ClientGeoCoordMismatch, true)
                            .Add(ClientGeoSignalKeys.ClientGeoDistanceKm, distance)
                    });
                }
            }
        }

        // Check locale consistency
        if (!string.IsNullOrEmpty(clientLocale))
        {
            var acceptLanguage = state.HttpContext.Request.Headers.AcceptLanguage.ToString();
            if (!string.IsNullOrEmpty(acceptLanguage) &&
                !LocalesMatch(clientLocale, acceptLanguage))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "GeoClient-Locale",
                    ConfidenceDelta = 0.15,
                    Weight = 0.8,
                    Reason = $"Client locale '{clientLocale}' doesn't match Accept-Language '{acceptLanguage}'",
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(ClientGeoSignalKeys.ClientGeoLocaleMismatch, true)
                });
            }
        }

        return contributions;
    }

    private static bool TimezonesMatch(string serverTz, string clientTz)
    {
        // Exact match
        if (serverTz.Equals(clientTz, StringComparison.OrdinalIgnoreCase))
            return true;

        // Common timezone equivalents
        var equivalents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["America/New_York"] = ["US/Eastern", "America/Detroit", "America/Indiana/Indianapolis"],
            ["America/Chicago"] = ["US/Central", "America/Indiana/Knox"],
            ["America/Denver"] = ["US/Mountain", "America/Boise"],
            ["America/Los_Angeles"] = ["US/Pacific", "America/Tijuana"],
            ["Europe/London"] = ["GB", "Europe/Belfast"],
            ["Europe/Paris"] = ["CET", "Europe/Brussels", "Europe/Amsterdam"],
            ["Asia/Tokyo"] = ["Japan"],
            ["Asia/Shanghai"] = ["Asia/Hong_Kong", "PRC"]
        };

        foreach (var (canonical, alts) in equivalents)
        {
            if (serverTz.Equals(canonical, StringComparison.OrdinalIgnoreCase) &&
                alts.Contains(clientTz, StringComparer.OrdinalIgnoreCase))
                return true;

            if (clientTz.Equals(canonical, StringComparison.OrdinalIgnoreCase) &&
                alts.Contains(serverTz, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LocalesMatch(string clientLocale, string acceptLanguage)
    {
        var primaryLang = acceptLanguage.Split(',').FirstOrDefault()?.Split(';').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(primaryLang))
            return true;  // Can't determine

        return clientLocale.StartsWith(primaryLang, StringComparison.OrdinalIgnoreCase) ||
               primaryLang.StartsWith(clientLocale, StringComparison.OrdinalIgnoreCase);
    }

    // Haversine formula for distance between coordinates
    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Earth radius in km

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}

/// <summary>
///     Client-side coordinates from Geolocation API.
/// </summary>
public record ClientCoordinates(double Latitude, double Longitude, double Accuracy);

/// <summary>
///     Signal keys for client-side geo data.
/// </summary>
public static partial class ClientGeoSignalKeys
{
    public const string ClientGeoTimezone = "client_geo.timezone";
    public const string ClientGeoLocale = "client_geo.locale";
    public const string ClientGeoCoords = "client_geo.coordinates";
    public const string ClientGeoTimezoneMismatch = "client_geo.timezone_mismatch";
    public const string ClientGeoCoordMismatch = "client_geo.coord_mismatch";
    public const string ClientGeoLocaleMismatch = "client_geo.locale_mismatch";
    public const string ClientGeoDistanceKm = "client_geo.distance_km";
}
