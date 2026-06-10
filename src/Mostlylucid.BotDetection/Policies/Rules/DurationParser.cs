using System.Globalization;

namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Tiny human-duration parser used by the policy-rule YAML loader. Accepts
///     a small grammar: <c>{decimal}{unit}</c> where unit is one of <c>s</c>,
///     <c>m</c>, <c>h</c>, <c>d</c>. Examples: <c>"30s"</c>, <c>"5m"</c>,
///     <c>"1h"</c>, <c>"1.5m"</c>, <c>"2d"</c>.
///
///     <para>
///         Falls back to <see cref="TimeSpan.TryParse(string?, IFormatProvider?, out TimeSpan)"/>
///         (using <see cref="CultureInfo.InvariantCulture"/>) when the input
///         doesn't match the suffix grammar, so the standard
///         <c>hh:mm:ss</c> shape keeps working.
///     </para>
/// </summary>
internal static class DurationParser
{
    /// <summary>
    ///     Parse <paramref name="raw"/> into a <see cref="TimeSpan"/>. Returns
    ///     <c>true</c> on success and sets <paramref name="value"/>; returns
    ///     <c>false</c> on null/whitespace/garbage input and leaves
    ///     <paramref name="value"/> at <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public static bool TryParse(string? raw, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        var lastChar = trimmed[^1];

        // Suffix grammar: digits/decimal followed by a single unit char.
        if (lastChar is 's' or 'm' or 'h' or 'd' or 'S' or 'M' or 'H' or 'D')
        {
            var numericPart = trimmed[..^1];
            if (double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                value = char.ToLowerInvariant(lastChar) switch
                {
                    's' => TimeSpan.FromSeconds(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'd' => TimeSpan.FromDays(amount),
                    _ => TimeSpan.Zero,
                };
                return true;
            }
        }

        // Fallback: standard TimeSpan shapes (hh:mm:ss, etc.).
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
