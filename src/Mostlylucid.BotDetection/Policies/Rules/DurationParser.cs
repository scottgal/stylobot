using System.Globalization;

namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Canonical human-duration parser. Originally added for the policy-rule
///     YAML loader; promoted to a shared FOSS helper in Wave 5 of the
///     architectural-drift remediation so the dashboard's
///     <c>PolicyStackFilter</c> can stop hand-rolling its own
///     <c>@since:&lt;value&gt;</c> parser.
///
///     <para>
///         Accepted shapes (case-insensitive on the unit suffix):
///         <list type="bullet">
///             <item><description><c>{decimal}{unit}</c> where unit is one of <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>. Examples: <c>"30s"</c>, <c>"5m"</c>, <c>"1.5h"</c>, <c>"2d"</c>.</description></item>
///             <item><description>Composite suffix groups, e.g. <c>"1h30m"</c>, <c>"1d12h"</c>, <c>"2m30s"</c>. Each group must be <c>{decimal}{unit}</c>; groups are summed. Required to keep parity with hand-rolled compound parsers some callers expected.</description></item>
///             <item><description>Standard <see cref="TimeSpan"/> shapes (<c>hh:mm:ss</c>, etc.) via <see cref="TimeSpan.TryParse(string?, IFormatProvider?, out TimeSpan)"/> with <see cref="CultureInfo.InvariantCulture"/>.</description></item>
///         </list>
///     </para>
///
///     <para>
///         <b>Why public:</b> the policy-rule YAML loader is in this assembly
///         (<c>Mostlylucid.BotDetection</c>) and the dashboard
///         <c>PolicyStackFilter</c> lives in <c>Mostlylucid.BotDetection.UI</c>
///         which references this one. Public + same-namespace keeps both call
///         sites on one canonical implementation without an
///         <c>InternalsVisibleTo</c> dance. Satisfies
///         <c>feedback_no_duplication</c>.
///     </para>
/// </summary>
public static class DurationParser
{
    /// <summary>
    ///     Parse <paramref name="raw"/> into a <see cref="TimeSpan"/>. Returns
    ///     <c>true</c> on success and sets <paramref name="value"/>; returns
    ///     <c>false</c> on null / whitespace / garbage input and leaves
    ///     <paramref name="value"/> at <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <remarks>
    ///     Per call-site requirements:
    ///     <list type="bullet">
    ///         <item><description>YAML rule loader (<c>YamlPolicyRuleStore</c>): single-suffix tokens like <c>"30s"</c>, <c>"5m"</c>, <c>"1h"</c>, <c>"1.5m"</c>.</description></item>
    ///         <item><description>Dashboard filter (<c>PolicyStackFilter</c>): <c>@since:24h</c>, <c>@since:7d</c>, <c>@since:30d</c>. The dashboard validates positivity at its own boundary; this parser tolerates zero/negative for the YAML loader.</description></item>
    ///         <item><description>Composite suffix groups (<c>"1h30m"</c>) added in Wave 5 so the dashboard's previously hand-rolled parser can be retired without losing any expressive range.</description></item>
    ///     </list>
    /// </remarks>
    public static bool TryParse(string? raw, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();

        // 1) Try the suffix grammar first. Single-suffix tokens (the YAML
        //    loader + dashboard "since" form) and composite-suffix tokens
        //    (e.g. "1h30m") share the same scanner; the scanner walks
        //    pairs of [digits[.digits]] + [unit-char] from left to right
        //    and sums the contributions.
        if (TryParseSuffixGroups(trimmed, out var fromSuffix))
        {
            value = fromSuffix;
            return true;
        }

        // 2) Fallback: standard TimeSpan shapes (hh:mm:ss, etc.).
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Walk a <c>{number}{unit}{number}{unit}...</c> token left-to-right
    ///     and sum each group. Returns <c>false</c> the moment any character
    ///     fails to fit the grammar (so the caller can fall through to the
    ///     TimeSpan.TryParse path).
    /// </summary>
    private static bool TryParseSuffixGroups(ReadOnlySpan<char> input, out TimeSpan total)
    {
        total = TimeSpan.Zero;
        if (input.IsEmpty) return false;

        var i = 0;
        var groups = 0;

        while (i < input.Length)
        {
            // Skip optional whitespace between groups (lenient with "1h 30m").
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;

            // Scan the numeric prefix: digits, optional '.', more digits.
            // Allow a leading sign on the first group only; YAML loader uses
            // unsigned values but the original parser would accept "+1.5m"
            // because it deferred to double.TryParse.
            var numericStart = i;
            if (groups == 0 && (input[i] == '+' || input[i] == '-')) i++;

            var sawDigit = false;
            while (i < input.Length && char.IsDigit(input[i])) { i++; sawDigit = true; }
            if (i < input.Length && input[i] == '.')
            {
                i++;
                while (i < input.Length && char.IsDigit(input[i])) { i++; sawDigit = true; }
            }
            if (!sawDigit) return false;

            if (i >= input.Length) return false; // numeric prefix with no unit char

            var unit = input[i];
            if (unit is not ('s' or 'm' or 'h' or 'd' or 'S' or 'M' or 'H' or 'D'))
                return false;

            var numericSpan = input[numericStart..i];
            if (!double.TryParse(numericSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                return false;

            total += char.ToLowerInvariant(unit) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => TimeSpan.Zero,
            };

            i++;
            groups++;
        }

        return groups > 0;
    }
}
