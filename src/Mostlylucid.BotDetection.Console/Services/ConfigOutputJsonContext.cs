using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Source-generated JSON context for the <c>--output-config</c> dump. The Console
///     binary is AOT-published, so reflection-based JsonSerializer is off; this context
///     gives a statically-known JsonTypeInfo for <see cref="BotDetectionOptions"/> and
///     the dump wrapper.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BotDetectionOptions))]
[JsonSerializable(typeof(ConfigOutputCommand.ConfigDumpWrapper))]
internal partial class ConfigOutputJsonContext : JsonSerializerContext
{
}
