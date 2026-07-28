using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Middleware;

/// <summary>Explicit, opt-in Markdown test route for a public response representation.</summary>
public sealed class MarkdownQueryOverrideMiddleware
{
    private readonly RequestDelegate _next;
    public MarkdownQueryOverrideMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IActionPolicyRegistry actions,
        IOptionsFactory<StyloExtractActionOptions> optionsFactory)
    {
        var options = optionsFactory.Create("extract-markdown");
        if (!options.EnableQueryOverride ||
            !HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Query.TryGetValue(options.QueryParamName, out var value) ||
            !string.Equals(value.ToString(), options.QueryParamValue, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var policy = actions.GetPolicy("extract-markdown");
        if (policy is null)
        {
            await _next(context);
            return;
        }

        context.Items["StyloExtract.MarkdownOverride"] = true;
        var result = await policy.ExecuteAsync(context, new AggregatedEvidence
        {
            BotProbability = 0,
            Confidence = 0,
            RiskBand = RiskBand.VeryLow
        }, context.RequestAborted);
        if (result.Continue) await _next(context);
    }
}
