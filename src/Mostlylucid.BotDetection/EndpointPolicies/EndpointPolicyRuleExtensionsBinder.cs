using Microsoft.Extensions.Configuration;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Post-configure step that recovers operator YAML/config keys which are not
///     baked-in matchers into <see cref="EndpointPolicyRule.Extensions"/>, so
///     external <see cref="IEndpointPolicyRuleExtension"/> matchers can consume
///     them. The standard configuration binder silently ignores keys that have no
///     matching property, so without this the extension sub-trees would be lost.
/// </summary>
public static class EndpointPolicyRuleExtensionsBinder
{
    // Baked-in EndpointPolicyRule field names -- any other key under a rule is an
    // extension key. Case-insensitive to match the configuration binder.
    private static readonly HashSet<string> BakedInKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(EndpointPolicyRule.Host),
        nameof(EndpointPolicyRule.Method),
        nameof(EndpointPolicyRule.Path),
        nameof(EndpointPolicyRule.Transport),
        nameof(EndpointPolicyRule.ProtocolVersion),
        nameof(EndpointPolicyRule.ModeIn),
        nameof(EndpointPolicyRule.Source),
        nameof(EndpointPolicyRule.Action),
        nameof(EndpointPolicyRule.StatusCode),
        nameof(EndpointPolicyRule.Reason),
        nameof(EndpointPolicyRule.Extensions),
    };

    /// <summary>
    ///     Collects unknown keys from the configured <c>Rules</c> array into each
    ///     rule's <see cref="EndpointPolicyRule.Extensions"/>. Config-declared
    ///     rules are the first N in declaration order, so the i-th config child
    ///     maps to <c>opts.Rules[i]</c>. Rules appended after binding (for
    ///     example built-in health defaults) have no config child and are left
    ///     with their empty extension dictionary.
    /// </summary>
    public static void Collect(EndpointPolicyOptions opts, IConfiguration config)
    {
        var rulesSection = config.GetSection($"{EndpointPolicyOptions.SectionName}:Rules");
        if (!rulesSection.Exists()) return;

        var i = 0;
        foreach (var ruleSection in rulesSection.GetChildren())
        {
            if (i >= opts.Rules.Count) break;
            var rule = opts.Rules[i];
            foreach (var child in ruleSection.GetChildren())
            {
                if (BakedInKeys.Contains(child.Key)) continue;
                rule.Extensions[child.Key] = ConvertSection(child);
            }
            i++;
        }
    }

    // Materialises a raw config sub-tree into object?: string leaves, nested
    // dictionaries (IReadOnlyDictionary<string, object?>), and integer-keyed
    // arrays (List<object?>). Extension payloads are usually maps.
    private static object? ConvertSection(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return section.Value;

        // Treat as an array only when the child keys are exactly the contiguous
        // integer sequence 0..n-1 (how the binder represents YAML/JSON arrays).
        var isArray = children.All(c => int.TryParse(c.Key, out _))
                      && children.Select(c => int.Parse(c.Key)).OrderBy(x => x)
                          .SequenceEqual(Enumerable.Range(0, children.Count));

        if (isArray)
            return children.OrderBy(c => int.Parse(c.Key)).Select(ConvertSection).ToList();

        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
            map[child.Key] = ConvertSection(child);
        return map;
    }
}
