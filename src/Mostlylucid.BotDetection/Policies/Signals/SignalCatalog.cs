using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Read-side facade over the signal vocabulary. Construction happens once
///     via <see cref="LoadAsync"/>; the resulting instance is immutable and
///     safe to share between requests.
///
///     <para>
///     The constructor pipeline is:
///     </para>
///     <list type="number">
///       <item><description>Reflect every <c>public const string</c> on <c>SignalKeys</c>.</description></item>
///       <item><description>Read the assembly's XML doc file (if present) to populate <c>Short</c>/<c>Long</c>.</description></item>
///       <item><description>Infer <see cref="SignalKind"/> from the first token of the summary.</description></item>
///       <item><description>Apply every embedded YAML overlay on top.</description></item>
///       <item><description>Fall back to a default operator list per kind when the overlay does not specify.</description></item>
///     </list>
/// </summary>
public sealed class SignalCatalog : ISignalCatalog
{
    private readonly Dictionary<string, SignalDescriptor> _byKey;
    private readonly IReadOnlyList<SignalDescriptor> _reflected;
    private readonly IReadOnlyList<string> _reflectedNamespaces;
    private readonly IReadOnlyList<ISignalCatalogSource> _sources;

    private SignalCatalog(IReadOnlyList<SignalDescriptor> reflected, IReadOnlyList<ISignalCatalogSource> sources)
    {
        _reflected = reflected;
        _sources = sources;
        _byKey = new Dictionary<string, SignalDescriptor>(reflected.Count, StringComparer.Ordinal);
        var nsSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in reflected)
        {
            _byKey[d.Key] = d;
            var dotIdx = d.Key.IndexOf('.');
            var ns = dotIdx > 0 ? d.Key[..dotIdx] : d.Key;
            nsSet.Add(ns);
        }
        _reflectedNamespaces = nsSet.ToArray();
    }

    /// <summary>
    ///     Merge every catalogued descriptor (reflected + sources) once, with
    ///     reflected entries winning on key collisions. The result is a fresh
    ///     dictionary -- catalog reads aren't on a per-request hot path
    ///     (autocomplete = once per HTTP request) and source membership is
    ///     dynamic, so caching for staleness would buy a small allocation
    ///     win at the cost of a stale-meter bug surface.
    /// </summary>
    private (IReadOnlyList<SignalDescriptor> All, Dictionary<string, SignalDescriptor> ByKey, IReadOnlyList<string> Namespaces) Snapshot()
    {
        if (_sources.Count == 0)
            return (_reflected, _byKey, _reflectedNamespaces);

        var merged = new Dictionary<string, SignalDescriptor>(_byKey, StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            IReadOnlyList<SignalDescriptor>? supplied;
            try
            {
                supplied = source.GetDescriptors();
            }
            catch
            {
                // A faulty source must not take down the catalog.
                supplied = null;
            }
            if (supplied is null) continue;

            foreach (var d in supplied)
            {
                if (d is null) continue;
                // TryAdd semantics -- reflected entries always win.
                if (merged.ContainsKey(d.Key)) continue;
                merged[d.Key] = d;
            }
        }

        // Stable order: reflected entries first (preserves their existing
        // ordering), then source entries in iteration order.
        var all = new List<SignalDescriptor>(merged.Count);
        foreach (var d in _reflected) all.Add(d);
        foreach (var kvp in merged)
        {
            if (_byKey.ContainsKey(kvp.Key)) continue;
            all.Add(kvp.Value);
        }

        var nsSet = new SortedSet<string>(_reflectedNamespaces, StringComparer.Ordinal);
        foreach (var d in all)
        {
            if (_byKey.ContainsKey(d.Key)) continue;
            var dotIdx = d.Key.IndexOf('.');
            var ns = dotIdx > 0 ? d.Key[..dotIdx] : d.Key;
            nsSet.Add(ns);
        }

        return (all, merged, nsSet.ToArray());
    }

    /// <inheritdoc />
    public IReadOnlyList<SignalDescriptor> All => Snapshot().All;

    /// <inheritdoc />
    public IReadOnlyList<string> Namespaces => Snapshot().Namespaces;

    /// <inheritdoc />
    public SignalDescriptor? TryGet(string key)
        => Snapshot().ByKey.TryGetValue(key, out var d) ? d : null;

    /// <inheritdoc />
    public SignalDescriptor Require(string key)
    {
        if (Snapshot().ByKey.TryGetValue(key, out var d)) return d;
        var suggestions = SuggestForUnknown(key, 3);
        throw new UnknownSignalException(key, suggestions);
    }

    /// <inheritdoc />
    public IEnumerable<SignalDescriptor> Search(string query, int max = 20)
    {
        if (string.IsNullOrEmpty(query) || max <= 0) yield break;

        var snapshot = Snapshot();
        var all = snapshot.All;

        if (query.Contains('.', StringComparison.Ordinal))
        {
            var count = 0;
            foreach (var d in all)
            {
                if (count >= max) yield break;
                if (d.Key.StartsWith(query, StringComparison.Ordinal))
                {
                    count++;
                    yield return d;
                }
            }
            yield break;
        }

        // Fuzzy mode: rank every key by a composite distance.
        // Plain Levenshtein over the full key swamps short queries (e.g. "rj"
        // against 'ua.raw' wins by length alone), so we fold in a subsequence
        // bonus -- keys whose every query character appears in order get their
        // distance halved. That lets typed acronyms ('rj' -> 'risk.justification')
        // land near the top without abandoning Levenshtein for typo correction.
        var scored = new List<(int score, SignalDescriptor d)>(all.Count);
        foreach (var d in all)
            scored.Add((FuzzyScore(query, d.Key), d));
        scored.Sort(static (a, b) =>
        {
            var c = a.score.CompareTo(b.score);
            return c != 0 ? c : string.CompareOrdinal(a.d.Key, b.d.Key);
        });
        for (var i = 0; i < scored.Count && i < max; i++)
            yield return scored[i].d;
    }

    private static int FuzzyScore(string query, string candidate)
    {
        var lev = Levenshtein(query, candidate);
        if (IsSubsequenceCaseInsensitive(query, candidate))
        {
            // Halve the distance and apply a small constant pull so a subsequence
            // hit on a long key still beats a far-Levenshtein-but-similar-length
            // miss. Use Math.Max to keep the result non-negative.
            return Math.Max(0, lev / 2 - 1);
        }
        return lev;
    }

    private static bool IsSubsequenceCaseInsensitive(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query)) return true;
        var qi = 0;
        foreach (var c in candidate)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
                if (qi == query.Length) return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SuggestForUnknown(string key, int max = 3)
    {
        var all = Snapshot().All;
        if (all.Count == 0 || max <= 0) return Array.Empty<string>();
        var scored = new List<(int dist, string key)>(all.Count);
        foreach (var d in all)
            scored.Add((Levenshtein(key, d.Key), d.Key));
        scored.Sort(static (a, b) =>
        {
            var c = a.dist.CompareTo(b.dist);
            return c != 0 ? c : string.CompareOrdinal(a.key, b.key);
        });
        var take = Math.Min(max, scored.Count);
        var result = new string[take];
        for (var i = 0; i < take; i++) result[i] = scored[i].key;
        return result;
    }

    /// <summary>
    ///     Build a fully-populated catalogue from <paramref name="bdAssembly"/>
    ///     (typically <c>typeof(SignalKeys).Assembly</c>). The method is async only
    ///     because future overlay sources (URL pulls, content packs, etc.) will
    ///     want to be -- today's implementation is synchronous under the await.
    ///     <para>
    ///         Optional <paramref name="sources"/> contribute additional
    ///         descriptors lazily on read (see <see cref="ISignalCatalogSource"/>).
    ///         Reflected entries always win on key collisions.
    ///     </para>
    /// </summary>
    [RequiresUnreferencedCode("Drives SignalKeysReflector which inspects const fields by type name.")]
    public static Task<SignalCatalog> LoadAsync(
        Assembly bdAssembly,
        IEnumerable<ISignalCatalogSource>? sources = null)
    {
        var docs = XmlDocCommentReader.Load(bdAssembly);
        var overlays = LoadOverlays(bdAssembly);

        var descriptors = new List<SignalDescriptor>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reflected in SignalKeysReflector.Reflect(bdAssembly))
        {
            if (!seenKeys.Add(reflected.KeyValue)) continue;

            var summary = docs.TryGetSummary(reflected.FieldName) ?? string.Empty;
            var (shortText, longText) = SplitShortLong(summary);
            var inferredKind = InferKind(summary);

            overlays.TryGetValue(reflected.KeyValue, out var overlay);
            var kind = ResolveKind(inferredKind, overlay);
            var operators = ResolveOperators(kind, overlay);
            var examples = ResolveExamples(overlay);
            var related = overlay?.Related is { Length: > 0 } r
                ? (IReadOnlyList<string>)r.ToArray()
                : Array.Empty<string>();

            descriptors.Add(new SignalDescriptor(
                Key: reflected.KeyValue,
                Kind: kind,
                Short: shortText,
                Long: longText,
                DeclaringType: reflected.DeclaringType,
                Operators: operators,
                Examples: examples,
                Related: related));
        }

        var sourceArray = sources is null
            ? Array.Empty<ISignalCatalogSource>()
            : sources.Where(s => s is not null).ToArray();

        return Task.FromResult(new SignalCatalog(descriptors, sourceArray));
    }

    // ----- Kind inference + overlay resolution -----

    private static SignalKind InferKind(string summary)
    {
        if (string.IsNullOrEmpty(summary)) return SignalKind.String;
        // First token before ':' -- the convention is "String: ...", "Bool: ...", etc.
        var colon = summary.IndexOf(':');
        if (colon <= 0) return SignalKind.String;
        var head = summary[..colon].Trim();
        return head switch
        {
            var s when s.Equals("Bool", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Boolean", StringComparison.OrdinalIgnoreCase)
                => SignalKind.Bool,
            var s when s.Equals("Float", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Double", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Int", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Integer", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Long", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Number", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("Decimal", StringComparison.OrdinalIgnoreCase)
                => SignalKind.Number,
            var s when s.Equals("Array", StringComparison.OrdinalIgnoreCase)
                       || s.Equals("List", StringComparison.OrdinalIgnoreCase)
                => SignalKind.Array,
            var s when s.Equals("String", StringComparison.OrdinalIgnoreCase)
                => SignalKind.String,
            _ => SignalKind.String
        };
    }

    private static SignalKind ResolveKind(SignalKind inferred, SignalOverlay? overlay)
    {
        var raw = overlay?.Kind ?? overlay?.ValueKind;
        if (string.IsNullOrEmpty(raw)) return inferred;
        if (Enum.TryParse<SignalKind>(raw, ignoreCase: true, out var parsed)) return parsed;
        return inferred;
    }

    private static IReadOnlyList<string> ResolveOperators(SignalKind kind, SignalOverlay? overlay)
    {
        if (overlay?.Operators is { Length: > 0 } supplied)
            return supplied.ToArray();
        return DefaultOperators(kind);
    }

    private static IReadOnlyList<string> DefaultOperators(SignalKind kind) => kind switch
    {
        SignalKind.Bool => new[] { "=" },
        SignalKind.Number => new[] { "=", "!=", ">=", ">", "<=", "<", "between" },
        SignalKind.String => new[] { "=", "!=", "in", "not in", "matches", "contains" },
        SignalKind.EnumOf => new[] { "=", "!=", "in", "not in" },
        SignalKind.Array => new[] { "contains", "any in", "all in" },
        _ => new[] { "=" }
    };

    private static IReadOnlyList<SignalExample> ResolveExamples(SignalOverlay? overlay)
    {
        if (overlay?.Examples is not { Length: > 0 } src) return Array.Empty<SignalExample>();
        var result = new SignalExample[src.Length];
        for (var i = 0; i < src.Length; i++)
            result[i] = new SignalExample(src[i].Expr, src[i].Reads);
        return result;
    }

    /// <summary>
    ///     Split the summary text into a first-sentence headline (everything up to
    ///     and including the first period) and a "long" body (the full summary).
    ///     When no period is present the entire text becomes both fields.
    /// </summary>
    private static (string Short, string Long) SplitShortLong(string summary)
    {
        if (string.IsNullOrEmpty(summary)) return (string.Empty, string.Empty);
        var period = summary.IndexOf('.');
        if (period < 0) return (summary, summary);
        // Include the period itself so the headline reads as a complete sentence.
        return (summary[..(period + 1)].Trim(), summary.Trim());
    }

    // ----- Overlay loading -----

    private const string OverlayResourceMarker = "Policies.Signals.Overlays.";

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "VYaml [YamlObject] formatters for SignalOverlay are registered in VYamlBootstrap, keeping AOT happy.")]
    private static Dictionary<string, SignalOverlay> LoadOverlays(Assembly assembly)
    {
        var result = new Dictionary<string, SignalOverlay>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(OverlayResourceMarker, StringComparison.Ordinal))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length == 0) continue;

                var overlay = YamlSerializer.Deserialize<SignalOverlay>(bytes);
                if (overlay is null || string.IsNullOrEmpty(overlay.Key)) continue;
                result[overlay.Key] = overlay;
            }
            catch
            {
                // A malformed overlay must not blow up catalogue construction --
                // the worst it can do is leave that one signal at its defaults.
            }
        }
        return result;
    }

    // ----- Levenshtein -----

    /// <summary>
    ///     Iterative two-row Levenshtein distance. Vocabulary keys are short
    ///     (under 64 chars in practice) so the O(n*m) cost is negligible.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                var sub = prev[j - 1] + cost;
                var min = del < ins ? del : ins;
                if (sub < min) min = sub;
                curr[j] = min;
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
