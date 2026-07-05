namespace Mostlylucid.BotDetection.Dashboard;

/// <summary>
///     PII / feature-flag controls for what <c>DetectionBroadcastMiddleware</c>
///     writes into the dashboard event stream. Formerly co-located with the
///     legacy <c>DetectionRecord</c>; kept as a standalone options class since
///     the broadcast middleware still uses these knobs against the atom-
///     orchestrator evidence.
/// </summary>
public sealed class DetectionRecordOptions
{
    public bool IncludeClientIp { get; set; } = false;
    public bool IncludeUserAgent { get; set; } = true;
    public bool IncludeGeo { get; set; } = true;
    public bool IncludeLocale { get; set; } = true;
    public bool IncludeReferer { get; set; } = false;
    public Func<string?, string?>? RefererHostSelector { get; set; }
    public bool IncludeAcceptLanguages { get; set; } = true;
    public bool IncludeSecFetch { get; set; } = false;
    public Func<string?, string?>? DeriveReferrerHost { get; set; }
    public Func<string?, string?>? DeriveUaDeviceClass { get; set; }
}