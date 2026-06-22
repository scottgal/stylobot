using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

// ---------------------------------------------------------------------------
// Minimal AggregatedEvidence factory
// ---------------------------------------------------------------------------
internal static class Evidence
{
    internal static AggregatedEvidence Bot(double prob = 0.95) =>
        new()
        {
            BotProbability = prob,
            Confidence = 0.9,
            RiskBand = RiskBand.High
        };

    internal static AggregatedEvidence Human() =>
        new()
        {
            BotProbability = 0.05,
            Confidence = 0.9,
            RiskBand = RiskBand.VeryLow
        };
}

// ---------------------------------------------------------------------------
// Fake ILayoutExtractor implementations
// ---------------------------------------------------------------------------
internal sealed class FakeExtractor : ILayoutExtractor
{
    public string MarkdownToReturn { get; set; } = "# Extracted\n\nContent here.";
    public int CallCount { get; private set; }

    public Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = "Test Title",
            Markdown = MarkdownToReturn,
            Blocks = [],
            Stats = new ExtractionStats
            {
                BlockCount = 2,
                FingerprintShingleCount = 16,
                ParseTime = TimeSpan.Zero,
                FingerprintTime = TimeSpan.Zero,
                MatchTime = TimeSpan.Zero,
                RenderTime = TimeSpan.Zero
            },
            Match = new LayoutMatch
            {
                TemplateId = Guid.NewGuid(),
                TemplateVersion = 1,
                FingerprintHex = "abc123",
                Status = MatchStatus.FastPathHit,
                Similarity = 0.95,
                ObservationCount = 5,
                LatencyMatch = TimeSpan.FromMilliseconds(1),
                LatencyTotal = TimeSpan.FromMilliseconds(2),
            }
        });
    }
}

internal sealed class ThrowingExtractor : ILayoutExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated extractor failure");
}

// ---------------------------------------------------------------------------
// IOptionsMonitor<StyloExtractActionOptions> - simple in-memory stub
// ---------------------------------------------------------------------------
internal sealed class StaticOptions : IOptionsMonitor<StyloExtractActionOptions>
{
    private readonly StyloExtractActionOptions _value;

    internal StaticOptions(StyloExtractActionOptions? value = null)
    {
        _value = value ?? new StyloExtractActionOptions();
    }

    public StyloExtractActionOptions CurrentValue => _value;
    public StyloExtractActionOptions Get(string? name) => _value;
    public IDisposable? OnChange(Action<StyloExtractActionOptions, string?> listener) => null;
}

// ---------------------------------------------------------------------------
// HttpContext helpers
// ---------------------------------------------------------------------------
internal static class HttpContextBuilder
{
    /// <summary>
    /// Creates an HttpContext pre-loaded with HTML that simulates what a downstream
    /// handler writes. The response body starts as an empty MemoryStream; tests can
    /// simulate downstream writes by calling WriteHtmlAsync before invoking policies.
    /// </summary>
    internal static DefaultHttpContext CreateHtmlContext(
        string? queryString = null)
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = 200;

        if (queryString is not null)
            context.Request.QueryString = new QueryString("?" + queryString);

        context.Response.Body = new MemoryStream();
        return context;
    }

    internal static DefaultHttpContext CreateJsonContext()
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 200;
        context.Response.Body = new MemoryStream();
        return context;
    }

    internal static DefaultHttpContext CreateStatusContext(int status)
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = status;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Writes HTML into the current response body stream (simulates downstream).</summary>
    internal static async Task WriteHtmlAsync(HttpContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        await context.Response.Body.WriteAsync(bytes);
    }
}

// ---------------------------------------------------------------------------
// Shared policy factory helpers
// ---------------------------------------------------------------------------
internal static class PolicyFactory
{
    internal static ExtractMarkdownActionPolicy Markdown(
        ILayoutExtractor? extractor = null,
        StyloExtractActionOptions? opts = null)
        => new(
            extractor ?? new FakeExtractor(),
            new StaticOptions(opts),
            NullLogger<ExtractMarkdownActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter());

    internal static ExtractHeadersActionPolicy Headers(
        ILayoutExtractor? extractor = null,
        StyloExtractActionOptions? opts = null)
        => new(
            extractor ?? new FakeExtractor(),
            new StaticOptions(opts),
            NullLogger<ExtractHeadersActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter());

    internal static ExtractSidecarActionPolicy Sidecar(StyloExtractActionOptions? opts = null)
        => new(
            new StaticOptions(opts),
            NullLogger<ExtractSidecarActionPolicy>.Instance);
}

// ---------------------------------------------------------------------------
// Simulate the StyloBot middleware pattern: install interceptor then write HTML
// ---------------------------------------------------------------------------
internal static class ActionPolicyRunner
{
    /// <summary>
    /// Simulates the StyloBot middleware's call pattern:
    ///   1. Call policy.ExecuteAsync (installs interceptor, returns Allowed).
    ///   2. Write HTML to the response body (simulates downstream handler).
    ///   3. Flush the interceptor (triggers transformation).
    ///
    /// Returns the body bytes available on the original MemoryStream after flush.
    /// </summary>
    internal static async Task<(string Body, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)>
        RunAndFlushAsync(
            DefaultHttpContext context,
            Func<DefaultHttpContext, Task<Mostlylucid.BotDetection.Actions.ActionResult>> executePolicy,
            string downstreamHtml,
            MemoryStream originalBody)
    {
        var result = await executePolicy(context);

        // Simulate downstream writing HTML to the (possibly swapped) body.
        await HttpContextBuilder.WriteHtmlAsync(context, downstreamHtml);

        // Flush triggers the interceptor transform.
        await context.Response.Body.FlushAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var bodyText = Encoding.UTF8.GetString(originalBody.ToArray());
        return (bodyText, context.Response.Headers);
    }
}
