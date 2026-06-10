using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     Loads the embedded <c>tls-reference-corpus.yaml</c> at startup and
///     answers reference lookups for the TLS fingerprint contributor.
///     Thread-safe: <see cref="ReplaceCorpus"/> swaps the internal indices
///     atomically so the refresh service (Plan 2b) can hot-swap without
///     locking readers.
/// </summary>
public sealed class Ja3ReferenceIndex : IJa3ReferenceIndex
{
    private const string EmbeddedResourceSuffix = "tls-reference-corpus.yaml";

    private readonly ILogger<Ja3ReferenceIndex>? _logger;

    // Both indices are reference-replaced wholesale by ReplaceCorpus, so
    // readers see either the old set or the new set, never a partial mix.
    private IReadOnlyDictionary<(string browser, bool mobile), IReadOnlyList<Ja3Reference>> _byBrowser
        = new Dictionary<(string, bool), IReadOnlyList<Ja3Reference>>();

    private IReadOnlyDictionary<string, Ja3Reference> _byHash
        = new Dictionary<string, Ja3Reference>(StringComparer.OrdinalIgnoreCase);

    public Ja3ReferenceIndex(ILogger<Ja3ReferenceIndex>? logger = null)
    {
        _logger = logger;
        LoadEmbeddedCorpus();
    }

    public int Count => _byHash.Count;

    public Ja3Reference? GetReference(string browser, int maxVersion, bool mobile)
    {
        if (string.IsNullOrEmpty(browser)) return null;
        var key = (browser.ToLowerInvariant(), mobile);
        if (!_byBrowser.TryGetValue(key, out var refs)) return null;
        // refs are pre-sorted by descending version; return the first at or below maxVersion.
        foreach (var r in refs)
            if (r.Version <= maxVersion)
                return r;
        return null;
    }

    public Ja3Reference? MatchAnyVersion(string observedJa3Hash) =>
        !string.IsNullOrEmpty(observedJa3Hash) && _byHash.TryGetValue(observedJa3Hash, out var r) ? r : null;

    /// <summary>
    ///     Replaces the in-memory corpus from a parsed <see cref="Ja3CorpusFile"/>.
    ///     Plan 2b's refresh service calls this after verifying a signed update.
    /// </summary>
    public void ReplaceCorpus(Ja3CorpusFile corpus)
    {
        var byBrowser = new Dictionary<(string, bool), List<Ja3Reference>>();
        var byHash = new Dictionary<string, Ja3Reference>(StringComparer.OrdinalIgnoreCase);

        if (corpus.References != null)
        {
            foreach (var (browserName, perVersion) in corpus.References)
            {
                var browser = browserName.ToLowerInvariant();
                foreach (var (version, platforms) in perVersion)
                {
                    IndexEntry(platforms.Desktop, browser, version, mobile: false, byBrowser, byHash);
                    IndexEntry(platforms.Mobile, browser, version, mobile: true, byBrowser, byHash);
                }
            }
        }

        // Sort each list by descending version so GetReference can scan
        // from newest to oldest and return the first <= maxVersion.
        var sorted = byBrowser.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<Ja3Reference>)kv.Value.OrderByDescending(r => r.Version).ToList());

        _byBrowser = sorted;
        _byHash = byHash;

        _logger?.LogInformation(
            "TLS reference corpus loaded: {Entries} entries across {Browsers} browsers (version {Version}, generated {Generated})",
            byHash.Count, sorted.Keys.Select(k => k.Item1).Distinct().Count(),
            corpus.Version, corpus.GeneratedAt ?? "unknown");
    }

    private void IndexEntry(
        Ja3CorpusEntry? entry, string browser, int version, bool mobile,
        Dictionary<(string, bool), List<Ja3Reference>> byBrowser,
        Dictionary<string, Ja3Reference> byHash)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Ja3) || string.IsNullOrEmpty(entry.Ja3String))
            return;

        var reference = new Ja3Reference
        {
            Browser = browser,
            Version = version,
            Mobile = mobile,
            Ja3Hash = entry.Ja3,
            Ja3String = entry.Ja3String,
            Ja4 = entry.Ja4,
            Http2Settings = entry.H2Settings,
            Source = entry.Source
        };

        if (!byBrowser.TryGetValue((browser, mobile), out var list))
        {
            list = new List<Ja3Reference>();
            byBrowser[(browser, mobile)] = list;
        }
        list.Add(reference);

        // First write wins on hash collisions (corpus shouldn't have any).
        // A collision typically means a botched refresh or a YAML edit that
        // duplicated an entry; logging surfaces it for the operator while
        // leaving detection working with the earlier-loaded reference.
        if (!byHash.TryAdd(entry.Ja3, reference))
        {
            _logger?.LogWarning(
                "TLS reference corpus hash collision for JA3 {Hash}: dropping {Browser} {Version} ({Platform}); retaining earlier entry",
                entry.Ja3, browser, version, mobile ? "mobile" : "desktop");
        }
    }

    private void LoadEmbeddedCorpus()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(EmbeddedResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (name == null)
        {
            _logger?.LogWarning("Embedded TLS reference corpus not found ({Suffix})", EmbeddedResourceSuffix);
            return;
        }

        try
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var corpus = YamlSerializer.Deserialize<Ja3CorpusFile>(ms.ToArray());
            if (corpus != null) ReplaceCorpus(corpus);
        }
        catch (TypeInitializationException ex) when (ex.InnerException is MissingMethodException)
        {
            // Known VYaml + Native AOT limitation: BuiltinResolver's
            // FormatterCache<T> cctor calls Activator.CreateInstance on
            // doubly-nested closed-generic dictionaries, which the AOT
            // trimmer strips. Explicit GeneratedResolver registration in
            // VYamlBootstrap was attempted but the BuiltinResolver cctor
            // still runs because VYaml's source-gen Deserialize routes
            // certain lookups through StandardResolver before consulting
            // the composite chain. The fix is upstream: either VYaml's
            // source generator pre-emits formatters for nested closed
            // generics, or we flatten the model with a named wrapper
            // class (breaking change to tls-reference-corpus.yaml).
            //
            // Daemon stays up; JA3 corpus matching is offline until this
            // is resolved. WARN (not ERR) because it's a known limitation
            // documented here, not a fault to wake oncall.
            _logger?.LogWarning(
                "TLS reference corpus could not be loaded under Native AOT due to a known VYaml " +
                "nested-generic limitation (see Ja3ReferenceIndex.LoadEmbeddedCorpus catch block). " +
                "JA3 corpus matching will be offline; the rest of the detection pipeline is unaffected. " +
                "Resource: {Resource}",
                name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load embedded TLS reference corpus from {Resource}", name);
        }
    }
}
