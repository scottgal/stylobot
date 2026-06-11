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
/// </summary>
public sealed class TemplateRegistry
{
    private readonly Dictionary<string, Template> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Build the registry from a pre-loaded template list. Duplicate ids
    ///     throw: the loader should de-duplicate (customer overrides FOSS) or
    ///     surface the conflict to the operator before constructing the
    ///     registry.
    /// </summary>
    public TemplateRegistry(IEnumerable<Template> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        foreach (var t in templates)
        {
            if (_byId.ContainsKey(t.Id))
                throw new InvalidOperationException($"Duplicate template id: {t.Id}");
            _byId[t.Id] = t;
        }
    }

    /// <summary>Lookup a template by id. Returns <c>null</c> when no template with that id is loaded.</summary>
    public Template? Find(string id) => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>Every template currently loaded. Used by the dashboard template-picker.</summary>
    public IReadOnlyCollection<Template> All => _byId.Values;
}
