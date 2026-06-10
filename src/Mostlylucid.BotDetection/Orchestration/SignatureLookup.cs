using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Canonical "what is the visitor's signature?" lookup against an
///     <see cref="AggregatedEvidence"/> bag. Single source for the
///     <see cref="SignalKeys.PrimarySignature"/> -> <c>SignatureMultifactor.PrimarySignature</c>
///     fallback chain that was previously reinvented at ~30 inline sites
///     across the action policies / matcher / orchestrator / dashboard
///     surfaces. Slight differences between those copies (some checked
///     for empty string, some didn't; some included the multifactor
///     fallback, some didn't) were silent divergence vectors.
///     <para>
///     Two shapes are exposed because callers genuinely want different
///     contracts:
///     </para>
///     <list type="bullet">
///         <item><see cref="Primary"/> -- PrimarySignature only. Used by
///         rate-limit key derivation where multifactor variants would
///         scatter bucket keys.</item>
///         <item><see cref="PrimaryOrMultifactor"/> -- PrimarySignature
///         with a fallback to <c>SignatureMultifactor.PrimarySignature</c>
///         when the orchestrator hadn't projected the top-level signal
///         yet. Used by challenge / honeypot / cookie-binding paths that
///         want the broadest possible identifier.</item>
///     </list>
///     <para>
///     Both return <c>null</c> when the signal is genuinely absent or
///     empty; callers decide whether to fall back to IP+UA, route to a
///     null-bucket, etc.
///     </para>
/// </summary>
public static class SignatureLookup
{
    /// <summary>
    ///     Returns <see cref="SignalKeys.PrimarySignature"/> when present
    ///     and non-empty; otherwise <c>null</c>.
    /// </summary>
    public static string? Primary(AggregatedEvidence evidence)
        => evidence.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sig)
            && sig is string s && s.Length > 0
            ? s
            : null;

    /// <summary>
    ///     Returns <see cref="SignalKeys.PrimarySignature"/> when present
    ///     and non-empty; otherwise falls back to the PrimarySignature on
    ///     a <c>SignatureMultifactor</c> signal (for evidence shapes where
    ///     the orchestrator hasn't projected the top-level signal yet);
    ///     otherwise <c>null</c>.
    /// </summary>
    public static string? PrimaryOrMultifactor(AggregatedEvidence evidence)
    {
        if (evidence.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sig)
            && sig is string s && !string.IsNullOrWhiteSpace(s))
            return s;

        if (evidence.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
            && multiObj is MultiFactorSignatures multi
            && !string.IsNullOrWhiteSpace(multi.PrimarySignature))
            return multi.PrimarySignature;

        return null;
    }
}
