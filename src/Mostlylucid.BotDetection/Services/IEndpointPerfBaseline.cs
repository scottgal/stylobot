namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Read-only per-(method, normalized-template) p95 lookup consumed by the
///     load-shed hot path to normalize upstream RTT into a dimensionless
///     deviation ratio. Implementations cache the values; the hot-path call
///     must be lock-free and allocation-free.
///     <para>
///     Optional DI: hosts that have no <see cref="UI.Services.IDashboardEventStore"/>
///     register <see cref="NullEndpointPerfBaseline"/>, and consumers degrade
///     to ratio 1.0 (no shed contribution) on those hosts.
///     </para>
///     <para>
///     <strong>Single consumer.</strong> Only the middleware OnCompleted hook
///     reads from this interface. Dashboard rendering, policy decisions, ops
///     surfaces all continue to read raw-path stats from
///     <see cref="UI.Services.IDashboardEventStore"/> directly. Do not add
///     convenience members here that would invite other call sites.
///     </para>
/// </summary>
public interface IEndpointPerfBaseline
{
    /// <summary>
    ///     Expected p95 in milliseconds for the given (method, normalized
    ///     template). Returns 0 when no trustworthy baseline exists yet (no
    ///     observations, below <see cref="PipelineLoadSensorOptions.MinSamplesForTrustedBaseline"/>,
    ///     or implementation absent). Callers MUST treat 0 as
    ///     "unknown endpoint, contribute neutral 1.0 ratio".
    /// </summary>
    double GetExpectedMs(string method, string normalizedPath);
}

/// <summary>
///     No-op default. Boots hosts that have no per-endpoint stats source
///     (website-only / remote dashboard mode). Always returns 0.
/// </summary>
internal sealed class NullEndpointPerfBaseline : IEndpointPerfBaseline
{
    public double GetExpectedMs(string method, string normalizedPath) => 0.0;
}
