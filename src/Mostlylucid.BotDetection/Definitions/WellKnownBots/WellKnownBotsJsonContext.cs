using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext"/> for the arcjet
///     well-known-bots catalog. Reflection-based JSON is disabled under
///     <c>PublishAot=true</c> (the AOT console + sidecar), so
///     <see cref="WellKnownBotRefreshService"/> must deserialize the download
///     through this context's <c>JsonTypeInfo</c> rather than the reflection
///     overload of <c>GetFromJsonAsync</c>. Without it the refresh throws
///     "Reflection-based serialization has been disabled" on every AOT host and
///     the catalog silently never loads.
/// </summary>
[JsonSerializable(typeof(List<WellKnownBotEntry>))]
internal sealed partial class WellKnownBotsJsonContext : JsonSerializerContext;