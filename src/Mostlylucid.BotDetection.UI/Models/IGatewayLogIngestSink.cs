using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Direct, synchronous ingest hook used by a log-sink logger provider to route
///     the gateway's own <c>ILogger</c> output (gateway startup messages, exceptions,
///     <c>Yarp.ReverseProxy.*</c> upstream-error messages, etc.) into whatever
///     per-fingerprint cache the host has already provisioned for the Log-sink
///     dashboard tile.
///     <para>
///         This interface is a generic FOSS seam that lives in
///         <c>Mostlylucid.BotDetection.UI</c> -- alongside
///         <see cref="IRecentLogEntriesProvider"/> -- so any pack (the commercial
///         ASP.NET pack, OtelMesh, future packs) can depend on it without pulling a
///         pack's OTLP-proto vendor copy into their compile graph. The commercial
///         ASP.NET pack owns the provider impl + the OTLP shipper for remote-SDK
///         hosts; this interface is the seam between capture and whichever atom the
///         pack chose to back the dashboard tile with.
///     </para>
///     <para>
///         Implementations MUST be allocation-light and non-blocking: the call
///         is made from the per-request <c>ILogger.Log</c> hot path. Heavier
///         work (batching, PII scrub, export) belongs behind the dashboard
///         read path, not here. Implementations MUST also swallow their own
///         exceptions -- a throw from <see cref="Record"/> would surface on
///         the request that emitted the log line.
///     </para>
/// </summary>
public interface IGatewayLogIngestSink
{
    /// <summary>
    ///     Hand the <paramref name="entry"/> to the underlying store. See
    ///     contract above for the non-throw / non-block requirements.
    /// </summary>
    void Record(GatewayLogEntry entry);
}

/// <summary>
///     Minimal record passed across the FOSS / commercial boundary by
///     <see cref="IGatewayLogIngestSink"/>. Deliberately POCO with no proto
///     dependency so it lives cleanly in <c>Mostlylucid.BotDetection.UI</c>
///     -- the AspNet Pack's protobuf-vendored <c>LogRecord</c> belongs to the
///     OTLP shipper path and isn't appropriate for cross-pack contracts.
/// </summary>
public sealed record GatewayLogEntry(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    string Body,
    string? TraceIdHex,
    string? SpanIdHex,
    string? FingerprintId);
