using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Short-circuits the request pipeline when the
///     <see cref="HoneypotPathTagger"/> classified the path as
///     <see cref="HoneypotTier.Always"/> -- i.e. a Tier 1 honeypot:
///     a path nothing legitimate ever requests
///     (<c>/.env</c>, <c>/.git/config</c>, <c>/etc/passwd</c>, etc).
/// </summary>
/// <remarks>
///     <para>
///         Pipeline placement: AFTER <see cref="HoneypotPathTagger"/> (so the
///         tag is set) and AFTER <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware"/> (so the
///         detection event is recorded against the dashboard's event store,
///         honeypot panel, and threat aggregator), but BEFORE the YARP
///         reverse-proxy mapping (so the upstream origin never sees the
///         request and can't fingerprint-leak via its own 404 / 403 / 200).
///     </para>
///     <para>
///         Tier 2 paths (<c>/wp-admin</c>, <c>/actuator</c>, etc.) are
///         intentionally NOT terminated here -- they are legitimate on
///         some stacks and the operator may have exempt-listed them via
///         <see cref="HoneypotDetectionOptions.ExemptPaths"/> or a per-site
///         <c>framework_paths</c> profile. The tagger already returned
///         <see cref="HoneypotTier.None"/> for any Tier 2 path the operator
///         exempted.
///     </para>
///     <para>
///         Why a separate middleware instead of folding into the tagger:
///         the dashboard panel + threat aggregator both consume the tag,
///         and they get fed by the detection event store -- the
///         <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware"/> needs to RUN against the
///         tagged request to write that event. Terminating in the tagger
///         would silence the dashboard ("0 honeypot hits") even when the
///         scanner is hammering us. Terminating AFTER detection keeps the
///         event book correct AND keeps the upstream silent.
///     </para>
/// </remarks>
public sealed class HoneypotPathTerminator
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;

    public HoneypotPathTerminator(
        RequestDelegate next,
        IOptionsMonitor<HoneypotDetectionOptions> options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_options.CurrentValue.Enabled) return _next(context);

        // Only Tier 1 (Always) terminates. Tier 2 falls through to the rest
        // of the pipeline; the operator's existing detection-policy chain or
        // EndpointPolicy rules decide what to do with it.
        if (context.Items.TryGetValue(HoneypotPathTagger.ItemKeyTier, out var tierObj)
            && tierObj is HoneypotTier.Always)
        {
            // 404 with no body and no content-type matches the "this path
            // does not exist" shape an unconfigured Kestrel returns -- the
            // scanner can't distinguish a terminated honeypot from a
            // legitimately-missing path, so it can't fingerprint the
            // gateway by comparing honeypot hits to true-404 hits.
            //
            // NOT 403: a 403 leaks "this path is special / guarded" and
            // scanners follow up. NOT 200: would still hand the scanner
            // a "this app exists" confirmation.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentLength = 0;
            return Task.CompletedTask;
        }

        return _next(context);
    }
}

public static class HoneypotPathTerminatorExtensions
{
    /// <summary>
    ///     Registers the <see cref="HoneypotPathTerminator"/> in the
    ///     request pipeline. Call after <c>UseBotDetection()</c> and
    ///     before <c>MapReverseProxy()</c> so detection events still
    ///     write through but upstream never sees a Tier 1 request.
    /// </summary>
    public static IApplicationBuilder UseHoneypotTermination(this IApplicationBuilder builder)
        => builder.UseMiddleware<HoneypotPathTerminator>();
}