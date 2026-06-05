using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     System.Text.Json source-gen context for the upstream-signals header parse.
///     The <c>X-Bot-Detection-Signals</c> header arrives on every request coming
///     through the dashboard host when an upstream gateway has already done detection.
///     Replacing the reflection-based <c>JsonSerializer.Deserialize&lt;Dictionary&lt;string,JsonElement&gt;&gt;</c>
///     call site with a source-gen <see cref="JsonTypeInfo{T}"/> handles two things at
///     once: closes the IL2026/IL3050 AoT warnings the dashboard layer was emitting,
///     and removes a per-request reflection hit on the edge-hydration path.
///
///     This context covers exactly the one shape that ParseUpstreamSignals
///     deserialises into. Per the no-word-lists rule the surface is not an inventory
///     of signal names — it is a generic dict shape; the signal-key allowlist still
///     lives at the call site in the broadcast middleware.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
internal partial class UpstreamSignalsJsonContext : JsonSerializerContext;