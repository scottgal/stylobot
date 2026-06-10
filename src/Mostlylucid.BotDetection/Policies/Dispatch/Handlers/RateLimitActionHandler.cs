using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.RateLimit"/> by funnelling the
///     matched request through the FOSS
///     <see cref="ITokenBucketStore"/>. Unlike
///     <see cref="ThrottleActionHandler"/>, RateLimit is per-key (per
///     fingerprint by default; per remote IP when no fingerprint is
///     attached) rather than per-rule-scope. Two requests on the same rule
///     but different visitors compete for separate buckets, so one
///     misbehaving visitor cannot exhaust the budget for everyone.
///
///     <para>
///         Bucket capacity AND refill rate both default to the rule's
///         <see cref="PolicyAction.RateLimit.RequestsPerMinute"/>. The bucket
///         starts full so the first request through is always admitted; the
///         visitor's RPM is then bounded by the refill rate.
///     </para>
///
///     <para>
///         On admit: returns <see cref="PolicyDispatchResult.FallThrough"/>
///         so the legacy enforcement path runs unchanged. On exhaustion:
///         writes 429 + <c>Retry-After: 1</c> + <c>X-Stylobot-Policy:
///         rule-rate-limit (rule-id)</c> and returns
///         <see cref="PolicyDispatchResult.Handled"/>.
///     </para>
///
///     <para>
///         The <see cref="ITokenBucketStore"/> dependency is OPTIONAL
///         (per <c>feedback_remote_mode_optional_di</c>): hosts that never
///         registered the FOSS rate-limit primitives skip the rate-limit
///         action and fall through with a structured-log breadcrumb.
///     </para>
/// </summary>
public sealed class RateLimitActionHandler : IPolicyActionHandler
{
    /// <summary>Bucket-policy-name prefix used to keep policy-stack RateLimit buckets out of the legacy rate-limit registry's key space.</summary>
    public const string BucketPolicyPrefix = "policy.ratelimit:";

    private readonly ITokenBucketStore? _store;

    /// <summary>
    ///     DI constructor. <paramref name="store"/> nullable so hosts that
    ///     never registered the FOSS rate-limit primitives still boot.
    /// </summary>
    public RateLimitActionHandler(ITokenBucketStore? store = null)
    {
        _store = store;
    }

    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.RateLimit);

    /// <inheritdoc />
    public async Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        if (action is not PolicyAction.RateLimit rl) return PolicyDispatchResult.FallThrough;
        if (rl.RequestsPerMinute <= 0) return PolicyDispatchResult.FallThrough;
        if (_store is null) return PolicyDispatchResult.FallThrough;

        var key = ResolveVisitorKey(context);
        var policyName = BucketPolicyPrefix + rule.Id.ToString("N");

        // Capacity = burst allowance; refill = steady-state RPM. Setting both
        // to the same value mirrors the legacy RateLimit policy default.
        var admitted = _store.TryConsume(
            policyName,
            key,
            capacity: rl.RequestsPerMinute,
            refillRatePerMinute: rl.RequestsPerMinute);

        if (admitted) return PolicyDispatchResult.FallThrough;

        // Bucket exhausted -- shape a 429.
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = "1";
        context.Response.Headers[BlockActionHandler.PolicyHeader] = $"rule-rate-limit ({rule.Id})";
        context.Response.ContentType = "application/json";

        await context.Response
            .WriteAsync(
                $$"""{"error":"Too many requests","retryAfter":1,"policy":"{{rule.Id}}"}""",
                ct)
            .ConfigureAwait(false);
        return PolicyDispatchResult.Handled;
    }

    /// <summary>
    ///     Pick a stable per-visitor key for the bucket. Prefer the
    ///     fingerprint (canonical FOSS visitor identity); fall back to the
    ///     remote IP; fall back to a literal <c>"anon"</c> so the bucket is
    ///     still selected (degenerate case -- effectively a per-rule global
    ///     cap if every visitor lands on the same key).
    /// </summary>
    private static string ResolveVisitorKey(HttpContext context)
    {
        // Prefer fingerprint when the dispatcher stashed the signal bag.
        if (context.Items.TryGetValue(PolicyActionDispatcher.RequestSignalsItemKey, out var raw)
            && raw is IReadOnlyDictionary<string, object?> signals
            && signals.TryGetValue("signature.primary", out var sig)
            && sig is string s
            && !string.IsNullOrEmpty(s))
            return s;
        var ip = context.Connection?.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "anon" : ip!;
    }
}
