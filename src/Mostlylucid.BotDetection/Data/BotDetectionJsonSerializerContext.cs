using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Source-generated JSON serialization context for AOT/NativeAOT support.
///     Required for bot list fetching to work in trimmed/AOT environments.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
// Simple types
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
// AWS types
[JsonSerializable(typeof(BotListFetcher.AwsIpRangesResponse))]
[JsonSerializable(typeof(BotListFetcher.AwsPrefix))]
[JsonSerializable(typeof(BotListFetcher.AwsIpv6Prefix))]
[JsonSerializable(typeof(List<BotListFetcher.AwsPrefix>))]
[JsonSerializable(typeof(List<BotListFetcher.AwsIpv6Prefix>))]
// GCP types
[JsonSerializable(typeof(BotListFetcher.GcpIpRangesResponse))]
[JsonSerializable(typeof(BotListFetcher.GcpPrefix))]
[JsonSerializable(typeof(List<BotListFetcher.GcpPrefix>))]
// Azure types
[JsonSerializable(typeof(BotListFetcher.AzureIpRangesResponse))]
[JsonSerializable(typeof(BotListFetcher.AzureServiceTag))]
[JsonSerializable(typeof(BotListFetcher.AzureServiceTagProperties))]
[JsonSerializable(typeof(List<BotListFetcher.AzureServiceTag>))]
// Crawler/Scanner types
[JsonSerializable(typeof(BotListFetcher.CrawlerEntry))]
[JsonSerializable(typeof(BotListFetcher.ScannerUserAgentEntry))]
[JsonSerializable(typeof(List<BotListFetcher.CrawlerEntry>))]
[JsonSerializable(typeof(List<BotListFetcher.ScannerUserAgentEntry>))]
// Common User Agent types
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
// Persisted-state types serialised into SQLite columns by the various stores
[JsonSerializable(typeof(CentroidSequence))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<int, int>))]
[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(double[]))]
internal partial class BotDetectionJsonSerializerContext : JsonSerializerContext
{
}