using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Token-bucket rate-limit action policy. Allows the request when the
///     bucket has capacity, otherwise hands off to a composable
///     <see cref="RateLimitActionOptions.OverLimitAction"/> -- typically
///     <c>throttle-status</c> for friendly bots or <c>block-soft</c> for
///     aggressive AI scrapers.
/// </summary>
/// <remarks>
///     <para>
///         Phase 2 of the policy-grammar work
///         (docs/plans/2026-05-24-policy-grammar-core-experience.md).
///         The four built-in instances --
///         <c>rate-limit-search</c>, <c>rate-limit-ai</c>,
///         <c>rate-limit-social</c>, <c>rate-limit-monitoring</c> -- are
///         registered alongside the existing throttle/block/challenge
///         policies but aren't wired into the per-BotType defaults yet.
///         The default flip lands in phase 3.
///     </para>
///     <para>
///         <see cref="ActionType"/> reports <see cref="ActionType.Throttle"/>
///         (the closest existing fit), but <see cref="Intent"/> reports
///         <see cref="PolicyIntent.RateLimit"/> so the dashboard chip and
///         per-BotType mapping can distinguish capacity-based limits from
///         delay-based throttles.
///     </para>
/// </remarks>
public sealed class RateLimitActionPolicy : IActionPolicy
{
    private readonly RateLimitActionOptions _options;
    private readonly ITokenBucketStore _store;
    private readonly IActionPolicyRegistry _registry;
    private readonly IAdaptiveScalingTracker? _adaptiveScaling;
    private readonly ILogger<RateLimitActionPolicy>? _logger;

    public RateLimitActionPolicy(
        string name,
        RateLimitActionOptions options,
        ITokenBucketStore store,
        IActionPolicyRegistry registry,
        IAdaptiveScalingTracker? adaptiveScaling = null,
        ILogger<RateLimitActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adaptiveScaling = adaptiveScaling;
        _logger = logger;
    }

    /// <summary>
    ///     Live read of the active adaptive-scaling multiplier (phase 4).
    ///     1.0 when no tracker is wired or the origin is healthy. Surfaced
    ///     to the dashboard via the policy-state provider.
    /// </summary>
    public double CurrentMultiplier => _adaptiveScaling?.CurrentMultiplier ?? 1.0;

    /// <summary>
    ///     Current degradation tier name (e.g. <c>nominal</c> / <c>degraded</c>
    ///     / <c>critical</c>) or <c>null</c> when adaptive scaling is off.
    /// </summary>
    public string? CurrentTier => _adaptiveScaling?.CurrentTier;

    public string Name { get; }
    public ActionType ActionType => ActionType.Throttle;
    public PolicyIntent Intent => PolicyIntent.RateLimit;

    /// <summary>
    ///     Read-only access to the configured options. Used by the dashboard
    ///     state provider to surface RPS / burst / OverLimitAction.
    /// </summary>
    public RateLimitActionOptions Options => _options;

    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var key = ExtractKey(context, evidence);
        if (key is null)
        {
            // No stable identity to bill against -- fall through cleanly.
            _logger?.LogDebug(
                "RateLimit '{Policy}': no {KeyBy} key on request to {Path}, allowing",
                Name, _options.KeyBy, context.Request.Path);
            return ActionResult.Allowed($"rate-limit:no-key:{_options.KeyBy}");
        }

        // Adaptive scaling: when the upstream is unhealthy, scale down the
        // effective bot allowance so humans (who don't traverse rate limits)
        // get more headroom. Multiplier defaults to 1.0 when no tracker is
        // wired (e.g. tests) or the origin is healthy.
        var multiplier = _adaptiveScaling?.CurrentMultiplier ?? 1.0;
        var effectiveRpm = Math.Max(1, (int)Math.Round(_options.RequestsPerMinute * multiplier));
        var allowed = _store.TryConsume(Name, key, _options.BurstSize, effectiveRpm);
        if (allowed)
        {
            if (_options.IncludeHeaders)
            {
                context.Response.Headers.TryAdd("X-RateLimit-Policy", Name);
                context.Response.Headers.TryAdd("X-RateLimit-Limit",
                    effectiveRpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (multiplier < 0.99 && _adaptiveScaling is not null)
                {
                    // Help operators trace why the effective limit is below
                    // the configured RequestsPerMinute.
                    context.Response.Headers.TryAdd("X-RateLimit-Tier", _adaptiveScaling.CurrentTier ?? "unknown");
                    context.Response.Headers.TryAdd("X-RateLimit-Multiplier",
                        multiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return ActionResult.Allowed($"rate-limit:{Name}:passed");
        }

        // Bucket empty -- delegate to OverLimitAction (composable).
        var fallback = _registry.GetPolicy(_options.OverLimitAction);
        if (fallback is null)
        {
            _logger?.LogWarning(
                "RateLimit '{Policy}': OverLimitAction '{Over}' is not registered, falling back to 429",
                Name, _options.OverLimitAction);
            context.Response.StatusCode = 429;
            context.Response.Headers.TryAdd("Retry-After", "60");
            return ActionResult.Blocked(429,
                $"rate-limit:{Name}:over-limit (no fallback '{_options.OverLimitAction}')");
        }

        _logger?.LogDebug(
            "RateLimit '{Policy}': over limit for key, delegating to {Over}",
            Name, _options.OverLimitAction);
        return await fallback.ExecuteAsync(context, evidence, cancellationToken);
    }

    /// <summary>
    ///     Pull the rate-limit key off the request. Signature mode reads
    ///     <see cref="SignalKeys.PrimarySignature"/> from the aggregated
    ///     evidence; IP mode reads <see cref="HttpContext.Connection"/>'s
    ///     remote IP. Returns <c>null</c> when neither source is available
    ///     (the policy treats that as "allow" rather than risk a global
    ///     bucket).
    /// </summary>
    private string? ExtractKey(HttpContext context, AggregatedEvidence evidence) => _options.KeyBy switch
    {
        RateLimitKey.Signature => evidence.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sig)
                                  && sig is string s && s.Length > 0
            ? s
            : null,
        RateLimitKey.Ip => context.Connection.RemoteIpAddress?.ToString(),
        _ => null,
    };
}
