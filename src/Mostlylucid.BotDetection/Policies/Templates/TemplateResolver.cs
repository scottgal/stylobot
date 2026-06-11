using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Compiles a (<see cref="Template"/> + <see cref="TemplateApplication"/>)
///     pair into a flat list of bare <see cref="PolicyRule"/> instances.
///
///     <para>
///         For each applied-to scope, walks the template's expansion list,
///         substitutes <c>{{ parameter }}</c> placeholders with the
///         application's parameter values (or the template parameter's
///         default), parses the resulting predicate + action text into AST,
///         and emits a <see cref="PolicyRule"/> stamped with a
///         <see cref="RuleOrigin"/> for the template+application+expansion
///         that produced it.
///     </para>
///
///     <para>
///         Rule ids are deterministic: a stable hash of (template id,
///         application id, expansion suffix, scope-stable-key) folded to
///         a Guid. Re-applying the same application therefore produces the
///         same rule ids -- T2's override-divergence detection depends on
///         this property.
///     </para>
/// </summary>
public sealed class TemplateResolver
{
    /// <summary>
    ///     Resolve <paramref name="template"/> against <paramref name="application"/>
    ///     into a flat list of materialized rules. The returned list has
    ///     <c>expansion.Count * max(1, appliedTo.Count)</c> entries, in
    ///     <c>(scope, expansion-order)</c> nested order.
    /// </summary>
    public IReadOnlyList<PolicyRule> Resolve(Template template, TemplateApplication application)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(application);

        var rules = new List<PolicyRule>(template.Expansion.Count * Math.Max(1, application.AppliedTo.Count));

        // Build the merged parameter dictionary: application values win over
        // the template's declared defaults. Case-insensitive so placeholder
        // lookups match the operator's surface (the dashboard editor renders
        // parameter names verbatim).
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in template.Parameters)
            resolved[p.Name] = p.Default;
        foreach (var (key, value) in application.Parameters)
            resolved[key] = value;

        // Empty applied-to list means "global default" -- emit at the wildcard
        // scope so templates designed for wildcard application (e.g.
        // keep-verified-bots-working) don't need a synthetic wildcard entry.
        var scopes = application.AppliedTo.Count == 0
            ? new[] { PolicyScope.Wildcard() }
            : application.AppliedTo.ToArray();

        foreach (var scope in scopes)
        {
            foreach (var entry in template.Expansion)
            {
                var predicateText = Substitute(entry.Predicate, resolved);
                var actionText = Substitute(entry.Action, resolved);

                var predicate = PredicateParser.Parse(predicateText);
                var action = ActionBodyParser.Parse(actionText);
                var mode = ParseMode(entry.Mode);

                var ruleId = ComputeStableRuleId(template.Id, application.Id, entry.Suffix, scope);

                rules.Add(new PolicyRule(
                    Id: ruleId,
                    Scope: scope,
                    Priority: entry.Priority,
                    Predicate: predicate,
                    Action: action,
                    Mode: mode,
                    Notes: string.Empty,
                    Source: $"template:{template.Id} application:{application.Id}",
                    CreatedAt: DateTimeOffset.UtcNow,
                    RevisionId: Guid.NewGuid(),
                    Origin: new RuleOrigin(template.Id, application.Id, entry.Suffix)));
            }
        }

        return rules;
    }

    /// <summary>
    ///     Replace every <c>{{ name }}</c> placeholder in <paramref name="template"/>
    ///     with the parameter's value, formatted with invariant culture so
    ///     numeric values round-trip through the predicate / action parsers
    ///     deterministically. Unknown placeholders throw -- a template
    ///     authoring bug surfaced at apply-time.
    /// </summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, object?> parameters)
    {
        var sb = new StringBuilder(template.Length);
        var pos = 0;
        while (pos < template.Length)
        {
            // Scan for next "{{".
            var open = template.IndexOf("{{", pos, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(template, pos, template.Length - pos);
                break;
            }

            sb.Append(template, pos, open - pos);
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                throw new FormatException($"unterminated '{{{{' placeholder in template body: '{template}'");

            var name = template.Substring(open + 2, close - open - 2).Trim();
            if (!parameters.TryGetValue(name, out var value))
                throw new FormatException($"unknown parameter '{{{{ {name} }}}}' in template body: '{template}'");

            sb.Append(FormatValue(value));
            pos = close + 2;
        }
        return sb.ToString();
    }

    private static string FormatValue(object? v) => v switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        string s => s,
        TimeSpan ts => FormatDuration(ts),
        _ => v.ToString() ?? string.Empty
    };

    private static string FormatDuration(TimeSpan ts)
    {
        // Compose into the suffix grammar the predicate parser understands
        // for FOR Xs: prefer the largest single unit that divides cleanly,
        // fall back to total seconds. Always invariant.
        if (ts.TotalDays >= 1 && ts.TotalDays % 1 == 0)
            return ((int)ts.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
        if (ts.TotalHours >= 1 && ts.TotalHours % 1 == 0)
            return ((int)ts.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        if (ts.TotalMinutes >= 1 && ts.TotalMinutes % 1 == 0)
            return ((int)ts.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        return ((int)ts.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
    }

    private static PolicyMode ParseMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return PolicyMode.Live;
        return Enum.TryParse<PolicyMode>(mode.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : PolicyMode.Live;
    }

    /// <summary>
    ///     Deterministic rule id: SHA-256 of
    ///     <c>"template:{tid}|app:{aid}|exp:{suffix}|scope:{scopeKey}"</c>,
    ///     folded into the first 16 bytes as a Guid. Same inputs always
    ///     produce the same id -- so re-applying an application twice
    ///     materializes the same set of <see cref="PolicyRule.Id"/> values
    ///     and T2's override-divergence detector can spot operator edits
    ///     against the original materialization.
    /// </summary>
    internal static Guid ComputeStableRuleId(string templateId, string applicationId, string suffix, PolicyScope scope)
    {
        var seed = $"template:{templateId}|app:{applicationId}|exp:{suffix}|scope:{scope.ToStableKey()}";
        var bytes = Encoding.UTF8.GetBytes(seed);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.Slice(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
