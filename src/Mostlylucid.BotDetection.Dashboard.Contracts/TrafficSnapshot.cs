using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Dashboard.Contracts;

public sealed record TrafficQuery(
    string Window = "24h", DateTimeOffset? From = null, DateTimeOffset? To = null,
    string[]? Domains = null, bool IncludeInternal = false, int Limit = 50);

/// <summary>One bounded traffic read. Empty data is a successful response; failures use HTTP errors.</summary>
public sealed record TrafficSnapshot(
    DateTimeOffset GeneratedAtUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    string Window, IReadOnlyList<string> Domains, bool IncludeInternal,
    TrafficSummary Summary, IReadOnlyList<TrafficBucket> Timeline,
    IReadOnlyList<TrafficDetection> Detections);

public sealed record TrafficSummary(int Requests, int Humans, int Bots, int Uncertain);
public sealed record TrafficBucket(DateTimeOffset TimestampUtc, int Requests, int Humans, int Bots);
public sealed record TrafficDetection(
    DateTimeOffset TimestampUtc, string Method, string Path,
    bool IsBot, double BotProbability, string RiskBand, string? BotName = null, string? Domain = null);

[JsonSerializable(typeof(TrafficSnapshot))]
[JsonSerializable(typeof(TrafficQuery))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true)]
public partial class DashboardJsonContext : JsonSerializerContext;
