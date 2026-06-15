using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Builds the <c>signal_key -&gt; emitting detector(s)</c> inverted index
///     consumed by <see cref="SignalCatalog.LoadAsync(System.Reflection.Assembly, System.Collections.Generic.IEnumerable{ISignalCatalogSource}?, System.Collections.Generic.IReadOnlyDictionary{string, System.Collections.Generic.IReadOnlyList{string}}?)"/>.
///
///     <para>
///     The source of truth is each detector's <c>*.detector.yaml</c> manifest --
///     specifically the <c>emits.on_complete</c>, <c>emits.on_start</c>,
///     <c>emits.on_failure</c>, and <c>emits.conditional</c> lists already
///     consumed by <see cref="DetectorManifestLoader.GetEmittedSignals"/>.
///     We invert that map so the dashboard's "Source" column can show
///     <c>"HeaderContributor"</c> instead of the useless
///     <c>"SignalKeys"</c> that const-reflection produces.
///     </para>
///
///     <para>
///     Multiple detectors may emit the same key (e.g. a commercial pack
///     re-emitting a FOSS signal under override conditions); the returned
///     value list preserves the manifest iteration order with duplicates
///     removed.
///     </para>
/// </summary>
public static class SignalEmissionIndex
{
    /// <summary>
    ///     Snapshot the loader's currently-known manifests and build a fresh
    ///     inverted index. Safe to call at any time -- the loader is mutable
    ///     under <c>LoadFromDirectory</c>, so callers that need a stable index
    ///     across a process lifetime should invoke this once after boot wiring
    ///     completes (which is what the dashboard's ISignalCatalog DI
    ///     registration does).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(DetectorManifestLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var working = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var manifest in loader.GetAllDetectorManifests().Values)
        {
            if (manifest is null || string.IsNullOrEmpty(manifest.Name)) continue;
            var emitted = loader.GetEmittedSignals(manifest);
            foreach (var signalKey in emitted)
            {
                if (string.IsNullOrEmpty(signalKey)) continue;

                if (!working.TryGetValue(signalKey, out var list))
                {
                    list = new List<string>(1);
                    working[signalKey] = list;
                }

                // Stable order, dedup defensively -- a manifest that declares
                // the same key in on_complete AND conditional should still
                // surface its detector once.
                if (!list.Contains(manifest.Name, StringComparer.Ordinal))
                    list.Add(manifest.Name);
            }
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(working.Count, StringComparer.Ordinal);
        foreach (var (key, list) in working)
            result[key] = list.ToArray();
        return result;
    }
}