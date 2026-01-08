using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Orchestration.PiiManagement;

/// <summary>
///     Manages ephemeral PII data that exists ONLY as long as detectors need it.
///     CRITICAL: PII should NEVER be stored in signal payloads.
///     - Signals contain ONLY indicators (ipavailable) or hashed values (ipdetected:&lt;hash&gt;)
///     - Raw PII is accessed ONLY directly from BlackboardState
///     - PII is cleared immediately after all detectors complete
/// </summary>
public sealed class EphemeralPiiManager
{
    private readonly ConcurrentDictionary<string, PiiData> _piiStore = new();

    /// <summary>
    ///     Gets the count of currently stored PII entries (for monitoring).
    /// </summary>
    public int Count => _piiStore.Count;

    /// <summary>
    ///     Stores PII for a request.
    ///     This data is ephemeral and will be cleared after detection completes.
    /// </summary>
    public void StorePii(string requestId, PiiData pii)
    {
        _piiStore[requestId] = pii;
    }

    /// <summary>
    ///     Retrieves PII for direct access by detectors.
    ///     IMPORTANT: Detectors must access PII via this method or BlackboardState properties,
    ///     NEVER from signal payloads.
    /// </summary>
    public PiiData? GetPii(string requestId)
    {
        _piiStore.TryGetValue(requestId, out var pii);
        return pii;
    }

    /// <summary>
    ///     Clears PII immediately after detection completes.
    ///     This ensures PII exists in memory only as long as detectors need it.
    /// </summary>
    public void ClearPii(string requestId)
    {
        _piiStore.TryRemove(requestId, out _);
    }
}

/// <summary>
///     Container for all PII data.
///     This is the ONLY place where raw PII is stored during detection.
/// </summary>
public sealed class PiiData
{
    /// <summary>Raw IP address (NEVER put in signal payload)</summary>
    public string? IpAddress { get; init; }

    /// <summary>Raw user agent (NEVER put in signal payload)</summary>
    public string? UserAgent { get; init; }

    /// <summary>Geographic location data (NEVER put in signal payload)</summary>
    public GeoLocationData? GeoLocation { get; init; }

    /// <summary>Session ID (NEVER put in signal payload)</summary>
    public string? SessionId { get; init; }

    /// <summary>Referer header (NEVER put in signal payload)</summary>
    public string? Referer { get; init; }

    /// <summary>Accept-Language header (NEVER put in signal payload)</summary>
    public string? AcceptLanguage { get; init; }
}

/// <summary>
///     Geographic location data (PII).
/// </summary>
public sealed class GeoLocationData
{
    public string? CountryCode { get; init; }
    public string? Region { get; init; }
    public string? City { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Timezone { get; init; }
}