using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VYaml.Parser;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
///     FOSS-tier helper that powers the dashboard's YAML config editor. Centralises three
///     things so the dashboard middleware (and tests) don't have to duplicate them:
///     <list type="number">
///       <item><description>Listing all editable detector manifests (slug + parsed name + override-status).</description></item>
///       <item><description>Reading a manifest's embedded YAML and any on-disk override.</description></item>
///       <item><description>Writing/deleting an override safely - slug regex guard, path-traversal check, YAML-parse validation, atomic temp+rename.</description></item>
///     </list>
///
///     <para>
///     <b>Slug vs manifest name:</b> the embedded resource for "HeaderContributor" lives at
///     <c>…detectors.header.detector.yaml</c>. The slug is the leaf token (<c>header</c>); it's
///     what URLs and on-disk override files use because manifest names are CamelCase and
///     don't round-trip through filesystems / URLs cleanly. Slug regex: <c>^[a-z0-9_-]+$</c>.
///     </para>
///
///     <para>
///     The service deliberately does NOT push reload events itself - writes go through the
///     same FileSystemWatcher path as a manual edit, so <see cref="FileSystemConfigurationOverrideSource"/>
///     debounces and emits one <c>ConfigurationChangeNotification</c>. Keeping the contract
///     "edit a file, watcher reloads" makes the editor's behaviour identical to running
///     <c>vim</c> on the same file, which is the FOSS promise.
///     </para>
/// </summary>
/// <summary>
///     Read-only slice of <see cref="ConfigEditorService"/> consumed by the dashboard
///     middleware and by remote-mode dashboard hosts. Split out so a remote
///     implementation can satisfy reads over HTTP without dragging in the write path's
///     file-system / override / atomic-replace dependencies.
/// </summary>
public interface IConfigEditorService
{
    /// <summary>
    ///     List all editable detector manifests with their override status. Async so the
    ///     remote implementation can do HTTP I/O without blocking thread-pool threads.
    /// </summary>
    Task<IReadOnlyList<DetectorManifestSummary>> ListManifestsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Fetch the editor view for a single manifest: embedded YAML, override YAML (if any),
    ///     and the "effective" YAML the editor seeds with. Returns null when slug is unknown.
    /// </summary>
    Task<DetectorManifestDocument?> GetManifestAsync(string slug, CancellationToken ct = default);
}

// Not sealed: remote-mode dashboards register a HTTP-backed IConfigEditorService.
// The FOSS surface is read-only by design: there is no Save / Delete on this type and
// no API endpoint that would invoke one. Override editing lives in the commercial
// repo's ConfigEditorController, where it can be gated by licence + auth + RBAC. If
// you find yourself wanting to add a write method here, you are in the wrong repo.
public partial class ConfigEditorService : IConfigEditorService
{
    private const string DetectorYamlSuffix = ".detector.yaml";
    private const string DetectorsSubDir = "detectors";

    [GeneratedRegex(@"^[a-z0-9_-]+$")]
    private static partial Regex GetSlugPattern();
    private static readonly Regex SlugPattern = GetSlugPattern();

    private readonly FileSystemConfigurationOverrideSource _overrideSource;
    private readonly DetectorManifestLoader _loader;
    private readonly ILogger<ConfigEditorService> _logger;

    /// <summary>
    ///     Cache of (slug → embedded YAML) populated once on first access. Embedded resources
    ///     are immutable per release, so a single read is fine.
    /// </summary>
    private Dictionary<string, EmbeddedManifestEntry>? _embedded;
    private readonly object _embeddedLock = new();

    public ConfigEditorService(
        FileSystemConfigurationOverrideSource overrideSource,
        DetectorManifestLoader loader,
        ILogger<ConfigEditorService> logger)
    {
        _overrideSource = overrideSource;
        _loader = loader;
        _logger = logger;
    }

    /// <inheritdoc cref="ListManifests"/>
    public Task<IReadOnlyList<DetectorManifestSummary>> ListManifestsAsync(CancellationToken ct = default)
        => Task.FromResult(ListManifests());

    /// <inheritdoc cref="GetManifest"/>
    public Task<DetectorManifestDocument?> GetManifestAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(GetManifest(slug));

    /// <summary>List all editable detector manifests with their override status.</summary>
    public IReadOnlyList<DetectorManifestSummary> ListManifests()
    {
        var embedded = GetEmbeddedManifests();
        var list = new List<DetectorManifestSummary>(embedded.Count);

        foreach (var (slug, entry) in embedded.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var overrideExists = File.Exists(GetOverridePath(slug));
            list.Add(new DetectorManifestSummary(
                Slug: slug,
                Name: entry.Manifest?.Name ?? slug,
                Priority: entry.Manifest?.Priority ?? 0,
                Enabled: entry.Manifest?.Enabled ?? true,
                Description: entry.Manifest?.Description,
                HasOverride: overrideExists));
        }

        return list;
    }

    /// <summary>
    ///     Fetch the editor view for a single manifest: embedded YAML, override YAML (if any),
    ///     and the "effective" YAML the editor seeds with (override when present, else embedded).
    ///     Returns null when <paramref name="slug"/> is unknown / invalid.
    /// </summary>
    public DetectorManifestDocument? GetManifest(string slug)
    {
        if (!IsValidSlug(slug)) return null;
        var embedded = GetEmbeddedManifests();
        if (!embedded.TryGetValue(slug, out var entry)) return null;

        string? overrideYaml = null;
        DateTime? overrideMtime = null;
        var overridePath = GetOverridePath(slug);
        if (File.Exists(overridePath))
        {
            try
            {
                overrideYaml = File.ReadAllText(overridePath);
                overrideMtime = File.GetLastWriteTimeUtc(overridePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read override file at {Path}", overridePath);
            }
        }

        return new DetectorManifestDocument(
            Slug: slug,
            Name: entry.Manifest?.Name ?? slug,
            EmbeddedYaml: entry.RawYaml,
            OverrideYaml: overrideYaml,
            EffectiveYaml: overrideYaml ?? entry.RawYaml,
            HasOverride: overrideYaml is not null,
            LastModifiedUtc: overrideMtime);
    }

    private static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    private string GetOverridePath(string slug) =>
        Path.Combine(_overrideSource.RootPath, DetectorsSubDir, slug + DetectorYamlSuffix);


    /// <summary>Read the embedded manifest map exactly once and cache it for the process lifetime.</summary>
    private Dictionary<string, EmbeddedManifestEntry> GetEmbeddedManifests()
    {
        if (_embedded is not null) return _embedded;
        lock (_embeddedLock)
        {
            if (_embedded is not null) return _embedded;
            _embedded = LoadEmbeddedManifests();
            return _embedded;
        }
    }

    private Dictionary<string, EmbeddedManifestEntry> LoadEmbeddedManifests()
    {
        var map = new Dictionary<string, EmbeddedManifestEntry>(StringComparer.Ordinal);
        var asm = typeof(DetectorManifest).Assembly;

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(DetectorYamlSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Slug = the second-to-last dot-segment (e.g., "header" from
            // "…detectors.header.detector.yaml"). Lowercase-normalize for URL safety.
            var leaf = resourceName[..^DetectorYamlSuffix.Length];
            var lastDot = leaf.LastIndexOf('.');
            var slug = (lastDot >= 0 ? leaf[(lastDot + 1)..] : leaf).ToLowerInvariant();
            if (!IsValidSlug(slug)) continue; // skip oddly-named resources

            try
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var raw = reader.ReadToEnd();

                DetectorManifest? parsed = null;
                try { parsed = YamlSerializer.Deserialize<DetectorManifest>(Encoding.UTF8.GetBytes(raw)); }
                catch { /* malformed embedded manifest -> still surface in list, just no metadata */ }

                map[slug] = new EmbeddedManifestEntry(raw, parsed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load embedded manifest resource {Resource}", resourceName);
            }
        }

        return map;
    }

    private sealed record EmbeddedManifestEntry(string RawYaml, DetectorManifest? Manifest);
}

public sealed record DetectorManifestSummary(
    string Slug,
    string Name,
    int Priority,
    bool Enabled,
    string? Description,
    bool HasOverride);

public sealed record DetectorManifestDocument(
    string Slug,
    string Name,
    string EmbeddedYaml,
    string? OverrideYaml,
    string EffectiveYaml,
    bool HasOverride,
    DateTime? LastModifiedUtc);

