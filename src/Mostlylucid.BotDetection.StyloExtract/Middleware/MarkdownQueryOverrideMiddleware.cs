using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Middleware;

/// <summary>Explicit, opt-in Markdown test route for a public response representation.</summary>
public sealed class MarkdownQueryOverrideMiddleware
{
    /// <summary>
    ///     <see cref="HttpContext.Items"/> marker set when this middleware routes a request into the
    ///     Markdown policy. <see cref="Actions.ExtractMarkdownCacheAiActionPolicy"/> reads it to honour
    ///     the override regardless of bot-type classification, and
    ///     <see cref="Actions.ContentCacheActionPolicyBase"/> counts the request as an override in
    ///     telemetry so test traffic is separately labelled from real AI-scraper traffic.
    /// </summary>
    public const string MarkerKey = "StyloExtract.MarkdownOverride";

    private const string PolicyName = "extract-markdown-cache-ai";

    private readonly RequestDelegate _next;
    public MarkdownQueryOverrideMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IActionPolicyRegistry actions,
        IOptionsFactory<StyloExtractActionOptions> optionsFactory)
    {
        // Named options for the Markdown policy; the defaults are the spec's ?markdown=true
        // test action (QueryParamName="markdown", QueryParamValue="true").
        var options = optionsFactory.Create(PolicyName);
        if (!options.EnableQueryOverride ||
            !HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Query.TryGetValue(options.QueryParamName, out var value) ||
            !string.Equals(value.ToString(), options.QueryParamValue, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var policy = actions.GetPolicy(PolicyName);
        if (policy is null)
        {
            // Pack not registered — the override cannot be honoured; fail open to origin.
            await _next(context);
            return;
        }

        context.Items[MarkerKey] = true;
        var result = await policy.ExecuteAsync(context, new AggregatedEvidence
        {
            BotProbability = 0,
            Confidence = 0,
            RiskBand = RiskBand.VeryLow
        }, context.RequestAborted);
        if (result.Continue) await _next(context);
    }
}
