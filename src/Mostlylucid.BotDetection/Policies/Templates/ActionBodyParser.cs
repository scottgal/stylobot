using System.Globalization;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Tiny inline-action parser for template expansion. Accepts the
///     compact flow-style shape used inside template YAML expansion entries:
///     <code>
///     { kind: rate_limit, rpm: 6 }
///     { kind: block }
///     { kind: throttle, rps: 1, reason: "overload-protect" }
///     { kind: tag, name: "scraper" }
///     { kind: challenge, challenge_kind: turnstile }
///     </code>
///     Returns a fully constructed <see cref="PolicyAction"/>. The grammar is
///     deliberately small: comma-separated <c>key: value</c> pairs between
///     braces, with values being a bare ident, a number, or a double-quoted
///     string. Whitespace is ignored. Keys are case-insensitive.
///
///     <para>
///         The compact form keeps template YAML readable -- writing
///         <c>action: '{ kind: rate_limit, rpm: 6 }'</c> is shorter than the
///         seven-line nested mapping the on-disk
///         <see cref="Rules.YamlRuleAction"/> shape requires, and avoids
///         leaking the snake_case storage keys (<c>requests_per_minute</c>)
///         into the operator-facing template author surface
///         (<c>rpm</c>).
///     </para>
/// </summary>
internal static class ActionBodyParser
{
    /// <summary>
    ///     Parse an action body string into a <see cref="PolicyAction"/>.
    ///     Throws <see cref="FormatException"/> for any malformed input --
    ///     the caller (the template resolver) treats that as a template
    ///     authoring bug surfaced at resolution time.
    /// </summary>
    public static PolicyAction Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var fields = ParseFlowMapping(body);

        if (!fields.TryGetValue("kind", out var kindRaw) || kindRaw is null)
            throw new FormatException($"action body is missing required 'kind' field: '{body}'");

        var kind = kindRaw.Trim().ToLowerInvariant();
        return kind switch
        {
            "allow" => new PolicyAction.Allow(),
            "observe" => new PolicyAction.Observe(),
            "block" => new PolicyAction.Block(),

            "tag" => new PolicyAction.Tag(
                RequireString(fields, "name", body)),

            "challenge" => new PolicyAction.Challenge(
                RequireString(fields, "challenge_kind", body)),

            "rate_limit" or "ratelimit" => new PolicyAction.RateLimit(
                RequireInt(fields, "rpm", body, fallback: "requests_per_minute")),

            "throttle" => new PolicyAction.Throttle(
                RequireInt(fields, "rps", body, fallback: "requests_per_second"),
                OptionalString(fields, "reason")),

            _ => throw new FormatException($"unknown action kind '{kind}' in '{body}'")
        };
    }

    private static string RequireString(IReadOnlyDictionary<string, string?> fields, string key, string body)
    {
        if (!fields.TryGetValue(key, out var v) || v is null)
            throw new FormatException($"action body is missing required '{key}': '{body}'");
        return v;
    }

    private static string? OptionalString(IReadOnlyDictionary<string, string?> fields, string key)
        => fields.TryGetValue(key, out var v) ? v : null;

    private static int RequireInt(
        IReadOnlyDictionary<string, string?> fields,
        string key,
        string body,
        string? fallback = null)
    {
        var raw = fields.TryGetValue(key, out var v) ? v : null;
        if (raw is null && fallback is not null)
            raw = fields.TryGetValue(fallback, out var v2) ? v2 : null;
        if (raw is null)
            throw new FormatException($"action body is missing required '{key}': '{body}'");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new FormatException($"action body '{key}' is not an integer: '{raw}' in '{body}'");
        return n;
    }

    // ── tiny flow-mapping reader ────────────────────────────────────────────
    //
    // Hand-rolled because the input alphabet is tiny: braces, comma, colon,
    // optional double-quoted strings, ident/number lexemes. Avoids pulling
    // VYaml in for a five-token form.

    private static Dictionary<string, string?> ParseFlowMapping(string body)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var src = body.Trim();
        if (src.Length == 0 || src[0] != '{' || src[src.Length - 1] != '}')
            throw new FormatException($"action body must be wrapped in '{{...}}': '{body}'");

        var inner = src.Substring(1, src.Length - 2);
        var pos = 0;
        while (pos < inner.Length)
        {
            SkipWhitespace(inner, ref pos);
            if (pos >= inner.Length) break;

            var key = ReadKey(inner, ref pos, body);
            SkipWhitespace(inner, ref pos);
            if (pos >= inner.Length || inner[pos] != ':')
                throw new FormatException($"expected ':' after key '{key}' in '{body}'");
            pos++; // consume ':'
            SkipWhitespace(inner, ref pos);

            var value = ReadValue(inner, ref pos, body);
            fields[key] = value;

            SkipWhitespace(inner, ref pos);
            if (pos < inner.Length)
            {
                if (inner[pos] != ',')
                    throw new FormatException($"expected ',' or '}}' after value in '{body}', got '{inner[pos]}'");
                pos++;
            }
        }

        return fields;
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static string ReadKey(string s, ref int pos, string fullBody)
    {
        var start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '-'))
            pos++;
        if (pos == start)
            throw new FormatException($"expected key name at position {start} in '{fullBody}'");
        return s.Substring(start, pos - start);
    }

    private static string ReadValue(string s, ref int pos, string fullBody)
    {
        if (pos >= s.Length)
            throw new FormatException($"unexpected end of action body in '{fullBody}'");

        if (s[pos] == '"')
        {
            // Quoted string: read until closing quote, supporting \" escapes.
            pos++; // skip opening quote
            var sb = new System.Text.StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    sb.Append(s[pos + 1]);
                    pos += 2;
                    continue;
                }
                sb.Append(s[pos++]);
            }
            if (pos >= s.Length)
                throw new FormatException($"unterminated string in '{fullBody}'");
            pos++; // skip closing quote
            return sb.ToString();
        }

        // Bare token: ident / number / negative number. Read until ',' or end.
        var start = pos;
        while (pos < s.Length && s[pos] != ',' && !char.IsWhiteSpace(s[pos]))
            pos++;
        var raw = s.Substring(start, pos - start);
        if (raw.Length == 0)
            throw new FormatException($"expected value at position {start} in '{fullBody}'");
        return raw;
    }
}
