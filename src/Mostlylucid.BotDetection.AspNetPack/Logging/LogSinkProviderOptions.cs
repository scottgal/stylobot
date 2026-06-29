using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

/// <summary>
///     Configuration for <see cref="LogSinkLoggerProvider"/> -- the in-process
///     <c>ILogger</c> capture that pipes the gateway's own warn+ output into the
///     Log-sink dashboard tile. Distinct from <see cref="Configuration.LogSinkOptions"/>,
///     which governs the SDK-side OTLP shipper (the "send my host's logs to the
///     gateway" path); this one governs the "capture my own logs locally" path.
///     <para>
///         Bound from <c>BotDetection:AspNetPack:LogSink:GatewayCapture</c> so the
///         gateway-only capture knobs sit cleanly under the existing log-sink
///         configuration root without overloading <c>LogSinkOptions</c> with
///         fields whose meaning flips depending on which side of the OTLP wire
///         is consuming them.
///     </para>
/// </summary>
public sealed class LogSinkProviderOptions
{
    /// <summary>
    ///     Minimum <see cref="LogLevel"/> the provider forwards to registered
    ///     <see cref="IGatewayLogIngestSink"/> instances. Defaults to
    ///     <see cref="LogLevel.Warning"/> so the gateway's high-volume
    ///     informational chatter (per-request access lines, hosted-service
    ///     loops) doesn't flood the per-fingerprint ring. Operators raise the
    ///     gate explicitly via <c>BotDetection:AspNetPack:LogSink:GatewayCapture:MinLevel</c>
    ///     when debugging.
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Warning;
}
