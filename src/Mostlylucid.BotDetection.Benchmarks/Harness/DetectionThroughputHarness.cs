using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     Driver that fires N concurrent <c>DetectAsync</c> calls through a
///     fully-wired orchestrator + reports p50 / p95 / p99 latency, RPS, and
///     allocation rate. Microbenchmarks (BDN) measure single-call cost in
///     isolation; this harness measures what happens under realistic concurrent
///     load -- where lock contention, async-state-machine churn, and pool
///     starvation show up. Bypasses Kestrel + HTTP serialization so the
///     numbers reflect detection-pipeline cost only.
///     <para>
///     Invoke from <c>Program.cs</c>:
///     <code>
///     await Harness.DetectionThroughputHarness.RunAsync(
///         concurrentClients: 64, requestsPerClient: 1000, scenarioFile: null);
///     </code>
///     </para>
/// </summary>
public static class DetectionThroughputHarness
{
    public static async Task<ThroughputReport> RunAsync(
        int concurrentClients,
        int requestsPerClient,
        string? scenarioFile = null,
        TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Enabled"] = "true",
                ["BotDetection:AiDetection:OllamaEnabled"] = "false",
                ["BotDetection:AiDetection:AnthropicEnabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddBotDetection();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<BlackboardOrchestrator>();

        // Warm up: each client builds a per-thread context (avoid one shared
        // HttpContext that would force serialization at the per-request mutate
        // points). Half human Chrome traffic, half curl-bot traffic, matching
        // the perf-doc workload.
        var contexts = BuildContexts(concurrentClients, requestsPerClient);

        writer.WriteLine($"# Throughput harness: {concurrentClients} clients x {requestsPerClient} req = "
                         + $"{concurrentClients * requestsPerClient} requests");
        writer.WriteLine("# Warm-up: 200 detections per client");

        for (var i = 0; i < concurrentClients; i++)
            for (var j = 0; j < 200; j++)
                _ = await orchestrator.DetectAsync(contexts[i][j % requestsPerClient]);

        // GC settle so the workload measurement starts on a quiet heap.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var latenciesNs = new long[concurrentClients * requestsPerClient];
        var startBytes = GC.GetTotalAllocatedBytes(precise: false);
        var globalSw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, concurrentClients).Select(clientIdx =>
            Task.Run(async () =>
            {
                var ctxList = contexts[clientIdx];
                var baseSlot = clientIdx * requestsPerClient;
                for (var j = 0; j < requestsPerClient; j++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    _ = await orchestrator.DetectAsync(ctxList[j]);
                    var t1 = Stopwatch.GetTimestamp();
                    latenciesNs[baseSlot + j] = (long)((t1 - t0) * (1_000_000_000.0 / Stopwatch.Frequency));
                }
            })));

        globalSw.Stop();
        var endBytes = GC.GetTotalAllocatedBytes(precise: false);

        Array.Sort(latenciesNs);
        long p50 = latenciesNs[latenciesNs.Length / 2];
        long p95 = latenciesNs[(int)(latenciesNs.Length * 0.95)];
        long p99 = latenciesNs[(int)(latenciesNs.Length * 0.99)];
        long max = latenciesNs[^1];

        var totalRequests = latenciesNs.Length;
        var elapsedSec = globalSw.Elapsed.TotalSeconds;
        var rps = totalRequests / elapsedSec;
        var bytesAllocated = endBytes - startBytes;
        var bytesPerReq = bytesAllocated / (double)totalRequests;

        var report = new ThroughputReport(
            ConcurrentClients: concurrentClients,
            TotalRequests: totalRequests,
            ElapsedSeconds: elapsedSec,
            RequestsPerSecond: rps,
            P50Ns: p50,
            P95Ns: p95,
            P99Ns: p99,
            MaxNs: max,
            BytesAllocated: bytesAllocated,
            BytesPerRequest: bytesPerReq);

        writer.WriteLine($"# elapsed:  {elapsedSec:F3} s, {totalRequests} requests");
        writer.WriteLine($"# RPS:      {rps:F0}");
        writer.WriteLine($"# p50:      {p50 / 1000.0:F1} µs   p95: {p95 / 1000.0:F1} µs   "
                         + $"p99: {p99 / 1000.0:F1} µs   max: {max / 1000.0:F1} µs");
        writer.WriteLine($"# allocs:   {bytesAllocated / 1024.0 / 1024.0:F1} MB total, "
                         + $"{bytesPerReq:F0} B/req");
        return report;
    }

    private static HttpContext[][] BuildContexts(int clients, int perClient)
    {
        var result = new HttpContext[clients][];
        for (var c = 0; c < clients; c++)
        {
            var perClientArr = new HttpContext[perClient];
            for (var r = 0; r < perClient; r++)
                perClientArr[r] = (c + r) % 2 == 0 ? HumanContext(c, r) : BotContext(c, r);
            result[c] = perClientArr;
        }
        return result;
    }

    private static HttpContext HumanContext(int client, int req)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        ctx.Request.Headers.Accept = "text/html,application/xhtml+xml";
        ctx.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        ctx.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        ctx.Request.Headers.Referer = "https://google.com";
        // Vary the IP and path across the matrix so SignatureCoordinator and
        // Markov tracker see a realistic working set instead of one entry.
        ctx.Connection.RemoteIpAddress = IPAddress.Parse($"203.0.113.{(byte)(client % 254 + 1)}");
        ctx.Request.Method = "GET";
        ctx.Request.Path = $"/products/{req % 32}";
        return ctx;
    }

    private static HttpContext BotContext(int client, int req)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "curl/8.4.0";
        ctx.Request.Headers.Accept = "*/*";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{(byte)(client % 254 + 1)}");
        ctx.Request.Method = "GET";
        ctx.Request.Path = $"/api/v1/scan/{req % 8}";
        return ctx;
    }
}

public sealed record ThroughputReport(
    int ConcurrentClients,
    int TotalRequests,
    double ElapsedSeconds,
    double RequestsPerSecond,
    long P50Ns,
    long P95Ns,
    long P99Ns,
    long MaxNs,
    long BytesAllocated,
    double BytesPerRequest);
