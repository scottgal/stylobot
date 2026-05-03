using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
public sealed class ConfigEditorService
{
    private const string DetectorYamlSuffix = ".detector.yaml";
    private const string DetectorsSubDir = "detectors";
    private const int MaxYamlBytes = 256 * 1024;
    private static readonly Regex SlugPattern = new("^[a-z0-9_-]+$", RegexOptions.Compiled);

    private readonly FileSystemConfigurationOverrideSource _overrideSource;
    private readonly DetectorManifestLoader _loader;
    private readonly ILogger<ConfigEditorService> _logger;
    private readonly IDeserializer _yamlDeserializer;

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
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

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

    /// <summary>
    ///     Persist an override YAML for <paramref name="slug"/>. Returns a result that the
    ///     dashboard maps to HTTP status: <see cref="SaveOutcome.Ok"/> → 200,
    ///     <see cref="SaveOutcome.InvalidSlug"/> / <see cref="SaveOutcome.PathEscape"/> → 403,
    ///     <see cref="SaveOutcome.UnknownDetector"/> → 404,
    ///     <see cref="SaveOutcome.YamlInvalid"/> → 400 (carries line/col),
    ///     <see cref="SaveOutcome.TooLarge"/> → 413,
    ///     <see cref="SaveOutcome.IoError"/> → 500.
    /// </summary>
    public SaveResult SaveOverride(string slug, string yaml)
    {
        if (!IsValidSlug(slug)) return SaveResult.Failure(SaveOutcome.InvalidSlug);
        if (yaml.Length > MaxYamlBytes) return SaveResult.Failure(SaveOutcome.TooLarge);

        var embedded = GetEmbeddedManifests();
        if (!embedded.ContainsKey(slug)) return SaveResult.Failure(SaveOutcome.UnknownDetector);

        var targetPath = GetOverridePath(slug);
        if (!IsInsideRoot(targetPath)) return SaveResult.Failure(SaveOutcome.PathEscape);

        // Validate YAML parses cleanly. A failed parse must NOT touch the on-disk file -
        // an operator with a syntax error mid-edit would otherwise nuke a previously-good
        // override and have no way to revert.
        try
        {
            _yamlDeserializer.Deserialize<DetectorManifest>(yaml);
        }
        catch (YamlException yx)
        {
            return SaveResult.Failure(SaveOutcome.YamlInvalid,
                error: yx.Message, line: yx.Start.Line, column: yx.Start.Column);
        }
        catch (Exception ex)
        {
            return SaveResult.Failure(SaveOutcome.YamlInvalid, error: ex.Message);
        }

        try
        {
            var dir = Path.GetDirectoryName(targetPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Atomic replace - write to .tmp sibling then File.Move with overwrite.
            // The watcher fires on the rename; the embedded debounce collapses the burst.
            var tmp = targetPath + ".tmp";
            File.WriteAllText(tmp, yaml);
            File.Move(tmp, targetPath, overwrite: true);

            _logger.LogInformation("Wrote detector override {Slug} ({Bytes} bytes)", slug, yaml.Length);
            return SaveResult.Success(targetPath, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write detector override {Slug} to {Path}", slug, targetPath);
            return SaveResult.Failure(SaveOutcome.IoError, error: ex.Message);
        }
    }

    /// <summary>Remove the override file (revert to embedded defaults). Idempotent.</summary>
    public DeleteOutcome DeleteOverride(string slug)
    {
        if (!IsValidSlug(slug)) return DeleteOutcome.InvalidSlug;
        var path = GetOverridePath(slug);
        if (!IsInsideRoot(path)) return DeleteOutcome.PathEscape;

        if (!File.Exists(path)) return DeleteOutcome.NotFound;

        try
        {
            File.Delete(path);
            _logger.LogInformation("Deleted detector override {Slug}", slug);
            return DeleteOutcome.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete detector override {Slug}", slug);
            return DeleteOutcome.IoError;
        }
    }

    private static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    private string GetOverridePath(string slug) =>
        Path.Combine(_overrideSource.RootPath, DetectorsSubDir, slug + DetectorYamlSuffix);

    /// <summary>
    ///     Defence-in-depth - even after the slug regex and Path.Combine, re-resolve the
    ///     absolute path and confirm it sits underneath the override root. Catches exotic
    ///     edge cases (symlink races, NTFS reparse points) that a regex alone wouldn't.
    /// </summary>
    private bool IsInsideRoot(string path)
    {
        var fullTarget = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(_overrideSource.RootPath);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;
        return fullTarget.StartsWith(fullRoot, StringComparison.Ordinal);
    }

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
                try { parsed = _yamlDeserializer.Deserialize<DetectorManifest>(raw); }
                catch { /* malformed embedded manifest → still surface in list, just no metadata */ }

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

/// <summary>Outcome of a save attempt - middleware maps to HTTP status code.</summary>
public enum SaveOutcome
{
    Ok,
    InvalidSlug,
    UnknownDetector,
    PathEscape,
    YamlInvalid,
    TooLarge,
    IoError
}

public sealed record SaveResult(
    SaveOutcome Outcome,
    string? Path = null,
    DateTime? WrittenAtUtc = null,
    string? Error = null,
    long? Line = null,
    long? Column = null)
{
    public bool Ok => Outcome == SaveOutcome.Ok;

    public static SaveResult Success(string path, DateTime writtenAtUtc) =>
        new(SaveOutcome.Ok, path, writtenAtUtc);

    public static SaveResult Failure(SaveOutcome outcome, string? error = null, long? line = null, long? column = null) =>
        new(outcome, Error: error, Line: line, Column: column);
}

public enum DeleteOutcome { Ok, NotFound, InvalidSlug, PathEscape, IoError }