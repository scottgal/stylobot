using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch;

/// <summary>
///     Bridges the new <see cref="PolicyAction"/> vocabulary to the HTTP
///     request pipeline. Invoked by <c>BotDetectionMiddleware</c> AFTER the
///     detection orchestrator has populated request signals and BEFORE the
///     legacy <c>DetectionPolicyAction</c> enum is consulted.
///
///     <para>
///         The dispatcher pulls the effective rule stack via
///         <see cref="IPolicyResolver.EffectiveWithContextAsync"/>, picks the
///         winning rule (see Selection below), and dispatches its action to
///         the matching <see cref="IPolicyActionHandler"/>. Returns a
///         <see cref="PolicyDispatchResult"/> telling the middleware whether
///         the HTTP response is already shaped (short-circuit) or whether to
///         continue to the legacy path (fall-through).
///     </para>
///
///     <para>Selection:</para>
///     <list type="number">
///         <item><description>Armed rules ALWAYS win (Phase G semantics). The resolver already sorts armed first, so the dispatcher accepts the first armed entry it sees.</description></item>
///         <item><description>Otherwise the first Live-mode rule wins. The resolver returns rules sorted by composite specificity descending, so first-match-wins lands on the most-specific Live rule.</description></item>
///         <item><description>Observe-mode rules are logged ("would have done X") but never shape the HTTP response. The dispatcher records to the decision log and keeps walking.</description></item>
///         <item><description>Draft and Disabled rules are skipped entirely.</description></item>
///     </list>
///
///     <para>
///         The decision log integration is fail-soft: a faulty log writer never
///         takes down the dispatcher. Cancellation propagates; everything else
///         is swallowed with an <see cref="ILogger"/> warning when available.
///     </para>
/// </summary>
public sealed class PolicyActionDispatcher
{
    /// <summary>
    ///     <see cref="HttpContext.Items"/> key set by <c>AllowActionHandler</c>
    ///     when an Allow rule fires. Read by the middleware to skip the legacy
    ///     <c>DetectionPolicyAction</c> enforcement -- the policy stack said
    ///     this request is fine and the legacy path should not override that.
    ///     Value: the <see cref="PolicyRule.Id"/> of the matched rule (Guid).
    /// </summary>
    public const string AllowMarkerItemKey = "PolicyDispatched.Allow";

    /// <summary>
    ///     <see cref="HttpContext.Items"/> key prefix used by
    ///     <c>TagActionHandler</c> when a Tag rule fires. The full key is
    ///     <c>"stylobot.policy.tag.{tag.Name}"</c>, value <c>true</c>.
    /// </summary>
    public const string TagItemKeyPrefix = "stylobot.policy.tag.";

    /// <summary>
    ///     <see cref="HttpContext.Items"/> key under which the dispatcher
    ///     stashes the per-request signal bag handed to the resolver. The
    ///     handler set can read this when it needs to attribute behaviour to
    ///     a fingerprint without re-binding to detection internals.
    /// </summary>
    public const string RequestSignalsItemKey = "PolicyDispatched.RequestSignals";

    private readonly IPolicyResolver _resolver;
    private readonly IPolicyDecisionLogQueue? _decisionLogQueue;
    private readonly IReadOnlyDictionary<Type, IPolicyActionHandler> _handlers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PolicyActionDispatcher>? _logger;

    /// <summary>
    ///     Constructor for DI. The handler set is materialised once into a
    ///     <see cref="Type"/>-keyed dictionary so per-request dispatch is a
    ///     single lookup. Multiple handlers registered for the same action
    ///     type: the last one registered wins (matches DI <c>AddSingleton</c>
    ///     enumeration semantics).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Wave 4 architectural-drift remediation:</b> the dispatcher
    ///         used to take an <see cref="IPolicyDecisionLog"/> and await
    ///         <c>AppendAsync</c> inline on the dispatch hot path. It now
    ///         takes an <see cref="IPolicyDecisionLogQueue"/> -- the
    ///         queue is a bounded channel with a Tick1s drainer, so the
    ///         request thread enqueues and returns without blocking on the
    ///         underlying store.
    ///     </para>
    /// </remarks>
    public PolicyActionDispatcher(
        IPolicyResolver resolver,
        IEnumerable<IPolicyActionHandler> handlers,
        IPolicyDecisionLogQueue? decisionLogQueue = null,
        TimeProvider? timeProvider = null,
        ILogger<PolicyActionDispatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(handlers);
        _resolver = resolver;
        _decisionLogQueue = decisionLogQueue;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        var map = new Dictionary<Type, IPolicyActionHandler>();
        foreach (var h in handlers)
        {
            if (h is null) continue;
            map[h.HandledAction] = h;
        }
        _handlers = map;
    }

    /// <summary>
    ///     Resolve the effective rule stack for <paramref name="requestScope"/>,
    ///     select the winning rule, and dispatch its action.
    /// </summary>
    /// <param name="context">The active HTTP context.</param>
    /// <param name="requestScope">The request-shaped <see cref="PolicyScope"/> -- Host slot populated from the request URL, orthogonal slots from per-request signals.</param>
    /// <param name="requestSignals">Per-request signal bag fed to the resolver.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/> is propagated.</param>
    public async Task<PolicyDispatchResult> DispatchAsync(
        HttpContext context,
        PolicyScope requestScope,
        IReadOnlyDictionary<string, object?> requestSignals,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestScope);
        ArgumentNullException.ThrowIfNull(requestSignals);

        // Stash the request signals dict on HttpContext.Items so the decision-
        // log path can pull the fingerprint without re-binding to detection
        // internals. Cleaned up implicitly at request end (Items dies with the
        // context); cost is one dictionary key.
        context.Items[RequestSignalsItemKey] = requestSignals;

        IReadOnlyList<EffectiveRule> effective;
        try
        {
            effective = await _resolver
                .EffectiveWithContextAsync(requestScope, requestSignals, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Resolver fault MUST NOT take down the request -- fall through so
            // the legacy enum path still runs.
            _logger?.LogWarning(ex, "PolicyActionDispatcher: resolver threw; falling through");
            return PolicyDispatchResult.FallThrough;
        }

        // Selection: armed first, then Live; Observe logs+falls through; Draft/Disabled skipped.
        EffectiveRule? winner = null;
        foreach (var entry in effective)
        {
            switch (entry.Rule.Mode)
            {
                case PolicyMode.Disabled:
                case PolicyMode.Draft:
                    continue;
                case PolicyMode.Observe when !entry.IsArmed:
                    // Observe-mode rules log the would-have outcome and keep
                    // walking; they NEVER shape the HTTP response. An Observe
                    // rule that is also armed is treated as Live (the operator
                    // explicitly wired the trigger; armed beats authored mode).
                    await TryLogDecisionAsync(context, entry, entry.Rule.Action,
                        matched: true, ObservedOutcome.Observed, ct).ConfigureAwait(false);
                    continue;
            }

            // Armed OR Live -- this is the winner.
            winner = entry;
            break;
        }

        if (winner is null)
            return PolicyDispatchResult.FallThrough;

        // Phase G: if the rule is armed AND its trigger options carry an
        // ActionWhileArmed, substitute that for the authored action.
        var action = winner.IsArmed && winner.Rule.Trigger?.ActionWhileArmed is not null
            ? winner.Rule.Trigger.ActionWhileArmed
            : winner.Rule.Action;

        if (!_handlers.TryGetValue(action.GetType(), out var handler))
        {
            // Unknown action type -- log + fall through. The legacy enum path
            // still runs; no risk of orphaning a request because we couldn't
            // find a handler for a new action subtype.
            _logger?.LogWarning(
                "PolicyActionDispatcher: no handler registered for action type {Action} (rule {RuleId}); falling through",
                action.GetType().Name, winner.Rule.Id);
            await TryLogDecisionAsync(context, winner, action,
                matched: true, ObservedOutcome.FellThrough, ct).ConfigureAwait(false);
            return PolicyDispatchResult.FallThrough;
        }

        PolicyDispatchResult result;
        try
        {
            result = await handler.HandleAsync(context, winner.Rule, action, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "PolicyActionDispatcher: handler {Handler} threw for rule {RuleId}; falling through",
                handler.GetType().Name, winner.Rule.Id);
            await TryLogDecisionAsync(context, winner, action,
                matched: true, ObservedOutcome.FellThrough, ct).ConfigureAwait(false);
            return PolicyDispatchResult.FallThrough;
        }

        var outcome = result == PolicyDispatchResult.Handled
            ? ObservedOutcome.Enforced
            : ObservedOutcome.FellThrough;
        await TryLogDecisionAsync(context, winner, action, matched: true, outcome, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     Best-effort decision-log write. The legacy
    ///     <see cref="PolicyDecision"/> schema has no <c>outcome</c> column
    ///     (the existing log row shape is per-rule "matched / would have done
    ///     X"). We encode the dispatcher's outcome by mapping
    ///     <see cref="ObservedOutcome.Observed"/> to <see cref="PolicyMode.Observe"/>
    ///     in the decision row and <see cref="ObservedOutcome.Enforced"/> to
    ///     <see cref="PolicyMode.Live"/>, then fall back to a structured log
    ///     line carrying the outcome verbatim. Adding an explicit
    ///     <c>outcome</c> column to the schema is the right long-term move but
    ///     out of scope for this task.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Wave 4:</b> the method name is kept for source compat with the
    ///         pre-Wave-4 dispatcher, but the body now enqueues on a bounded
    ///         channel via <see cref="IPolicyDecisionLogQueue"/> and returns
    ///         a completed task. The await chain inherited by every dispatch
    ///         call site keeps compiling unchanged; the runtime cost is one
    ///         <c>TryWrite</c>.
    ///     </para>
    /// </remarks>
    private Task TryLogDecisionAsync(
        HttpContext context,
        EffectiveRule entry,
        PolicyAction action,
        bool matched,
        ObservedOutcome outcome,
        CancellationToken ct)
    {
        // Structured-log fallback runs unconditionally so operators can grep
        // the dispatcher's decisions even when no IPolicyDecisionLogQueue is wired.
        _logger?.LogDebug(
            "PolicyDispatch: rule={RuleId} mode={Mode} armed={IsArmed} action={Action} outcome={Outcome}",
            entry.Rule.Id, entry.Rule.Mode, entry.IsArmed, action.GetType().Name, outcome);

        if (_decisionLogQueue is null) return Task.CompletedTask;

        try
        {
            var fingerprint = TryReadFingerprint(context);
            var observedMode = outcome switch
            {
                ObservedOutcome.Observed => PolicyMode.Observe,
                ObservedOutcome.Enforced => PolicyMode.Live,
                _ => entry.Rule.Mode
            };
            var decision = new PolicyDecision(
                RuleId: entry.Rule.Id,
                WinnerRuleId: entry.Rule.Id,
                Matched: matched,
                RequestFingerprint: fingerprint,
                Action: action,
                Mode: observedMode,
                EvalLatencyTicks: 0,
                ObservedAt: _timeProvider.GetUtcNow());
            _decisionLogQueue.Enqueue(decision);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "PolicyActionDispatcher: decision log enqueue threw for rule {RuleId}; ignoring",
                entry.Rule.Id);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Pull the request fingerprint out of the signal bag the dispatcher
    ///     stashed on <see cref="HttpContext.Items"/>. The canonical key is
    ///     <see cref="Models.SignalKeys.PrimarySignature"/> (<c>signature.primary</c>).
    /// </summary>
    internal static string? TryReadFingerprint(HttpContext context)
    {
        if (!context.Items.TryGetValue(RequestSignalsItemKey, out var raw)
            || raw is not IReadOnlyDictionary<string, object?> signals)
            return null;
        return signals.TryGetValue(Mostlylucid.BotDetection.Models.SignalKeys.PrimarySignature, out var v) ? v as string : null;
    }

    /// <summary>
    ///     What happened to a per-rule decision in this dispatch. Recorded on
    ///     the structured log line and encoded onto the
    ///     <see cref="PolicyDecision.Mode"/> field when the
    ///     <see cref="IPolicyDecisionLog"/> is wired.
    /// </summary>
    private enum ObservedOutcome
    {
        /// <summary>The handler shaped the HTTP response.</summary>
        Enforced,

        /// <summary>An Observe-mode rule matched; logged-only, no enforcement.</summary>
        Observed,

        /// <summary>Action handler said fall-through, or the dispatcher could not pick a handler.</summary>
        FellThrough
    }
}
