using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Single entry point that ties the rate-limit primitives together.
///     Called by <c>DetectionPolicyMiddleware</c> (phase 5) once it has
///     matched a request to the applicable rules. Avoids a new layer of
///     IActionPolicy registrations for composite-subject rules -- the
///     existing per-name <c>RateLimitActionPolicy</c> still covers the
///     simple single-subject case for back-compat with the policy-grammar
///     plan's phase-2 work.
///     <para>
///     The enforcer:
///       1. Resolves the request's subject set.
///       2. For each applicable rule whose declared subjects are ALL
///          present in the request set, attempts to acquire a request-rate
///          token.
///       3. If any bucket is empty, dispatches the rule's
///          <see cref="RateLimitRule.OverLimitAction"/> through the
///          existing <see cref="IActionPolicyRegistry"/> and short-circuits.
///       4. Otherwise, wraps the response body with the most-restrictive
///          <see cref="RateLimitDataRate"/> across the matched rules so a
///          composite "scraper AND US" rule's bandwidth cap takes effect
///          even if it's tighter than the single-subject rule's cap.
///     </para>
/// </summary>
public sealed class RateLimitEnforcer
{
    private readonly IRateLimiter _rateLimiter;
    private readonly IDataRateLimiter _dataRateLimiter;
    private readonly ScopedRateLimitResolver _scopes;
    private readonly RateLimitSubjectResolver _subjects;

    public RateLimitEnforcer(
        IRateLimiter rateLimiter,
        IDataRateLimiter dataRateLimiter,
        ScopedRateLimitResolver scopes,
        RateLimitSubjectResolver subjects)
    {
        _rateLimiter = rateLimiter;
        _dataRateLimiter = dataRateLimiter;
        _scopes = scopes;
        _subjects = subjects;
    }

    /// <summary>
    ///     Apply rate-limit rules to the request. Returns a result the
    ///     caller acts on: continue with the (possibly wrapped) response,
    ///     or short-circuit because an over-limit OverLimitAction took
    ///     over.
    /// </summary>
    public async Task<RateLimitOutcome> ApplyAsync(
        HttpContext context,
        DetectionFacts detection,
        IActionPolicyRegistry actionRegistry,
        Mostlylucid.BotDetection.Orchestration.AggregatedEvidence evidence,
        CancellationToken ct = default)
    {
        // Internal-class exemption (2026-08-16, the compose-path 429 incident): the
        // product's OWN traffic — LAN-trusted peers, health probes, the hub/beacon
        // plumbing, the site's own data path (the compose key's requests classify
        // Internal by peer trust) — is operator-owned, never threat-throttled. Skip
        // the rule resolution + the data-rate wrap entirely; an external caller can
        // never classify Internal (peer-verified trust or the internal-plumbing paths
        // only), so the exemption is precisely the operator's own surface.
        if (evidence.PrimaryBotType == BotType.Internal)
        {
            return RateLimitOutcome.Allowed;
        }

        var requestSubjects = _subjects.Resolve(context, detection);
        var rules = _scopes.ResolveRules(
            context.Request.Host.Host,
            context.Request.Path.Value ?? "/",
            context.Request.Method);

        long? tightestBytesPerSecond = null;
        IReadOnlyList<RateLimitSubject>? tightestDataRateSubjects = null;

        foreach (var rule in rules)
        {
            var ruleSubjects = MatchSubjects(rule, requestSubjects);
            if (ruleSubjects is null) continue;        // rule's predicates not satisfied

            var config = new RateLimitConfig(
                rule.RequestRate.RequestsPerMinute,
                rule.RequestRate.BurstSize);

            if (!_rateLimiter.TryAcquire(ruleSubjects, config))
            {
                // Over-limit: dispatch the rule's OverLimitAction. Returns
                // short-circuit so the pipeline doesn't continue past us.
                var fallback = actionRegistry.GetPolicy(rule.OverLimitAction);
                if (fallback is null)
                {
                    context.Response.StatusCode = 429;
                    // Closed-loop feedback gate: stylobot-synthesised response.
                    context.MarkResponseFromStyloBot();
                    context.Response.Headers.TryAdd("Retry-After", "60");
                    return RateLimitOutcome.Blocked($"rate-limit:{rule.OverLimitAction}:not-registered");
                }
                await fallback.ExecuteAsync(context, evidence, ct).ConfigureAwait(false);
                return RateLimitOutcome.Blocked($"rate-limit:over-limit:{rule.OverLimitAction}");
            }

            // Bucket had room. If this rule has a data-rate cap, remember the
            // TIGHTEST one across matched rules so the wrap below uses the
            // smallest budget the operator declared.
            if (rule.DataRate is { BytesPerSecond: > 0 } dr
                && (tightestBytesPerSecond is null || dr.BytesPerSecond < tightestBytesPerSecond))
            {
                tightestBytesPerSecond = dr.BytesPerSecond;
                tightestDataRateSubjects = ruleSubjects;
            }
        }

        if (tightestBytesPerSecond is long bps && tightestDataRateSubjects is not null)
        {
            context.Response.Body = _dataRateLimiter.WrapResponseStream(
                tightestDataRateSubjects, context.Response.Body, bps);
        }

        return RateLimitOutcome.Allowed;
    }

    /// <summary>
    ///     Returns the matched subject set when every predicate in
    ///     <see cref="RateLimitRule.Subjects"/> finds a corresponding
    ///     <see cref="RateLimitSubject"/> in <paramref name="requestSubjects"/>.
    ///     Returns null when any predicate fails -- the rule doesn't apply.
    ///     <para>
    ///     A subject spec with multiple Values is satisfied when ANY of the
    ///     values matches (OR within a single spec); the spec itself joins
    ///     other specs with AND.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<RateLimitSubject>? MatchSubjects(
        RateLimitRule rule,
        IReadOnlyList<RateLimitSubject> requestSubjects)
    {
        if (rule.Subjects.Count == 0) return Array.Empty<RateLimitSubject>();

        var matched = new List<RateLimitSubject>(rule.Subjects.Count);
        foreach (var spec in rule.Subjects)
        {
            var values = spec.Effective();
            RateLimitSubject? found = null;
            foreach (var subj in requestSubjects)
            {
                if (subj.Kind != spec.Type) continue;
                foreach (var v in values)
                {
                    if (string.Equals(subj.Value, v, StringComparison.OrdinalIgnoreCase))
                    {
                        found = subj;
                        break;
                    }
                }
                if (found is not null) break;
            }
            if (found is null) return null;          // predicate failed -> rule doesn't apply
            matched.Add(found.Value);
        }
        return matched;
    }
}

/// <summary>
///     What the enforcer did. <see cref="Allowed"/> means the pipeline
///     continues (with response body possibly wrapped for data-rate);
///     <see cref="Blocked"/> means the OverLimitAction took over the
///     response and the caller short-circuits.
/// </summary>
public readonly record struct RateLimitOutcome(bool Continue, string? Reason)
{
    public static RateLimitOutcome Allowed { get; } = new(true, null);
    public static RateLimitOutcome Blocked(string reason) => new(false, reason);
}
