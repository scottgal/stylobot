namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Read-only vocabulary the Policy Stack expression editor (and any other
///     consumer that needs to enumerate / describe / autocomplete signal keys)
///     binds to. Backed by reflection over the <c>SignalKeys</c> constants in
///     <c>Mostlylucid.BotDetection.Models</c>, enriched with the assembly's XML
///     doc summary text and merged with embedded VYaml overlays under
///     <c>Policies/Signals/Overlays/*.yaml</c>.
///
///     <para>
///     The catalogue is immutable once <see cref="SignalCatalog.LoadAsync"/>
///     returns -- detectors that add new <c>SignalKeys</c> constants only show
///     up after the next process restart, which matches every other compile-time
///     vocabulary in this codebase.
///     </para>
/// </summary>
public interface ISignalCatalog
{
    /// <summary>Every catalogued signal in the order returned by reflection.</summary>
    IReadOnlyList<SignalDescriptor> All { get; }

    /// <summary>
    ///     Distinct namespace heads (the segment before the first <c>.</c>) across
    ///     <see cref="All"/>. Editors use this to power the namespace-tabs UI.
    /// </summary>
    IReadOnlyList<string> Namespaces { get; }

    /// <summary>Returns the descriptor for <paramref name="key"/> or null when unknown.</summary>
    SignalDescriptor? TryGet(string key);

    /// <summary>
    ///     Like <see cref="TryGet"/> but throws <see cref="UnknownSignalException"/>
    ///     with Levenshtein-nearest suggestions embedded in the message.
    /// </summary>
    SignalDescriptor Require(string key);

    /// <summary>
    ///     Two-mode search:
    ///     <list type="bullet">
    ///       <item><description>If <paramref name="query"/> contains a <c>.</c> -- exact prefix match against every key.</description></item>
    ///       <item><description>Otherwise -- Levenshtein-nearest-first across every key.</description></item>
    ///     </list>
    ///     Capped at <paramref name="max"/> results.
    /// </summary>
    IEnumerable<SignalDescriptor> Search(string query, int max = 20);

    /// <summary>
    ///     Levenshtein-nearest known keys for <paramref name="key"/>. Used by
    ///     <see cref="UnknownSignalException"/> to power the "did you mean"
    ///     message and by editors to surface inline corrections.
    /// </summary>
    IReadOnlyList<string> SuggestForUnknown(string key, int max = 3);
}
