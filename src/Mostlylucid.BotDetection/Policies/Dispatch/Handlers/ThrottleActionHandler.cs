using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Throttle"/> via the shared
///     <see cref="ITokenBucketStore"/> -- the same primitive that backs
///     <see cref="RateLimitActionHandler"/>. Unlike RateLimit (per-visitor
///     bucket), Throttle's bucket is keyed by the rule's
///     <see cref="PolicyScope.ToStableKey"/>: every request matching the
///     same scope competes for the same tokens, so a single armed rule
///     globally caps throughput on its scope (a domain, an endpoint, a
///     country, etc.).
///
///     <para>
///         The bucket key is namespaced with the <see cref="BucketPolicyPrefix"/>
///         (<c>"throttle:"</c>) so a RateLimit and a Throttle rule can never
///         collide on the same underlying ITokenBucketStore key value, even
///         if their other inputs happen to overlap.
///     </para>
///
///     <para>
///         <see cref="ITokenBucketStore.TryConsume"/> takes refill rate in
///         requests-per-MINUTE; Throttle is authored in requests-per-SECOND,
///         so this handler does the *60 conversion at the call site. Bucket
///         capacity equals the per-second rate (the burst allowance) -- the
///         steady-state cap is the refill rate.
///     </para>
///
///     <para>
///         On admit: returns <see cref="PolicyDispatchResult.FallThrough"/>
///         so the legacy enforcement path runs unchanged. On denial: writes
///         429 + <c>Retry-After: 1</c> + <c>X-Stylobot-Policy: rule-throttle
///         (rule-id)</c> and returns <see cref="PolicyDispatchResult.Handled"/>.
///     </para>
/// </summary>
public sealed class ThrottleActionHandler : IPolicyActionHandler
{
    /// <summary>
    ///     Bucket-policy-name prefix used to keep policy-stack Throttle
    ///     buckets out of every other ITokenBucketStore key space
    ///     (notably <see cref="RateLimitActionHandler.BucketPolicyPrefix"/>).
    /// </summary>
    public const string BucketPolicyPrefix = "policy.throttle:";

    private readonly ITokenBucketStore _store;

    /// <summary>DI constructor. <paramref name="store"/> is required.</summary>
    public ThrottleActionHandler(ITokenBucketStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Throttle);

    /// <inheritdoc />
    public async Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        if (action is not PolicyAction.Throttle throttle) return PolicyDispatchResult.FallThrough;
        if (throttle.RequestsPerSecond <= 0) return PolicyDispatchResult.FallThrough;

        // Stable bucket identity: composite scope stringified. Two rules at
        // the same scope share a bucket, which is intentional -- the operator
        // can arm "throttle posts on x.com" once and every armed copy of
        // that rule contends for the same tokens.
        var scopeKey = rule.Scope.ToStableKey();

        // Capacity = the per-second rate (burst allowance == steady-state
        // rate, matching the legacy registry's behaviour). Refill rate
        // converted to per-minute for the store contract.
        var rps = throttle.RequestsPerSecond;
        var rpm = rps * 60;
        var admitted = _store.TryConsume(
            BucketPolicyPrefix + rule.Id.ToString("N"),
            scopeKey,
            capacity: rps,
            refillRatePerMinute: rpm);

        if (admitted) return PolicyDispatchResult.FallThrough;

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = "1";
        context.Response.Headers[BlockActionHandler.PolicyHeader] = $"rule-throttle ({rule.Id})";
        context.Response.ContentType = "application/json";

        await context.Response
            .WriteAsync(
                $$"""{"error":"Too many requests","retryAfter":1,"policy":"{{rule.Id}}"}""",
                ct)
            .ConfigureAwait(false);
        return PolicyDispatchResult.Handled;
    }
}
