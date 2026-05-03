using System.Diagnostics;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Telemetry;

/// <summary>
///     Telemetry instrumentation for geo detection operations
/// </summary>
public static class GeoDetectionTelemetry
{
    /// <summary>
    ///     Activity source name for geo detection
    /// </summary>
    public const string ActivitySourceName = "Mostlylucid.GeoDetection";

    /// <summary>
    ///     Activity source for geo detection telemetry
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, GetVersion());

    private static string GetVersion()
    {
        return typeof(GeoDetectionTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    /// <summary>
    ///     Starts an activity for geo location lookup
    /// </summary>
    public static Activity? StartGetLocationActivity(string? ipAddress = null)
    {
        var activity = ActivitySource.StartActivity("GeoDetection.GetLocation");

        if (activity != null)
            if (ipAddress != null)
                activity.SetTag("http.client_ip", ipAddress);

        return activity;
    }

    /// <summary>
    ///     Starts an activity for country check
    /// </summary>
    public static Activity? StartIsFromCountryActivity(string? ipAddress = null, string? countryCode = null)
    {
        var activity = ActivitySource.StartActivity("GeoDetection.IsFromCountry");

        if (activity != null)
        {
            if (ipAddress != null)
                activity.SetTag("http.client_ip", ipAddress);
            if (countryCode != null)
                activity.SetTag("mostlylucid.geodetection.target_country", countryCode);
        }

        return activity;
    }

    /// <summary>
    ///     Records geo location result on the activity
    /// </summary>
    public static void RecordResult(Activity? activity, GeoLocation? location, bool cacheHit = false)
    {
        if (activity == null)
            return;

        activity.SetTag("mostlylucid.geodetection.cache_hit", cacheHit);

        if (location != null)
        {
            activity.SetTag("mostlylucid.geodetection.country_code", location.CountryCode);
            activity.SetTag("mostlylucid.geodetection.country_name", location.CountryName);

            if (!string.IsNullOrEmpty(location.ContinentCode))
                activity.SetTag("mostlylucid.geodetection.continent_code", location.ContinentCode);

            if (!string.IsNullOrEmpty(location.City))
                activity.SetTag("mostlylucid.geodetection.city", location.City);

            if (!string.IsNullOrEmpty(location.RegionCode))
                activity.SetTag("mostlylucid.geodetection.region_code", location.RegionCode);

            activity.SetTag("mostlylucid.geodetection.is_vpn", location.IsVpn);
            activity.SetTag("mostlylucid.geodetection.is_hosting", location.IsHosting);

            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetTag("mostlylucid.geodetection.found", false);
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    /// <summary>
    ///     Records country check result on the activity
    /// </summary>
    public static void RecordCountryCheckResult(Activity? activity, bool isFromCountry,
        string? actualCountryCode = null)
    {
        if (activity == null)
            return;

        activity.SetTag("mostlylucid.geodetection.is_from_country", isFromCountry);

        if (!string.IsNullOrEmpty(actualCountryCode))
            activity.SetTag("mostlylucid.geodetection.actual_country", actualCountryCode);

        activity.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    ///     Records cache source on the activity
    /// </summary>
    public static void RecordCacheSource(Activity? activity, string source)
    {
        if (activity == null)
            return;

        activity.SetTag("mostlylucid.geodetection.cache_source", source);
    }

    /// <summary>
    ///     Records an exception on the activity
    /// </summary>
    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("exception.type", ex.GetType().FullName);
        activity.SetTag("exception.message", ex.Message);
    }
}