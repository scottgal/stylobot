using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Loads browser-mode predicates from embedded YAML files in
///     <c>Definitions/BrowserModes/*.yaml</c>, compiles them into a small
///     decision walker, and exposes a per-request <see cref="Classify"/> entry
///     point. The YAML inventory IS the source of truth for mode ids and
///     predicates per <c>feedback_no_word_lists</c> — there is no C# enum, no
///     hand-rolled accept/path/header allowlist anywhere.
///
///     Walking order: ascending <c>order</c> field. The first predicate whose
///     clauses all match the raw-values dict + live probe wins. The registry
///     guarantees a fallback id at the floor so <see cref="Classify"/> never
///     returns null even when no other predicate matches.
///
///     See <c>docs/architecture/composite-character-fingerprints.md</c>.
/// </summary>
public sealed class BrowserModeRegistry
{
    private readonly ILogger<BrowserModeRegistry> _logger;
    private readonly string _fallbackModeId;
    private IReadOnlyList<BrowserMode> _modes;

    public BrowserModeRegistry(
        ILogger<BrowserModeRegistry> logger,
        string fallbackModeId)
    {
        _logger = logger;
        _fallbackModeId = fallbackModeId;
        _modes = LoadFromEmbeddedResources();
        _logger.LogInformation(
            "Loaded {Count} browser modes from embedded resources (fallback: {Fallback})",
            _modes.Count, _fallbackModeId);
    }

    public IReadOnlyList<BrowserMode> All => _modes;

    /// <summary>
    ///     Walk the predicates in ascending <c>order</c> until one matches the
    ///     raw-values dict + live probe; return that mode's id. When nothing
    ///     matches, return the configured fallback id.
    /// </summary>
    public string Classify(IReadOnlyDictionary<string, object?> rawValues, IRequestProbe probe)
    {
        foreach (var mode in _modes)
        {
            if (mode.Predicate.Matches(rawValues, probe))
                return mode.ModeId;
        }
        return _fallbackModeId;
    }

    /// <summary>
    ///     Replace the in-memory mode set. Used by a future calibration surface
    ///     that ships an updated YAML inventory at runtime; today's callers seed
    ///     once from embedded resources and leave it alone.
    /// </summary>
    public void Replace(IReadOnlyList<BrowserMode> refreshed)
    {
        _modes = refreshed;
    }

    private IReadOnlyList<BrowserMode> LoadFromEmbeddedResources()
    {
        var assembly = typeof(BrowserModeRegistry).Assembly;
        var results = new List<BrowserMode>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("BrowserModes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dto = YamlSerializer.Deserialize<BrowserModeYaml>(ms.ToArray());
                if (dto is null || string.IsNullOrEmpty(dto.ModeId)) continue;

                results.Add(new BrowserMode
                {
                    ModeId = dto.ModeId,
                    Name = dto.Name ?? dto.ModeId,
                    Description = dto.Description,
                    Order = dto.Order ?? int.MaxValue / 2,
                    Predicate = BrowserModePredicate.Compile(dto.Predicate)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load browser mode from {Resource}", resourceName);
            }
        }

        results.Sort((a, b) => a.Order.CompareTo(b.Order));
        return results;
    }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class BrowserModeYaml
{
    public string? ModeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? Order { get; set; }
    public RequestCharacterPredicateYaml? Predicate { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class RequestCharacterPredicateYaml
{
    public List<string>? MethodIn { get; set; }
    public List<int>? SecFetchPatternIn { get; set; }
    public List<string>? SecFetchDestIn { get; set; }
    public bool? UpgradeInsecureRequests { get; set; }
    public string? AcceptContains { get; set; }
    public List<string>? AcceptIn { get; set; }
    public List<string>? PathMatches { get; set; }
    public List<string>? HasHeader { get; set; }
    public Dictionary<string, string>? HeaderEquals { get; set; }
}
