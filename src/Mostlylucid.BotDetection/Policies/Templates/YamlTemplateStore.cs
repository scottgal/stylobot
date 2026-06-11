using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     VYaml-backed loader for <see cref="Template"/> definitions. Loads from
///     two sources:
///     <list type="bullet">
///       <item><description>
///         The FOSS canonical catalog -- YAML embedded as resources in the
///         <c>Mostlylucid.BotDetection</c> assembly under
///         <c>Policies/Templates/Catalog/*.yaml</c>.
///       </description></item>
///       <item><description>
///         An optional customer directory on disk (passed in by configuration
///         at boot). Customer templates parse alongside the embedded catalog
///         using the same YAML shape.
///       </description></item>
///     </list>
///
///     <para>
///         A malformed file (bad YAML, missing required field, empty
///         expansion) is logged at <c>Warning</c> and skipped -- one bad
///         template never blocks host boot. The loader emits a flat
///         <see cref="IReadOnlyList{T}"/> of <see cref="Template"/> the
///         caller hands to <see cref="TemplateRegistry"/>.
///     </para>
/// </summary>
public sealed class YamlTemplateStore
{
    /// <summary>
    ///     Resource prefix used by <see cref="LoadEmbeddedCatalog"/>. Kept
    ///     public so tests can re-use the same constant without duplicating
    ///     the string.
    /// </summary>
    public const string EmbeddedCatalogPrefix = "Mostlylucid.BotDetection.Policies.Templates.Catalog.";

    private readonly ILogger<YamlTemplateStore> _logger;

    public YamlTemplateStore(ILogger<YamlTemplateStore>? logger = null)
    {
        _logger = logger ?? NullLogger<YamlTemplateStore>.Instance;
    }

    /// <summary>
    ///     Load every template embedded in the FOSS assembly under
    ///     <see cref="EmbeddedCatalogPrefix"/>. The result is the canonical
    ///     stylobot catalog that ships in the DLL.
    /// </summary>
    public IReadOnlyList<Template> LoadEmbeddedCatalog()
        => LoadFromEmbeddedResources(typeof(YamlTemplateStore).Assembly, EmbeddedCatalogPrefix);

    /// <summary>
    ///     Load templates from a customer-supplied directory on disk. Returns
    ///     an empty list when <paramref name="directory"/> is <c>null</c>,
    ///     empty, or does not exist -- a "no customer templates" deployment is
    ///     the common case and should not crash.
    /// </summary>
    public IReadOnlyList<Template> LoadFromDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<Template>();

        var result = new List<Template>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read template file {Path}", path);
                continue;
            }

            if (TryParse(bytes, $"yaml:{Path.GetFileName(path)}", out var t) && t is not null)
                result.Add(t);
        }
        return result;
    }

    /// <summary>
    ///     Load the embedded FOSS catalog AND a customer directory together
    ///     and hand back the merged list. Customer templates that re-use a
    ///     FOSS template id REPLACE the FOSS template (customer wins) -- the
    ///     loader logs the override at <c>Information</c> so the operator
    ///     can audit the swap.
    /// </summary>
    public IReadOnlyList<Template> LoadAll(string? customerDirectory)
    {
        var combined = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in LoadEmbeddedCatalog())
            combined[t.Id] = t;

        foreach (var t in LoadFromDirectory(customerDirectory))
        {
            if (combined.ContainsKey(t.Id))
                _logger.LogInformation("Customer template {Id} overrides the FOSS catalog entry", t.Id);
            combined[t.Id] = t;
        }
        return combined.Values.ToArray();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "VYaml [YamlObject] formatters for YamlTemplateFile/Parameter/ExpansionEntry are registered in VYamlBootstrap.")]
    private IReadOnlyList<Template> LoadFromEmbeddedResources(Assembly assembly, string prefix)
    {
        var result = new List<Template>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            byte[] bytes;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read embedded template {Resource}", name);
                continue;
            }

            if (TryParse(bytes, $"embedded:{name}", out var t) && t is not null)
                result.Add(t);
        }
        return result;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "VYaml [YamlObject] formatters for YamlTemplateFile/Parameter/ExpansionEntry are registered in VYamlBootstrap.")]
    private bool TryParse(byte[] bytes, string sourceLabel, out Template? template)
    {
        template = null;
        if (bytes.Length == 0) return false;

        YamlTemplateFile? file;
        try
        {
            file = YamlSerializer.Deserialize<YamlTemplateFile>(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping malformed template file {Source}", sourceLabel);
            return false;
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Template))
        {
            _logger.LogWarning("Skipping template file {Source} -- missing 'template' id", sourceLabel);
            return false;
        }

        var parameters = (file.Parameters ?? new List<YamlTemplateParameter>())
            .Select(p => new TemplateParameter(
                Name: p.Name ?? string.Empty,
                Type: string.IsNullOrWhiteSpace(p.Type) ? "string" : p.Type,
                Default: CoerceDefault(p.Type, p.Default),
                Description: p.Description))
            .ToArray();

        var expansion = (file.Expansion ?? new List<YamlTemplateExpansionEntry>())
            .Select(e => new TemplateExpansionEntry(
                Suffix: e.Suffix ?? string.Empty,
                Predicate: e.Predicate ?? string.Empty,
                Action: e.Action ?? string.Empty,
                Priority: e.Priority,
                Mode: string.IsNullOrWhiteSpace(e.Mode) ? "live" : e.Mode))
            .ToArray();

        if (expansion.Length == 0)
        {
            _logger.LogWarning("Skipping template {Id} -- expansion is empty", file.Template);
            return false;
        }

        template = new Template(
            Id: file.Template,
            DisplayName: file.DisplayName ?? string.Empty,
            Description: file.Description ?? string.Empty,
            Category: file.Category ?? string.Empty,
            Parameters: parameters,
            Expansion: expansion);
        return true;
    }

    /// <summary>
    ///     Convert a YAML default string into a typed value so substitution
    ///     formats deterministically. YAML parses bare numbers / bools but
    ///     VYaml's flow-string default is "always string" -- coerce here so
    ///     <c>default: 6</c> arrives at <see cref="TemplateResolver.Substitute"/>
    ///     as an int (formats as <c>6</c>) and not the string <c>"6"</c>
    ///     (which would format identically here but matters for downstream
    ///     editor surfaces).
    /// </summary>
    private static object? CoerceDefault(string? type, string? raw)
    {
        if (raw is null) return null;
        var t = (type ?? "string").Trim().ToLowerInvariant();
        return t switch
        {
            "int" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? (object)i
                : raw,
            "number" => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                ? (object)d
                : raw,
            "bool" => bool.TryParse(raw, out var b) ? (object)b : raw,
            _ => raw
        };
    }
}
