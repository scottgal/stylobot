namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>Where the dashboard reads its data from.</summary>
public enum DashboardSourceType
{
    /// <summary>Same-process SQLite store. Correct for stylobot-all + single-host deployments.</summary>
    Local,
    /// <summary>HTTP client against a remote stylobot gateway's <c>/api/v1/*</c>.</summary>
    Rest
}

/// <summary>Whether the dashboard subscribes to a live invalidation stream.</summary>
public enum DashboardLiveFeedType
{
    /// <summary>Poll-only. Browsers refresh on their own cadence (HTMX intervals).</summary>
    None,
    /// <summary>Connect to a remote gateway's SignalR invalidation hub and relay locally.</summary>
    SignalR
}

/// <summary>
///     Configuration for a remote-mode dashboard host. Bound from <c>StyloBot:Source</c>:
///     <code>
///     "StyloBot": { "Source": {
///         "Pull": {
///             "Type": "rest",
///             "Url":  "https://gateway.internal:8080",
///             "ApiKey": "SB-...",
///             "TimeoutSeconds": 10
///         },
///         "Live": {
///             "Type": "signalr",
///             "Url":  "https://gateway.internal:8080/api/v1/hub"
///         }
///     }}
///     </code>
/// </summary>
public sealed class DashboardSourceOptions
{
    public DashboardSourcePullOptions Pull { get; set; } = new();
    public DashboardSourceLiveOptions Live { get; set; } = new();
}

public sealed class DashboardSourcePullOptions
{
    /// <summary>Source backend - <see cref="DashboardSourceType.Local"/> or <see cref="DashboardSourceType.Rest"/>.</summary>
    public DashboardSourceType Type { get; set; } = DashboardSourceType.Local;

    /// <summary>Base URL of the gateway exposing <c>/api/v1/*</c>. Required when <see cref="Type"/> = Rest.</summary>
    public string? Url { get; set; }

    /// <summary>API key sent as <c>X-SB-Api-Key</c>. Required when <see cref="Type"/> = Rest.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     HTTP request timeout for each gateway <c>/api/v1/*</c> pull. This is the
    ///     ceiling on how long a dashboard page will wait for one aggregate before the
    ///     remote client fails soft (GatewayApiClient catches the timeout and returns
    ///     an empty result, so the page still renders). Kept short because a dashboard
    ///     read that takes longer than this is pathological (a cold / unindexed durable
    ///     aggregate, or a stalled gateway) - waiting 30s to render an empty page is the
    ///     bug this fixes. Legit reads are single-digit seconds; raise it for a
    ///     genuinely high-latency WAN link. Default 10s.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class DashboardSourceLiveOptions
{
    /// <summary>Live feed kind - <see cref="DashboardLiveFeedType.None"/> or <see cref="DashboardLiveFeedType.SignalR"/>.</summary>
    public DashboardLiveFeedType Type { get; set; } = DashboardLiveFeedType.None;

    /// <summary>SignalR hub URL on the gateway. Required when <see cref="Type"/> = SignalR.</summary>
    public string? Url { get; set; }
}
