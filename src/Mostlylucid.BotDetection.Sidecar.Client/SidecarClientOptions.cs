using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarClientOptions
{
    public string Endpoint { get; set; } = "http://localhost:5090";
    public int TimeoutMs { get; set; } = 50;

    /// <summary>
    ///     What to do when the sidecar gRPC call fails (timeout, unreachable, RPC error).
    ///     Defaults to <see cref="FailureMode.FailOpen"/>. Set to
    ///     <see cref="FailureMode.FailClosed"/> for high-security deployments where
    ///     letting requests through without detection is worse than dropping them.
    /// </summary>
    public FailureMode OnFailure { get; set; } = FailureMode.FailOpen;
}
