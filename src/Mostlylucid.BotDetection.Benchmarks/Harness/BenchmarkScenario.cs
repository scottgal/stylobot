using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     A benchmark scenario loaded from a *.benchmark.yaml file.
/// </summary>
public sealed class BenchmarkScenario
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "detector")]
    public string DetectorName { get; set; } = "";

    [YamlMember(Alias = "request")]
    public RequestSpec Request { get; set; } = new();

    [YamlMember(Alias = "signals")]
    public Dictionary<string, object>? Signals { get; set; }

    [YamlMember(Alias = "thresholds")]
    public ThresholdSpec? Thresholds { get; set; }

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    public bool IsPipeline => DetectorName == "_pipeline";

    /// <summary>
    ///     Build a BlackboardState from this scenario's request + signals.
    ///     The signal dictionary is passed as IReadOnlyDictionary — BlackboardState
    ///     will cast it to ConcurrentDictionary internally to get SignalWriter access.
    /// </summary>
    public BlackboardState ToBlackboardState()
    {
        var context = BuildHttpContext();

        // Use ConcurrentDictionary so the orchestrator's internal cast for SignalWriter works
        var signalDict = new ConcurrentDictionary<string, object>(
            Signals ?? new Dictionary<string, object>());

        return new BlackboardState
        {
            HttpContext = context,
            Signals = signalDict,
            CurrentRiskScore = 0.0,
            DetectionConfidence = 0.0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = $"bench-{Name}",
            Elapsed = TimeSpan.Zero
        };
    }

    /// <summary>
    ///     Build a DefaultHttpContext from the request spec.
    /// </summary>
    public HttpContext BuildHttpContext()
    {
        var context = new DefaultHttpContext();

        var pathAndQuery = Request.Path ?? "/";
        var queryIndex = pathAndQuery.IndexOf('?');
        if (queryIndex >= 0)
        {
            context.Request.Path = pathAndQuery[..queryIndex];
            context.Request.QueryString = new QueryString(pathAndQuery[queryIndex..]);
        }
        else
        {
            context.Request.Path = pathAndQuery;
        }

        context.Request.Method = Request.Method ?? "GET";
        context.Request.Scheme = Request.Protocol ?? "https";

        if (Request.Headers != null)
        {
            foreach (var (key, value) in Request.Headers)
                context.Request.Headers[key] = value;
        }

        if (!string.IsNullOrEmpty(Request.Ip) && IPAddress.TryParse(Request.Ip, out var ip))
            context.Connection.RemoteIpAddress = ip;

        context.TraceIdentifier = $"bench-{Name}";
        return context;
    }

    public override string ToString() => Name;
}

public sealed class RequestSpec
{
    [YamlMember(Alias = "method")]
    public string? Method { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "protocol")]
    public string? Protocol { get; set; }

    [YamlMember(Alias = "ip")]
    public string? Ip { get; set; }

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class ThresholdSpec
{
    [YamlMember(Alias = "max_mean_ns")]
    public long? MaxMeanNs { get; set; }

    [YamlMember(Alias = "max_allocated_bytes")]
    public long? MaxAllocatedBytes { get; set; }

    [YamlMember(Alias = "max_p95_ns")]
    public long? MaxP95Ns { get; set; }
}
