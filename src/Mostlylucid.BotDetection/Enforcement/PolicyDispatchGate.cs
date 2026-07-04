using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
///     Post-detection policy-stack dispatch. Extracted verbatim from
///     <see cref="BotDetectionMiddleware"/> so the atom-orchestrator path
///     can dispatch the same policy stack without duplicating the shape
///     construction (<see cref="PolicyScope"/> + per-request signal bag)
///     or the try/catch fault-suppression contract.
/// </summary>
/// <remarks>
///     <para>
///         Bridges the new PolicyAction record family (Allow / Block /
///         Observe / Tag / Challenge / RateLimit / Throttle) to the HTTP
///         response BEFORE the legacy DetectionPolicyAction enum path runs.
///         When <see cref="PolicyActionDispatcher"/> returns
///         <see cref="PolicyDispatchResult.Handled"/>, the caller must
///         short-circuit (the dispatcher has already shaped the response);
///         when it returns <see cref="PolicyDispatchResult.FallThrough"/>,
///         the caller runs the remainder of the enforcement pipeline
///         unchanged.
///     </para>
///     <para>
///         Optional dependency: hosts that didn't register a
///         <see cref="PolicyActionDispatcher"/> see the gate short-circuit
///         to <see cref="PolicyDispatchResult.FallThrough"/> without ever
///         calling into the dispatcher, matching legacy behaviour.
///     </para>
/// </remarks>
public sealed class PolicyDispatchGate
{
    private readonly PolicyActionDispatcher? _dispatcher;
    private readonly ILogger<PolicyDispatchGate> _logger;

    public PolicyDispatchGate(
        ILogger<PolicyDispatchGate> logger,
        PolicyActionDispatcher? dispatcher = null)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    /// <summary>
    ///     Build the request scope + signal bag from
    ///     <paramref name="evidence"/> and hand off to
    ///     <see cref="PolicyActionDispatcher.DispatchAsync"/>. Swallows
    ///     non-cancellation faults so a dispatcher fault can never take
    ///     down detection -- matches the legacy try/catch in
    ///     <see cref="BotDetectionMiddleware"/>.
    /// </summary>
    public async Task<PolicyDispatchResult> EvaluateAsync(
        HttpContext context,
        AggregatedEvidence evidence)
    {
        if (_dispatcher is null)
            return PolicyDispatchResult.FallThrough;

        try
        {
            var scope = BuildPolicyRequestScope(context);
            var signals = BuildPolicyRequestSignals(context, evidence);
            return await _dispatcher
                .DispatchAsync(context, scope, signals, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PolicyDispatchGate: dispatcher threw for {Path}; falling through",
                context.Request.Path);
            return PolicyDispatchResult.FallThrough;
        }
    }

    /// <summary>
    ///     Derive the request-shaped <see cref="PolicyScope"/> the resolver
    ///     walks. Host slot is the request's domain / subdomain / path;
    ///     orthogonal slots are populated so the resolver's
    ///     <c>PolicyScopeMatcher</c> can narrow rules with explicit
    ///     Method / Geo / Identity axes.
    /// </summary>
    private static PolicyScope BuildPolicyRequestScope(HttpContext context)
    {
        var hostHeader = context.Request.Host.Host ?? string.Empty;
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var (domain, subdomain) = SplitHost(hostHeader);
        HostScope? hostSlot = string.IsNullOrEmpty(domain)
            ? null
            : new HostScope.Endpoint(domain, subdomain, path);
        var method = string.IsNullOrEmpty(context.Request.Method)
            ? null
            : context.Request.Method.ToUpperInvariant();
        return new PolicyScope(Host: hostSlot, Method: method);
    }

    /// <summary>
    ///     Build the per-request signal bag fed into the resolver. The
    ///     resolver already overlays scope-derived request.* / geo.country /
    ///     identity.* signals on top; this method forwards the orchestrator's
    ///     aggregated signals AND the canonical host slots so
    ///     <c>PolicyScopeMatcher</c> can narrow rules with explicit
    ///     Method / Geo / Identity axes.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildPolicyRequestSignals(
        HttpContext context,
        AggregatedEvidence evidence)
    {
        var dict = new Dictionary<string, object?>(evidence.Signals.Count + 8);
        foreach (var kv in evidence.Signals)
            dict[kv.Key] = kv.Value;

        var hostHeader = context.Request.Host.Host ?? string.Empty;
        var (domain, subdomain) = SplitHost(hostHeader);
        if (!string.IsNullOrEmpty(domain))
            dict[PolicyScopeMatcher.RequestDomainKey] = domain;
        if (!string.IsNullOrEmpty(subdomain))
            dict[PolicyScopeMatcher.RequestSubdomainKey] = subdomain;
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        dict[PolicyScopeMatcher.RequestPathKey] = path;
        if (!string.IsNullOrEmpty(context.Request.Method))
            dict[PolicyScopeMatcher.RequestMethodKey] = context.Request.Method.ToUpperInvariant();
        return dict;
    }

    /// <summary>
    ///     Split a host header into (apex domain, subdomain). Apex = last
    ///     two labels; subdomain = everything to the left (empty when the
    ///     host is just the apex). Handles IP literals and single-label
    ///     hosts by treating the whole thing as the domain with an empty
    ///     subdomain.
    /// </summary>
    private static (string Domain, string Subdomain) SplitHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return (string.Empty, string.Empty);
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        if (host.Length == 0) return (string.Empty, string.Empty);
        if (System.Net.IPAddress.TryParse(host, out _)) return (host, string.Empty);
        var parts = host.Split('.');
        if (parts.Length <= 2) return (host, string.Empty);
        var domain = parts[^2] + "." + parts[^1];
        var subdomain = string.Join('.', parts[..^2]);
        return (domain, subdomain);
    }
}