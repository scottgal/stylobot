using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Wires <see cref="DomainEntitlementValidator"/> into the request pipeline. For every
///     request we read the effective host (X-Forwarded-Host wins over Host so that anything
///     behind a reverse proxy still sees the customer-facing hostname), classify it, and
///     stash the result on <see cref="HttpContext.Items"/> for the dashboard to surface.
///
///     <para>
///     <b>Warn-never-lock:</b> this middleware never short-circuits the request, never alters
///     the response, never calls <c>next</c> with a different status code. A mismatch only
///     bumps a counter and writes a signal; the dashboard's license card translates that into
///     a banner. See <c>stylobot-commercial/docs/licensing-simplified.md §enforcement-behavior</c>.
///     </para>
///
///     <para>
///     <b>Pipeline placement:</b> register before <see cref="Middleware.BotDetectionMiddleware"/>
///     so the signals are available to detectors that want to read them - but the order isn't
///     load-bearing, since this middleware is purely additive.
///     </para>
/// </summary>
public sealed class DomainEntitlementMiddleware
{
    /// <summary>HttpContext.Items key carrying the most recent <see cref="DomainEntitlementResult"/>.</summary>
    public const string ResultItemsKey = "BotDetection.License.DomainResult";

    /// <summary>HttpContext.Items key carrying the normalized host inspected for the request.</summary>
    public const string HostItemsKey = "BotDetection.License.Host";

    private readonly RequestDelegate _next;
    private readonly DomainEntitlementValidator _validator;
    private readonly ILogger<DomainEntitlementMiddleware> _logger;

    public DomainEntitlementMiddleware(
        RequestDelegate next,
        DomainEntitlementValidator validator,
        ILogger<DomainEntitlementMiddleware> logger)
    {
        _next = next;
        _validator = validator;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Pass-through fast path - skip the host lookup entirely when no license is configured.
        if (!_validator.IsEnforcing)
        {
            await _next(context);
            return;
        }

        var host = ResolveEffectiveHost(context);
        var result = _validator.Check(host);

        // Always stash the host + result for the dashboard's license card and for downstream
        // signal-emitting code paths. Both keys are safe to read even on the OSS path because
        // they're plain strings/enums, not PII (host is already a public DNS name).
        context.Items[HostItemsKey] = host;
        context.Items[ResultItemsKey] = result;

        if (result is DomainEntitlementResult.Mismatch or DomainEntitlementResult.MismatchCloudPool)
        {
            // Info-level: a mismatch is a configuration drift signal, not an error. Operators
            // see it in logs alongside the dashboard banner. We do NOT log per-request - the
            // validator's internal counter rolls up the volume, and we only log on transitions
            // that are interesting (cloud-pool especially).
            if (result == DomainEntitlementResult.MismatchCloudPool)
            {
                _logger.LogInformation(
                    "License domain mismatch (cloud-pool host): request host {Host} is a shared platform suffix; license covers {Allowed}",
                    host,
                    string.Join(", ", _validator.GetStatistics().AllowedDomains));
            }
        }

        await _next(context);
    }

    /// <summary>
    ///     Pick the host we should validate against. Order of preference:
    ///     <list type="number">
    ///       <item><description><c>X-Forwarded-Host</c> first value - what the customer's edge
    ///       (Caddy, Cloudflare, AWS ALB) saw before the request hit our gateway.</description></item>
    ///       <item><description><c>Host</c> header - what this server saw directly.</description></item>
    ///     </list>
    ///     We trust X-Forwarded-Host blindly here for the same reason the rest of StyloBot does:
    ///     this is observation/telemetry, not authorization. Worst case a misbehaving client
    ///     spoofs their host into a licensed domain - they still get detected normally, the
    ///     license counter just doesn't tick.
    /// </summary>
    private static string? ResolveEffectiveHost(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-Host", out var fwd))
        {
            var first = fwd.ToString();
            // Header may carry a comma-separated proxy chain (most-recent first).
            var comma = first.IndexOf(',');
            if (comma > 0) first = first[..comma];
            first = first.Trim();
            if (first.Length > 0) return first;
        }

        return context.Request.Host.HasValue ? context.Request.Host.Host : null;
    }
}

/// <summary>
///     DI + pipeline extensions for <see cref="DomainEntitlementMiddleware"/>.
/// </summary>
public static class DomainEntitlementExtensions
{
    /// <summary>
    ///     Register the singleton <see cref="DomainEntitlementValidator"/> populated from
    ///     <c>BotDetection:Licensing:Domains</c>. Idempotent - safe to call from multiple
    ///     extension methods. The validator is a no-op when no domains are configured.
    /// </summary>
    public static IServiceCollection AddDomainEntitlement(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            return new DomainEntitlementValidator(opts.Licensing?.Domains);
        });
        return services;
    }

    /// <summary>
    ///     Mount the warn-never-lock domain entitlement middleware. Safe to call on installs
    ///     with no license configured - the middleware fast-paths to <c>next</c> in that case.
    ///     Place this before <see cref="Middleware.BotDetectionMiddlewareExtensions.UseBotDetection"/>
    ///     so license signals are populated before detectors run.
    /// </summary>
    public static IApplicationBuilder UseDomainEntitlement(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<DomainEntitlementMiddleware>();
    }
}