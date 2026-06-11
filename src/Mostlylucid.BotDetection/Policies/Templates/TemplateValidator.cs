namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Validates the integrity of an entire <see cref="Template"/> collection
///     at <see cref="TemplateRegistry"/> construction time. Every check throws
///     <see cref="InvalidOperationException"/> with an operator-readable
///     message naming the offending template id(s) so a bad customer YAML
///     fails boot loudly instead of silently producing the wrong policy.
///
///     <para>
///         The validator runs against the AS-AUTHORED templates (extends:
///         still set, expansion is local-only). The registry resolves the
///         inheritance graph after validation passes.
///     </para>
/// </summary>
public sealed class TemplateValidator
{
    private readonly IReadOnlyDictionary<string, Template> _byId;

    public TemplateValidator(IReadOnlyDictionary<string, Template> byId)
    {
        ArgumentNullException.ThrowIfNull(byId);
        _byId = byId;
    }

    /// <summary>
    ///     Detects cycles via DFS. Throws
    ///     <see cref="InvalidOperationException"/> with the cycle path
    ///     (<c>A -> B -> A</c>) when an extends-chain loops back on itself.
    ///     Self-extends (<c>A extends A</c>) is the degenerate one-node cycle.
    /// </summary>
    public void ValidateNoCycles()
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in _byId.Values)
        {
            var path = new List<string>();
            Walk(t.Id, visiting, visited, path);
        }
    }

    private void Walk(string id, HashSet<string> visiting, HashSet<string> visited, List<string> path)
    {
        if (visited.Contains(id)) return;

        if (!visiting.Add(id))
        {
            // Found a cycle -- compose a readable path from the in-progress stack.
            var cycleStart = path.FindIndex(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
            var cycle = cycleStart >= 0 ? path.Skip(cycleStart).ToList() : new List<string>(path);
            cycle.Add(id);
            throw new InvalidOperationException(
                $"Template inheritance cycle detected: {string.Join(" -> ", cycle)}");
        }

        path.Add(id);

        if (_byId.TryGetValue(id, out var node) && !string.IsNullOrWhiteSpace(node.ExtendsTemplateId))
        {
            if (_byId.ContainsKey(node.ExtendsTemplateId))
                Walk(node.ExtendsTemplateId, visiting, visited, path);
        }

        path.RemoveAt(path.Count - 1);
        visiting.Remove(id);
        visited.Add(id);
    }

    /// <summary>
    ///     Every <see cref="Template.ExtendsTemplateId"/> must reference a
    ///     template currently loaded. A dangling extends: would otherwise
    ///     silently flatten to the child alone, producing a policy that
    ///     differs from what the operator wrote.
    /// </summary>
    public void ValidateAllExtendsResolveToKnownTemplates()
    {
        foreach (var t in _byId.Values)
        {
            if (string.IsNullOrWhiteSpace(t.ExtendsTemplateId)) continue;
            if (!_byId.ContainsKey(t.ExtendsTemplateId))
                throw new InvalidOperationException(
                    $"Template '{t.Id}' extends unknown template '{t.ExtendsTemplateId}'");
        }
    }

    /// <summary>
    ///     When a child overrides a parent parameter by name, the declared
    ///     <see cref="TemplateParameter.Type"/> must match. The editor surface
    ///     keys input controls off type; a silent flip from int to string
    ///     would produce nonsense substitution at materialization.
    /// </summary>
    public void ValidateNoParameterTypeConflicts()
    {
        foreach (var t in _byId.Values)
        {
            if (string.IsNullOrWhiteSpace(t.ExtendsTemplateId)) continue;
            if (!_byId.TryGetValue(t.ExtendsTemplateId, out var parent)) continue;

            // Walk the resolved parameter chain via the parent (recursively),
            // pick up every parameter visible to the child via inheritance.
            var inheritedByName = CollectInheritedParameters(parent)
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var p in t.Parameters)
            {
                if (!inheritedByName.TryGetValue(p.Name, out var parentParam)) continue;
                if (!string.Equals(NormalizeType(parentParam.Type), NormalizeType(p.Type), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Template '{t.Id}' parameter '{p.Name}' type '{p.Type}' conflicts with parent '{parent.Id}' parameter type '{parentParam.Type}'");
            }
        }
    }

    /// <summary>
    ///     Within a fully-resolved expansion list, every
    ///     <see cref="TemplateExpansionEntry.Suffix"/> must be unique. A child
    ///     re-using a parent's suffix would collide on the stable rule id and
    ///     silently overwrite one of the materialized rules.
    /// </summary>
    public void ValidateUniqueSuffixesAfterResolution()
    {
        foreach (var t in _byId.Values)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in CollectResolvedExpansion(t))
            {
                if (seen.TryGetValue(entry.Suffix, out var owner))
                    throw new InvalidOperationException(
                        $"Template '{t.Id}' expansion suffix '{entry.Suffix}' collides between inheritance ancestor '{owner}' and self/child");
                seen[entry.Suffix] = t.Id;
            }
        }
    }

    private IEnumerable<TemplateParameter> CollectInheritedParameters(Template t)
    {
        // Returns the parent (and its ancestors')' parameters with child-most
        // overrides applied. Walks bottom-up so the resolved view matches the
        // registry's flattening.
        var resolved = new Dictionary<string, TemplateParameter>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        var cursor = t;
        var chain = new List<Template>();
        while (cursor is not null && depth++ < 32)
        {
            chain.Add(cursor);
            if (string.IsNullOrWhiteSpace(cursor.ExtendsTemplateId)) break;
            if (!_byId.TryGetValue(cursor.ExtendsTemplateId, out var next)) break;
            cursor = next;
        }
        // Apply oldest ancestor first so child wins on name collision.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var p in chain[i].Parameters)
                resolved[p.Name] = p;
        }
        return resolved.Values;
    }

    private IEnumerable<TemplateExpansionEntry> CollectResolvedExpansion(Template t)
    {
        var chain = new List<Template>();
        var cursor = t;
        var depth = 0;
        while (cursor is not null && depth++ < 32)
        {
            chain.Add(cursor);
            if (string.IsNullOrWhiteSpace(cursor.ExtendsTemplateId)) break;
            if (!_byId.TryGetValue(cursor.ExtendsTemplateId, out var next)) break;
            cursor = next;
        }
        // Yield from the eldest ancestor downward so suffix-collision messages
        // attribute the first occurrence to the parent.
        for (var i = chain.Count - 1; i >= 0; i--)
            foreach (var entry in chain[i].Expansion)
                yield return entry;
    }

    private static string NormalizeType(string? type)
        => string.IsNullOrWhiteSpace(type) ? "string" : type.Trim().ToLowerInvariant();
}
