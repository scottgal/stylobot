using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Deterministic hash over the four <see cref="PolicyRule"/> fields a
///     manual override can touch: <see cref="PolicyRule.Predicate"/> (via
///     <see cref="PredicateHasher"/>), <see cref="PolicyRule.Action"/> (its
///     record <c>ToString()</c>), <see cref="PolicyRule.Priority"/>, and
///     <see cref="PolicyRule.Mode"/>.
///
///     <para>
///         Used by <see cref="TemplateMaterializer"/> to detect
///         override-divergence: if a re-materialization produces a different
///         hash than what's currently stored for a given rule id, the loader
///         knows the rule was manually edited and preserves the operator's
///         edit.
///     </para>
///
///     <para>
///         The hash is SHA-256 over a fixed-order, culture-invariant
///         composition string so the result is stable across runs, processes,
///         platforms, and JsonSerializer property-ordering nondeterminism. The
///         output is the lower-case hex digest -- suitable for persistence and
///         dashboard display.
///     </para>
/// </summary>
public static class RuleHasher
{
    /// <summary>
    ///     Compute the deterministic hex digest of the four override-sensitive
    ///     fields on <paramref name="rule"/>. Same rule (structurally) always
    ///     returns the same hex string.
    /// </summary>
    public static string Hash(PolicyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // Fixed field order, invariant formatting. NOT json -- json property
        // ordering through System.Text.Json reflection is platform-dependent
        // and we need this to be byte-stable.
        var composed = string.Create(CultureInfo.InvariantCulture,
            $"predicateHash={PredicateHasher.Hash(rule.Predicate)}|" +
            $"action={ActionToStableString(rule.Action)}|" +
            $"priority={rule.Priority}|" +
            $"mode={rule.Mode}");

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(composed), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    ///     Project a <see cref="PolicyAction"/> to a stable string. C# record
    ///     <c>ToString()</c> is deterministic over the public properties but
    ///     pin the projection here so a future record-property addition doesn't
    ///     silently change every materialized hash on the next build.
    /// </summary>
    private static string ActionToStableString(PolicyAction action) => action switch
    {
        PolicyAction.Allow => "allow",
        PolicyAction.Observe => "observe",
        PolicyAction.Block => "block",
        PolicyAction.Tag t => $"tag:{t.Name}",
        PolicyAction.Challenge c => $"challenge:{c.Kind}",
        PolicyAction.RateLimit rl => string.Create(CultureInfo.InvariantCulture, $"rate_limit:{rl.RequestsPerMinute}"),
        PolicyAction.Throttle th => string.Create(CultureInfo.InvariantCulture, $"throttle:{th.RequestsPerSecond}:{th.Reason ?? ""}"),
        _ => action.GetType().Name
    };
}
