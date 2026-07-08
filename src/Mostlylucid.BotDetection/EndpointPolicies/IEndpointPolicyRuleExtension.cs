using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     External-package hook for contributing named policy-rule matchers to
///     <see cref="IEndpointPolicyResolver"/>. FOSS knows nothing about a
///     matcher's semantics — only its name and its pass/fail vote. The core
///     product ships zero implementations; commercial packages register their
///     own (for example a licence-capability matcher). The seam is
///     licensing-agnostic: no licensing knowledge lives in FOSS.
/// </summary>
/// <remarks>
///     <para>
///         Extensions are evaluated at <see cref="IEndpointPolicyResolver.Match"/>
///         time, which runs PRE-detection — <c>EndpointPolicyMiddleware</c> fires
///         before <c>BotDetectionMiddleware</c>, so the <c>SignalSink</c> is not
///         yet populated. An extension is therefore handed the
///         <see cref="HttpContext"/> only and must self-contain: parse what it
///         needs from headers / path / method and verify via its own
///         DI-injected services. It must not depend on detector signals.
///     </para>
///     <para>
///         An extension MAY stash resolved data in <see cref="HttpContext.Items"/>
///         (per-request scoped, not sink-broadcast) for a downstream detection
///         atom to read and emit sink signals from — that is the atom's job, not
///         the extension's.
///     </para>
/// </remarks>
public interface IEndpointPolicyRuleExtension
{
    /// <summary>YAML key this extension binds to (for example <c>"requiresCapability"</c>).</summary>
    string RuleName { get; }

    /// <summary>
    ///     Evaluates the rule payload for the current request. Returns
    ///     <c>true</c> when the rule condition is satisfied. A <c>false</c> vote
    ///     means the owning rule row does not match and the resolver falls
    ///     through to the next rule. Throwing is treated as <c>false</c>
    ///     (fail-closed) — an extension exception never surfaces a 500.
    /// </summary>
    bool Matches(EndpointPolicyExtensionContext context);
}

/// <summary>
///     Per-request context handed to an <see cref="IEndpointPolicyRuleExtension"/>.
///     Carries the <see cref="HttpContext"/> (headers / path / method) and the
///     parsed YAML sub-tree declared under the extension's
///     <see cref="IEndpointPolicyRuleExtension.RuleName"/>. There is deliberately
///     no <c>SignalSink</c>: extensions run pre-detection and must not depend on
///     it (the sink is empty at Match time).
/// </summary>
public readonly record struct EndpointPolicyExtensionContext(
    HttpContext HttpContext,
    IReadOnlyDictionary<string, object?> RulePayload);
