using System.Net;
using Microsoft.AspNetCore.Http;
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     A benchmark scenario loaded from a <c>*.benchmark.yaml</c> file. Recovered from the
///     pre-atom-refactor harness (deleted in <c>cbf0c564</c>) with the dead
///     <c>ToBlackboardState()</c> path removed — the contributor-era <c>BlackboardState</c>
///     no longer exists; the atom pipeline is driven entirely from the built
///     <see cref="HttpContext"/> (the orchestrator hydrates its own signal sink internally).
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

    /// <summary>Scenarios tagged <c>detector: _pipeline</c> drive the full atom pipeline.</summary>
    public bool IsPipeline => DetectorName == "_pipeline";

    /// <summary>
    ///     Build a <see cref="DefaultHttpContext"/> from the request spec. This is the only
    ///     input the atom pipeline needs: <see cref="BotDetectionOrchestrator"/> hydrates its
    ///     signal sink from the context and uses <see cref="HttpContext.TraceIdentifier"/> as
    ///     the session id.
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
