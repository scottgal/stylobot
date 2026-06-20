using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public sealed record LogRecord(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    string Body,
    string? TraceIdHex,
    string? SpanIdHex,
    string? FingerprintId,
    IReadOnlyDictionary<string, string> Attributes);
