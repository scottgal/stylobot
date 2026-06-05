using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Loads dashboard tooltip strings from embedded YAML at
///     <c>Definitions/Tooltips/*.yaml</c> and exposes them by key. The view
///     layer's <c>Html.SbTooltip(key)</c> helper looks each one up here.
///
///     The inventory IS the source of truth for tooltip text per the
///     <c>feedback_no_word_lists</c> rule. Adding a tooltip is a YAML edit; the
///     view declares a key and the registry resolves the prose. Missing keys
///     log once at first lookup so unknown keys surface during development
///     without spamming the log for every request that hits the same dead key.
///
///     See <c>docs/architecture/dashboard-tooltips.md</c>.
/// </summary>
public sealed class DashboardTooltipRegistry
{
    private readonly ILogger<DashboardTooltipRegistry> _logger;
    private readonly FrozenDictionary<string, string> _entries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _missedKeys = new();

    public DashboardTooltipRegistry(ILogger<DashboardTooltipRegistry> logger)
    {
        _logger = logger;
        _entries = LoadFromEmbeddedResources();
        _logger.LogInformation("Loaded {Count} dashboard tooltip entries", _entries.Count);
    }

    public int Count => _entries.Count;

    /// <summary>
    ///     Look up the tooltip text for <paramref name="key"/>. Returns false when
    ///     the key is unknown; logs the miss once per process so the missing key
    ///     surfaces in dev without flooding the log for every render.
    /// </summary>
    public bool TryGet(string key, out string text)
    {
        if (_entries.TryGetValue(key, out var t))
        {
            text = t;
            return true;
        }
        if (_missedKeys.TryAdd(key, 0))
            _logger.LogWarning("Dashboard tooltip key '{Key}' has no entry in the YAML inventory", key);
        text = string.Empty;
        return false;
    }

    private FrozenDictionary<string, string> LoadFromEmbeddedResources()
    {
        var assembly = typeof(DashboardTooltipRegistry).Assembly;
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("Tooltips", StringComparison.OrdinalIgnoreCase)) continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dto = YamlSerializer.Deserialize<TooltipInventoryYaml>(ms.ToArray());
                if (dto?.Tooltips is null) continue;

                foreach (var entry in dto.Tooltips)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    if (string.IsNullOrEmpty(entry.Text)) continue;
                    if (!seen.TryAdd(entry.Key, entry.Text))
                        _logger.LogWarning(
                            "Duplicate tooltip key '{Key}' in {Resource}; first wins",
                            entry.Key, resourceName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load tooltip inventory from {Resource}", resourceName);
            }
        }

        return seen.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class TooltipInventoryYaml
{
    public List<TooltipEntryYaml>? Tooltips { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class TooltipEntryYaml
{
    public string? Key { get; set; }
    public string? Text { get; set; }
}
