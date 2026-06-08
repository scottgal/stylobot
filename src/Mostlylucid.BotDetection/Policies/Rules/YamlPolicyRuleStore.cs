using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Policies.Predicate;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     YAML-backed <see cref="IPolicyRuleStore"/>. Two flavours:
///
///     <list type="bullet">
///       <item><description>
///         <see cref="FromEmbeddedResources"/> -- ships seed rules with the FOSS
///         assembly. No file-watcher: the corpus is fixed at process start.
///         Used at host boot so the dashboard has something to render out of
///         the box.
///       </description></item>
///       <item><description>
///         <see cref="FromDirectory"/> -- watches a directory on disk, debounces
///         editor saves, and fires <see cref="Changed"/> on reload. The dev
///         loop and the FOSS local-config story sit on this constructor.
///       </description></item>
///     </list>
///
///     <para>
///     Rules are parsed with <see cref="PredicateParser.Parse"/>, kept in an
///     immutable snapshot, and indexed by id and by scope. A malformed file
///     (bad YAML, bad Guid, bad predicate) is logged at <c>Warning</c> and
///     skipped; one rotten file never blocks host boot.
///     </para>
/// </summary>
public sealed class YamlPolicyRuleStore : IPolicyRuleStore, IDisposable
{
    private readonly ILogger<YamlPolicyRuleStore> _logger;
    private readonly Assembly? _assembly;
    private readonly string? _resourcePrefix;
    private readonly string? _directory;

    // Debounced FS watcher state.
    private FileSystemWatcher? _watcher;
    private readonly object _reloadGate = new();
    private DateTime _lastReload = DateTime.MinValue;
    private const int DebounceMs = 250;

    // Immutable snapshot. The dictionary is replaced under _reloadGate after a
    // successful reload so readers never observe a half-built corpus.
    private volatile RuleSnapshot _snapshot = RuleSnapshot.Empty;

    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;

    private YamlPolicyRuleStore(
        ILogger<YamlPolicyRuleStore>? logger,
        Assembly? assembly,
        string? resourcePrefix,
        string? directory)
    {
        _logger = logger ?? NullLogger<YamlPolicyRuleStore>.Instance;
        _assembly = assembly;
        _resourcePrefix = resourcePrefix;
        _directory = directory;
    }

    /// <summary>
    ///     Build a store that loads seed rules from embedded resources in
    ///     <paramref name="assembly"/>. <paramref name="resourcePrefix"/>
    ///     filters resource names (e.g.
    ///     <c>"Mostlylucid.BotDetection.Policies.Rules.SeedRules."</c>).
    /// </summary>
    public static YamlPolicyRuleStore FromEmbeddedResources(
        Assembly assembly,
        string resourcePrefix,
        ILogger<YamlPolicyRuleStore>? logger = null)
        => new(logger, assembly, resourcePrefix, directory: null);

    /// <summary>
    ///     Build a store that watches <paramref name="directory"/> on disk and
    ///     hot-reloads on edits.
    /// </summary>
    public static YamlPolicyRuleStore FromDirectory(
        string directory,
        ILogger<YamlPolicyRuleStore>? logger = null)
        => new(logger, assembly: null, resourcePrefix: null, directory);

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        Reload(initialLoad: true);

        // Start the watcher AFTER the first load so we don't race the initial
        // read with watcher-driven reloads while the snapshot is empty.
        if (_directory is not null && _watcher is null)
        {
            try
            {
                _watcher = new FileSystemWatcher(_directory)
                {
                    Filter = "*.yaml",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += (s, e) => OnFsEvent(s, e);
                _watcher.Error += (_, e) =>
                    _logger.LogWarning(e.GetException(), "Policy rule watcher error in {Directory}", _directory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start policy rule watcher in {Directory}", _directory);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
    {
        var snap = _snapshot;
        if (snap.ByScope.TryGetValue(scope, out var list))
            return Task.FromResult<IReadOnlyList<PolicyRule>>(list);
        return Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(
        IReadOnlyList<PolicyScope> scopePath,
        CancellationToken ct = default)
    {
        var snap = _snapshot;
        var result = new List<PolicyRule>();
        foreach (var scope in scopePath)
        {
            if (!snap.ByScope.TryGetValue(scope, out var atScope)) continue;
            // Within a scope: ascending priority (already sorted in the snapshot).
            result.AddRange(atScope);
        }
        return Task.FromResult<IReadOnlyList<PolicyRule>>(result);
    }

    /// <inheritdoc />
    public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var snap = _snapshot;
        snap.ById.TryGetValue(id, out var rule);
        return Task.FromResult(rule);
    }

    // -------- Reload pipeline --------

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(Path.GetExtension(e.FullPath), ".yaml", StringComparison.OrdinalIgnoreCase))
            return;

        DateTime now;
        lock (_reloadGate)
        {
            now = DateTime.UtcNow;
            if ((now - _lastReload).TotalMilliseconds < DebounceMs) return;
            _lastReload = now;
        }

        try
        {
            var previous = _snapshot;
            Reload(initialLoad: false);
            FireChangedForDelta(previous, _snapshot, e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy rule reload failed for {Path}", e.FullPath);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "VYaml [YamlObject] formatters for YamlRuleFile/Scope/Action are registered in VYamlBootstrap.")]
    private void Reload(bool initialLoad)
    {
        var byId = new Dictionary<Guid, PolicyRule>();
        var byScope = new Dictionary<PolicyScope, List<PolicyRule>>();

        foreach (var (sourceLabel, bytes) in EnumerateYamlSources())
        {
            if (bytes.Length == 0) continue;

            YamlRuleFile? yamlFile;
            try
            {
                yamlFile = YamlSerializer.Deserialize<YamlRuleFile>(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping malformed policy rule file {Source}", sourceLabel);
                continue;
            }

            if (yamlFile is null) continue;

            if (!Guid.TryParse(yamlFile.Id, out var id))
            {
                _logger.LogWarning("Skipping policy rule {Source} -- invalid id {Id}", sourceLabel, yamlFile.Id);
                continue;
            }

            PolicyScope scope;
            try { scope = MapScope(yamlFile.Scope); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping policy rule {Source} -- invalid scope", sourceLabel);
                continue;
            }

            PolicyAction action;
            try { action = MapAction(yamlFile.Action); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping policy rule {Source} -- invalid action", sourceLabel);
                continue;
            }

            Predicate.Predicate predicate;
            try { predicate = PredicateParser.Parse(yamlFile.Predicate); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping policy rule {Source} -- predicate failed to parse: {Predicate}",
                    sourceLabel, yamlFile.Predicate);
                continue;
            }

            var mode = MapMode(yamlFile.Mode);

            var rule = new PolicyRule(
                Id: id,
                Scope: scope,
                Priority: yamlFile.Priority,
                Predicate: predicate,
                Action: action,
                Mode: mode,
                Notes: yamlFile.Notes ?? string.Empty,
                Source: sourceLabel,
                CreatedAt: DateTimeOffset.UtcNow,
                RevisionId: Guid.NewGuid());

            byId[rule.Id] = rule;
            if (!byScope.TryGetValue(scope, out var atScope))
            {
                atScope = new List<PolicyRule>();
                byScope[scope] = atScope;
            }
            atScope.Add(rule);
        }

        // Sort each scope bucket by priority ascending, ties broken by id for determinism.
        foreach (var bucket in byScope.Values)
            bucket.Sort(static (a, b) =>
            {
                var c = a.Priority.CompareTo(b.Priority);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });

        var newScope = byScope.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<PolicyRule>)kv.Value.ToArray());

        _snapshot = new RuleSnapshot(byId, newScope);

        if (initialLoad)
            _logger.LogInformation("Loaded {Count} policy rule(s)", byId.Count);
    }

    private IEnumerable<(string Source, byte[] Bytes)> EnumerateYamlSources()
    {
        if (_assembly is not null && _resourcePrefix is not null)
        {
            foreach (var resourceName in _assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(_resourcePrefix, StringComparison.Ordinal)) continue;
                if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

                byte[] bytes;
                try
                {
                    using var stream = _assembly.GetManifestResourceStream(resourceName);
                    if (stream is null) continue;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read embedded policy rule {Resource}", resourceName);
                    continue;
                }
                yield return ($"embedded:{resourceName}", bytes);
            }
            yield break;
        }

        if (_directory is not null && Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.yaml", SearchOption.TopDirectoryOnly))
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read policy rule {Path}", path);
                    continue;
                }
                yield return ($"yaml:{Path.GetFileName(path)}", bytes);
            }
        }
    }

    // -------- Mapping helpers --------

    private static PolicyScope MapScope(YamlRuleScope scope)
    {
        var kind = (scope.Kind ?? "wildcard").Trim().ToLowerInvariant();
        return kind switch
        {
            "wildcard" => new PolicyScope.Wildcard(),

            "domain" => new PolicyScope.Domain(
                scope.Domain ?? throw new InvalidDataException("domain scope requires 'domain'")),

            "subdomain" => new PolicyScope.Subdomain(
                scope.Domain ?? throw new InvalidDataException("subdomain scope requires 'domain'"),
                scope.Subdomain ?? throw new InvalidDataException("subdomain scope requires 'subdomain'")),

            "endpoint" => new PolicyScope.Endpoint(
                scope.Domain ?? throw new InvalidDataException("endpoint scope requires 'domain'"),
                scope.Subdomain ?? throw new InvalidDataException("endpoint scope requires 'subdomain'"),
                scope.PathTemplate ?? throw new InvalidDataException("endpoint scope requires 'path_template'")),

            _ => throw new InvalidDataException($"unknown scope kind '{scope.Kind}'")
        };
    }

    private static PolicyAction MapAction(YamlRuleAction action)
    {
        var kind = (action.Kind ?? "observe").Trim().ToLowerInvariant();
        return kind switch
        {
            "allow" => new PolicyAction.Allow(),
            "observe" => new PolicyAction.Observe(),
            "block" => new PolicyAction.Block(),
            "tag" => new PolicyAction.Tag(
                action.TagName ?? throw new InvalidDataException("tag action requires 'tag_name'")),
            "challenge" => new PolicyAction.Challenge(
                action.ChallengeKind ?? throw new InvalidDataException("challenge action requires 'challenge_kind'")),
            "rate_limit" or "ratelimit" => new PolicyAction.RateLimit(
                action.RequestsPerMinute ?? throw new InvalidDataException("rate_limit action requires 'requests_per_minute'")),
            _ => throw new InvalidDataException($"unknown action kind '{action.Kind}'")
        };
    }

    private static PolicyMode MapMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return PolicyMode.Live;
        return Enum.TryParse<PolicyMode>(raw.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : PolicyMode.Live;
    }

    private void FireChangedForDelta(RuleSnapshot before, RuleSnapshot after, string touchedFile)
    {
        var handler = Changed;
        if (handler is null) return;

        // If the same logical id existed before, fire for its old scope so listeners that
        // had cached rules under the OLD scope still get notified to refresh.
        var sourceLabel = $"yaml:{Path.GetFileName(touchedFile)}";

        // Find rules whose Source matches the touched file in the new snapshot.
        var nowAtFile = after.ById.Values.Where(r => r.Source == sourceLabel).Select(r => r.Scope).ToList();
        var thenAtFile = before.ById.Values.Where(r => r.Source == sourceLabel).Select(r => r.Scope).ToList();

        var scopes = nowAtFile.Concat(thenAtFile).Distinct().ToList();
        if (scopes.Count == 0)
        {
            // Brand-new file or deletion we can't pin -- fire wildcard so caches drop.
            handler.Invoke(this, new PolicyRuleStoreChangedEventArgs(new PolicyScope.Wildcard()));
            return;
        }

        foreach (var scope in scopes)
            handler.Invoke(this, new PolicyRuleStoreChangedEventArgs(scope));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch { /* best effort */ }
    }

    // -------- Immutable snapshot --------

    private sealed record RuleSnapshot(
        IReadOnlyDictionary<Guid, PolicyRule> ById,
        IReadOnlyDictionary<PolicyScope, IReadOnlyList<PolicyRule>> ByScope)
    {
        public static RuleSnapshot Empty { get; } = new(
            new Dictionary<Guid, PolicyRule>(),
            new Dictionary<PolicyScope, IReadOnlyList<PolicyRule>>());
    }
}
