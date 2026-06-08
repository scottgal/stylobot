using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     Assembles the effective rule stack at a given <see cref="PolicyScope"/>
///     by walking from most-specific (Endpoint) to broadest (Wildcard) and
///     concatenating the per-scope rule lists in priority order. The resolver
///     does not evaluate predicates against a request unless asked via
///     <see cref="EffectiveWithContextAsync"/>; the basic
///     <see cref="EffectiveAsync"/> surface is what the dashboard "Stack" tab
///     and the YAML diff renderer consume.
/// </summary>
public interface IPolicyResolver
{
    /// <summary>
    ///     Returns the rules that apply at <paramref name="scope"/>, ordered
    ///     most-specific-scope first, then by priority ascending within a
    ///     scope. Does NOT evaluate the predicates -- it's the merged stack.
    /// </summary>
    Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(
        PolicyScope scope,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the rules merged like <see cref="EffectiveAsync"/> but
    ///     filtered to those whose predicate matches the supplied
    ///     <paramref name="requestSignals"/>. First-match-wins is the
    ///     caller's responsibility; this surface gives the operator the
    ///     complete candidate list so the trace UI can show which rule would
    ///     short-circuit.
    /// </summary>
    Task<IReadOnlyList<EffectiveRule>> EffectiveWithContextAsync(
        PolicyScope scope,
        IReadOnlyDictionary<string, object?> requestSignals,
        CancellationToken ct = default);
}
