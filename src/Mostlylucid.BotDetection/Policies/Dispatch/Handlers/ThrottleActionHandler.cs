using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Throttle;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Throttle"/> via the Phase G
///     <see cref="ThrottleBucketRegistry"/>. Unlike
///     <see cref="RateLimitActionHandler"/>, Throttle's bucket is keyed by
///     the rule's <see cref="PolicyScope"/> -- every request matching the
///     same scope competes for the same tokens, so a single armed rule
///     globally caps throughput on its scope (a domain, an endpoint, a
///     country, etc.).
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
    private readonly ThrottleBucketRegistry _buckets;
    private readonly TimeProvider _timeProvider;

    /// <summary>DI constructor.</summary>
    public ThrottleActionHandler(
        ThrottleBucketRegistry buckets,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        _buckets = buckets;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        // Stable bucket identity: stringified scope. Two rules at the same
        // scope share a bucket, which is intentional -- the operator can
        // arm "throttle posts on x.com" once and every armed copy of that
        // rule contends for the same tokens.
        var scopeKey = BuildScopeKey(rule.Scope);

        var admitted = _buckets.TryAcquire(scopeKey, throttle.RequestsPerSecond, _timeProvider.GetUtcNow());
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

    /// <summary>
    ///     Stable, lossless string projection of a <see cref="PolicyScope"/>
    ///     for use as a bucket key. The shape doesn't need to round-trip --
    ///     two scopes that are structurally equal MUST produce identical
    ///     strings, and that's all <see cref="ThrottleBucketRegistry"/>
    ///     requires.
    /// </summary>
    private static string BuildScopeKey(PolicyScope scope)
    {
        var host = scope.Host switch
        {
            HostScope.Endpoint e => $"endpoint:{e.DomainName}|{e.SubdomainName}|{e.PathTemplate}",
            HostScope.Subdomain s => $"subdomain:{s.DomainName}|{s.SubdomainName}",
            HostScope.Domain d => $"domain:{d.Name}",
            _ => "wildcard"
        };
        var method = scope.Method ?? "*";
        var geo = scope.Geo ?? "*";
        var identity = scope.Identity switch
        {
            IdentityScope.NamedBot n => $"namedbot:{n.Family}",
            IdentityScope.BotType b => $"bottype:{b.Category}",
            IdentityScope.HumanBrowser h => $"human:{h.Family}",
            IdentityScope.Fingerprint f => $"fp:{f.Id}",
            _ => "*"
        };
        return $"{host}::{method}::{geo}::{identity}";
    }
}
