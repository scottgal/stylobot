using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Action policy that handles a honeypot hit: applies a randomised
///     rate-limit delay (so an attacker can't profile our detection by
///     spotting a fixed response time) then serves a fake response.
/// </summary>
/// <remarks>
///     <para>
///         Selected automatically by <c>BotDetectionMiddleware</c> when the
///         pre-detection <see cref="HoneypotPathTagger"/> set a tier tag on
///         <see cref="HttpContext.Items"/>. Has policy name
///         <c>"honeypot-response"</c>; operators can also reference it from
///         their own transition rules.
///     </para>
///     <para>
///         Response selection:
///         <list type="number">
///             <item>If a <see cref="ISimulationPackRegistry"/> has a matching pack/CVE path, delegate to <see cref="SimulationPackResponder"/> -- gives a realistic body keyed on the framework being faked.</item>
///             <item>Otherwise write a minimal generic 404 with a plausible body for the path category.</item>
///         </list>
///     </para>
/// </remarks>
public sealed class HoneypotResponseActionPolicy : IActionPolicy
{
    public const string PolicyName = "honeypot-response";

    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;
    private readonly SimulationPackResponder _packResponder;
    private readonly ILogger<HoneypotResponseActionPolicy> _logger;

    public HoneypotResponseActionPolicy(
        IOptionsMonitor<HoneypotDetectionOptions> options,
        SimulationPackResponder packResponder,
        ILogger<HoneypotResponseActionPolicy> logger)
    {
        _options = options;
        _packResponder = packResponder;
        _logger = logger;
    }

    public string Name => PolicyName;

    public ActionType ActionType => ActionType.Custom;

    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var rate = _options.CurrentValue.RateLimit;

        // Apply randomised jitter delay BEFORE any response work so the
        // attacker's first observation is the timing curve. Without jitter
        // every probe gets the same delay -> obvious detection.
        if (rate.Enabled)
        {
            var min = Math.Max(0, rate.BaseDelayMs - rate.JitterMs);
            var max = Math.Max(min + 1, rate.BaseDelayMs + rate.JitterMs);
            var delay = Random.Shared.Next(min, max);
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return ActionResult.Blocked(499, "Client closed request during honeypot delay");
                }
            }
        }

        // Try the simulation-pack responder first -- it serves a realistic,
        // framework-specific fake response when a pack matches.
        var packResult = await _packResponder.ExecuteAsync(context, evidence, cancellationToken);
        if (!packResult.Continue) return packResult;

        // No pack matched -- write a generic minimal 404 with realistic content.
        var path = context.Request.Path.Value ?? "/";
        await WriteGenericFakeResponseAsync(context, path, cancellationToken);

        _logger.LogInformation(
            "Honeypot fake response served for {Path} ({Tier})",
            path,
            context.Items[HoneypotPathTagger.ItemKeyTier] ?? "unknown");

        return ActionResult.Blocked(context.Response.StatusCode, "Honeypot fake response served");
    }

    private static async Task WriteGenericFakeResponseAsync(HttpContext context, string path, CancellationToken ct)
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.TryAdd("X-StyloBot-Honeypot", "true");
        context.Response.Headers.TryAdd("Cache-Control", "no-store");

        const string body = """
            <!DOCTYPE html>
            <html><head><title>404 Not Found</title></head>
            <body><h1>Not Found</h1><p>The requested URL was not found on this server.</p></body></html>
            """;

        context.Response.ContentLength = body.Length;
        await context.Response.WriteAsync(body, ct);
    }
}
