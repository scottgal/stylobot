namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     In-memory catalog of every <see cref="Template"/> loaded at boot. The
///     registry is the runtime lookup surface used by the
///     <see cref="TemplateResolver"/> to expand a
///     <see cref="TemplateApplication"/> against its referenced template.
///
///     <para>
///         The on-disk source of truth is YAML: the FOSS embedded-resource
///         catalog under <c>Templates/Catalog/*.yaml</c> plus an optional
///         customer directory. <see cref="YamlTemplateStore"/> loads both
///         sources at boot and hands the resulting <see cref="Template"/>
///         list to this registry's constructor. The registry itself is a
///         dictionary by design -- it is the lookup index, not a persistence
///         layer.
///     </para>
///
///     <para>
///         T5 adds <c>extends:</c> inheritance. At construction time the
///         registry runs <see cref="TemplateValidator"/> against the raw
///         (as-authored) collection, then materializes each template via
///         <see cref="ResolveInheritance"/>. The stored value is the
///         FLATTENED template (parent's parameters + child's; parent's
///         expansion + child's). Lookup consumers see a single-source record
///         with <see cref="Template.ExtendsTemplateId"/> nulled; the original
///         YAML files preserve the inheritance metadata for round-trip.
///     </para>
/// </summary>
public sealed class TemplateRegistry
{
    /// <summary>
    ///     Hard cap on extends-chain depth. Well past anything reasonable --
    ///     four levels would already be operator anti-pattern. Throws rather
    ///     than silently truncating so the operator notices the runaway.
    /// </summary>
    internal const int MaxInheritanceDepth = 16;

    private readonly Dictionary<string, Template> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Build the registry from a pre-loaded template list. Duplicate ids
    ///     throw: the loader should de-duplicate (customer overrides FOSS) or
    ///     surface the conflict to the operator before constructing the
    ///     registry.
    ///
    ///     <para>
    ///         When any template's <see cref="Template.ExtendsTemplateId"/> is
    ///         set, the inheritance graph is validated and resolved: cycles
    ///         throw, unknown parents throw, parameter-type conflicts throw,
    ///         expansion-suffix collisions throw. On success the registry
    ///         holds FLATTENED templates -- consumers never see the as-authored
    ///         shape.
    ///     </para>
    /// </summary>
    public TemplateRegistry(IEnumerable<Template> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var raw = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates)
        {
            if (raw.ContainsKey(t.Id))
                throw new InvalidOperationException($"Duplicate template id: {t.Id}");
            raw[t.Id] = t;
        }

        var validator = new TemplateValidator(raw);
        validator.ValidateAllExtendsResolveToKnownTemplates();
        validator.ValidateNoCycles();
        validator.ValidateNoParameterTypeConflicts();
        validator.ValidateUniqueSuffixesAfterResolution();

        foreach (var t in raw.Values)
            _byId[t.Id] = ResolveInheritance(t, raw, depth: 0);
    }

    /// <summary>Lookup a template by id. Returns <c>null</c> when no template with that id is loaded.</summary>
    public Template? Find(string id) => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>Every template currently loaded. Used by the dashboard template-picker.</summary>
    public IReadOnlyCollection<Template> All => _byId.Values;

    /// <summary>
    ///     Flatten <paramref name="t"/> by walking its <c>extends:</c> chain.
    ///     Parent (resolved) parameters + child parameters with child winning
    ///     on name match; parent (resolved) expansion entries concatenated
    ///     with child's. The returned record has
    ///     <see cref="Template.ExtendsTemplateId"/> set to <c>null</c> --
    ///     runtime consumers are not aware the template was inherited.
    /// </summary>
    private static Template ResolveInheritance(Template t, IReadOnlyDictionary<string, Template> all, int depth)
    {
        if (depth > MaxInheritanceDepth)
            throw new InvalidOperationException(
                $"Template '{t.Id}' inheritance depth exceeds {MaxInheritanceDepth}");

        if (string.IsNullOrWhiteSpace(t.ExtendsTemplateId))
            return t with { ExtendsTemplateId = null };

        // Validator guarantees the parent exists.
        var parent = all[t.ExtendsTemplateId];
        var resolvedParent = ResolveInheritance(parent, all, depth + 1);

        // Merge parameters: start with parent's, child overrides on name match.
        var paramsByName = resolvedParent.Parameters
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var p in t.Parameters)
            paramsByName[p.Name] = p;
        var mergedParams = paramsByName.Values.ToArray();

        // Append child's expansion entries to the parent's resolved list.
        var mergedExpansion = resolvedParent.Expansion.Concat(t.Expansion).ToArray();

        return new Template(
            Id: t.Id,
            DisplayName: string.IsNullOrWhiteSpace(t.DisplayName) ? resolvedParent.DisplayName : t.DisplayName,
            Description: string.IsNullOrWhiteSpace(t.Description) ? resolvedParent.Description : t.Description,
            Category: string.IsNullOrWhiteSpace(t.Category) ? resolvedParent.Category : t.Category,
            Parameters: mergedParams,
            Expansion: mergedExpansion,
            ExtendsTemplateId: null);
    }
}
