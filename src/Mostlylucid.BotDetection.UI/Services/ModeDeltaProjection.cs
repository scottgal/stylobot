using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     One row of "this mode shifts the identity baseline by X, along these
///     slots". Replaces the deleted "Nearest archetype" column (T11) with the
///     spec's honest framing -- a mode delta is the centroid offset off the
///     parent fingerprint's rolled-up centroid, not a separate archetype.
/// </summary>
/// <param name="Magnitude">L2 distance between identity and mode centroids.</param>
/// <param name="TopSlotLabels">Top-K layout slot labels by per-slot L2 contribution, ordered largest first.</param>
public sealed record ModeDelta(double Magnitude, IReadOnlyList<string> TopSlotLabels);

/// <summary>
///     Read-time projector for the per-mode delta panel on the signature
///     detail page (composite-spec step 7, plan task 15).
///
///     <para>
///     Per spec D5 (<c>project_gateway_data_locality</c>) the projection runs
///     on the gateway and only the small <see cref="ModeDelta"/> record
///     crosses the wire. Raw centroid arrays never leave the gateway.
///     </para>
///
///     <para>
///     The projection is pure -- it is a deterministic function of the two
///     centroids + the layout + the signal-catalog vocabulary. Per
///     <c>feedback_centralised_change_detection</c> there is no private cache
///     here; every read recomputes off whatever the view-model builder
///     already loaded.
///     </para>
/// </summary>
public static class ModeDeltaProjector
{
    /// <summary>
    ///     Project the delta between the identity baseline centroid and a
    ///     per-mode centroid. Returns an L2 magnitude plus the labels of the
    ///     top-K layout slots whose per-slot L2 contribution is largest.
    ///
    ///     <para>
    ///     A slot's contribution is the L2 of the per-dimension deltas inside
    ///     its slot range (slots can span multiple dims under HashLsh /
    ///     BitmaskScaled), so the operator-visible "top slot" reads as "top
    ///     semantic signal", not "top raw dim". Labels are resolved via
    ///     <see cref="ISignalCatalog"/> (Short copy, falling back to the slot
    ///     key) -- no hand-rolled string switch, per
    ///     <c>feedback_no_word_lists</c>.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the two centroids have different lengths.</exception>
    public static ModeDelta Project(
        ReadOnlySpan<float> identityCentroid,
        ReadOnlySpan<float> modeCentroid,
        IdentityVectorLayout layout,
        ISignalCatalog signalCatalog,
        int topK = 3)
    {
        if (identityCentroid.Length != modeCentroid.Length)
            throw new ArgumentException(
                $"Centroid length mismatch: identity={identityCentroid.Length} mode={modeCentroid.Length}",
                nameof(modeCentroid));

        var len = identityCentroid.Length;
        var slotMagnitudes = new Dictionary<string, double>();
        double sumSq = 0;

        // Group per-dim deltas by their owning layout slot. A slot can span
        // multiple dims (HashLsh up to width 16 for hdr.ua_family); slot
        // magnitude is the L2 of its contained dims so the surfaced "top
        // slot" reads as the top semantic signal, not a single dim of a
        // hashed feature.
        foreach (var slot in layout.Slots)
        {
            double slotSq = 0;
            var end = Math.Min(slot.Offset + slot.Width, len);
            for (var i = slot.Offset; i < end; i++)
            {
                var d = (double)modeCentroid[i] - identityCentroid[i];
                slotSq += d * d;
                sumSq += d * d;
            }
            if (slotSq > 0)
                slotMagnitudes[slot.Name] = Math.Sqrt(slotSq);
        }

        if (slotMagnitudes.Count == 0)
            return new ModeDelta(0, Array.Empty<string>());

        var labels = slotMagnitudes
            .OrderByDescending(kv => kv.Value)
            .Take(Math.Max(0, topK))
            .Select(kv => ResolveLabel(kv.Key, signalCatalog))
            .ToArray();

        return new ModeDelta(Math.Sqrt(sumSq), labels);
    }

    /// <summary>
    ///     Resolve a slot key to a human-readable label via the signal
    ///     catalogue (Short text, falling back to the key when the slot has
    ///     no catalog entry). Per spec D4 the inline label uses Short; the
    ///     Long form belongs in the hover tooltip and is not consumed here.
    /// </summary>
    private static string ResolveLabel(string slotKey, ISignalCatalog signalCatalog)
    {
        // T17 audit (2026-06-22): IdentityVectorLayout slot names and SignalKeys
        // constants live in separate namespaces, so signalCatalog.TryGet returns
        // null for every drift-prone layout slot today. Two-stage fallback:
        // catalogue Short text when present, structural normalisation of the slot
        // key otherwise (drop the namespace prefix + replace underscores). Keeps
        // the path open for a future curated-label layer on IdentityVectorSlot
        // without forcing one now.
        var descriptor = signalCatalog.TryGet(slotKey);
        if (descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.Short))
            return descriptor.Short;

        return SlotKeyLabel.ToHumanLabel(slotKey);
    }
}
