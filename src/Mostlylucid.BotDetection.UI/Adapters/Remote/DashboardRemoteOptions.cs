namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Configuration for a remote-mode dashboard host. Bound from <c>StyloBot:Source</c>:
///     <code>
///     "StyloBot": { "Source": {
///         "Pull": {
///             "Type": "rest",
///             "Url":  "https://gateway.internal:8080",
///             "ApiKey": "SB-..."
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
    /// <summary><c>local</c> (default - same-process SQLite) or <c>rest</c> (remote viewer).</summary>
    public string Type { get; set; } = "local";

    /// <summary>Base URL of the gateway exposing <c>/api/v1/*</c>. Required when <see cref="Type"/> = rest.</summary>
    public string? Url { get; set; }

    /// <summary>API key sent as <c>X-SB-Api-Key</c>. Required when <see cref="Type"/> = rest.</summary>
    public string? ApiKey { get; set; }
}

public sealed class DashboardSourceLiveOptions
{
    /// <summary><c>signalr</c> (push) or <c>none</c> (poll-only).</summary>
    public string Type { get; set; } = "none";

    /// <summary>SignalR hub URL on the gateway. Required when <see cref="Type"/> = signalr.</summary>
    public string? Url { get; set; }
}
