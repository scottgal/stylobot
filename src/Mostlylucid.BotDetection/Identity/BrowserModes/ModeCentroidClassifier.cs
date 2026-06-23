namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     One nearest-centroid hit produced by <see cref="ModeCentroidClassifier"/>:
///     the <see cref="ModeId"/> that won the cosine search and the raw cosine
///     <see cref="Similarity"/> against the winning centroid. Similarity is
///     surfaced (not just the id) so callers can gate on a confidence threshold
///     without re-running the cosine themselves and so the dashboard can render
///     "matched mode X at 0.92" instead of a bare label.
/// </summary>
public sealed record ModeMatch(string ModeId, double Similarity);

/// <summary>
///     Nearest-centroid mode classifier. Encodes an observation
///     (method, sec-fetch-mode, upgrade-websocket) into the same
///     <see cref="IdentityVectorLayout"/> the identity matcher uses, then
///     walks every mode centroid published by <see cref="ModeCentroidCatalogue"/>
///     and returns the highest-cosine match.
///
///     <para>
///     Mode centroids and identity centroids share the vector layout per the
///     design spec ("mode_centroid - identity_centroid in the same vector
///     space"). Mode centroids are SPARSE identity vectors — only the
///     mode-relevant slots (<c>session.method_pattern</c>,
///     <c>hdr.sec_fetch_pattern</c>, <c>hdr.upgrade_insecure_requests</c>)
///     carry signal; all other slots stay at zero. That sparsity is what makes
///     the centroid an isolated "mode delta" rather than a full identity, and
///     it's why cosine against the matcher's full request vector picks up the
///     mode without dragging in network / locale / UA noise.
///     </para>
///
///     <para>
///     The classifier intentionally avoids any string-switch / rule path. It
///     compares one vector to N centroids; if the layout grows a slot
///     tomorrow, the classifier picks up the new dim for free as soon as
///     <see cref="HardcodedBrowserModeSeedSource"/> (T12) or the YAML loader
///     (T13) starts populating it.
///     </para>
///
///     <para>
///     "unknown" is the catalogue's explicit sentinel row (centroid = all
///     zeros). The classifier never invents the label — it just wins the
///     cosine search when the observation is too sparse to look like anything
///     else.
///     </para>
/// </summary>
public sealed class ModeCentroidClassifier
{
    private readonly IReadOnlyList<(string ModeId, float[] Centroid)> _centroids;
    private readonly IdentityVectorLayout _layout;

    private ModeCentroidClassifier(
        IReadOnlyList<(string, float[])> centroids,
        IdentityVectorLayout layout)
    {
        _centroids = centroids;
        _layout = layout;
    }

    /// <summary>
    ///     Pull every browser-mode row from <paramref name="catalogue"/> (which
    ///     in turn hydrates from the store or seeds on cold-start) and snapshot
    ///     it into a classifier instance. Cheap to call once at startup;
    ///     callers should hold the returned instance, not rebuild per request.
    ///     Subsequent rebuilds pick up drift updates that have landed since
    ///     the previous load.
    /// </summary>
    public static async Task<ModeCentroidClassifier> LoadAsync(
        ModeCentroidCatalogue catalogue,
        IdentityVectorLayout layout,
        CancellationToken ct = default)
    {
        var rows = await catalogue.LoadAsync(ct);
        var centroids = rows.Select(r => (r.ModeId, r.Centroid)).ToList();
        return new ModeCentroidClassifier(centroids, layout);
    }

    /// <summary>
    ///     Classify a single observation by nearest-centroid cosine search.
    ///     <paramref name="method"/> is the raw HTTP method string (case
    ///     preserved — the underlying HashLsh slot is order-sensitive but the
    ///     production call sites already pass "GET" / "POST" canonical-cased).
    ///     <paramref name="secFetchMode"/> is the raw Sec-Fetch-Mode header
    ///     value ("navigate" / "cors" / "no-cors" / empty); it is mapped to
    ///     the canonical sec-fetch presence bitmask the layout's
    ///     <c>hdr.sec_fetch_pattern</c> slot expects so seed and observation
    ///     vectors land in the same space.
    ///     <paramref name="upgradeWebsocket"/> drives the
    ///     <c>hdr.upgrade_insecure_requests</c> bool slot — the layout has no
    ///     dedicated websocket-upgrade slot today, so the bool slot is
    ///     repurposed as the universal "Upgrade header asserts something"
    ///     channel for mode classification.
    /// </summary>
    public ModeMatch Classify(string method, string secFetchMode, bool upgradeWebsocket)
    {
        var obs = ModeVectorEncoder.Encode(_layout, method, secFetchMode, upgradeWebsocket);
        return ClassifyVector(obs);
    }

    /// <summary>
    ///     Lower-level overload: classify a pre-built observation vector. The
    ///     vector must be encoded against the same <see cref="IdentityVectorLayout"/>
    ///     this classifier was constructed with — mismatched layouts throw at
    ///     the cosine length check. Exposed so callers that already encode the
    ///     full identity vector (e.g. the matcher pipeline) can hand it
    ///     straight to the mode classifier without re-extracting the
    ///     mode-relevant fields.
    /// </summary>
    public ModeMatch ClassifyVector(float[] observation)
    {
        var bestId = "unknown";
        var bestSim = double.NegativeInfinity;
        for (var i = 0; i < _centroids.Count; i++)
        {
            var (modeId, centroid) = _centroids[i];
            var sim = CosineSimilarity(observation, centroid);
            if (sim > bestSim)
            {
                bestId = modeId;
                bestSim = sim;
            }
        }

        if (double.IsNegativeInfinity(bestSim)) bestSim = 0;
        return new ModeMatch(bestId, bestSim);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector length mismatch");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
///     Shared sparse encoder used by both the classifier (live observation)
///     and the hardcoded seed source (cold-start centroids). Keeping the
///     encoding in one place is the load-bearing guarantee that seed and
///     observation vectors actually share the same vector space — splitting
///     them would let one side drift and silently mis-score every request.
///
///     <para>
///     The encoder writes exactly three slots in the layout:
///     </para>
///     <list type="bullet">
///         <item><description><c>session.method_pattern</c> (HashLsh, 1
///         slot) — the method string ("GET" / "POST") hashed into the
///         layout's canonical method slot. Identical to what
///         <c>IdentityVectorContributor</c> writes on the live
///         request pipeline so a mode observation lives in the same
///         per-dim space the matcher already understands.</description></item>
///         <item><description><c>hdr.sec_fetch_pattern</c> (BitmaskScaled,
///         1 slot) — the Sec-Fetch-Mode header value maps to the canonical
///         four-header presence bitmask. Empty / missing Sec-Fetch-Mode
///         leaves this slot at zero (the encoder skips it, not a
///         BitmaskScaled(0) write — the two would produce identical floats
///         but the "skipped" semantics let the unknown sentinel centroid
///         stay at all-zeros and pure cosine pick it.)</description></item>
///         <item><description><c>hdr.upgrade_insecure_requests</c> (Bool, 1
///         slot) — repurposed as the universal "Upgrade header present"
///         channel pending a dedicated websocket-upgrade slot.</description></item>
///     </list>
///
///     <para>
///     All other layout slots stay at zero. Sparsity is intentional: it is
///     what makes the cosine "mode delta" semantics work.
///     </para>
/// </summary>
public static class ModeVectorEncoder
{
    /// <summary>
    ///     Build a layout-sized sparse mode vector from the three mode-relevant
    ///     raw inputs. Pure function; no allocations beyond the returned
    ///     float[] so seed-source and per-request callers can both use it
    ///     without performance considerations.
    ///
    ///     <para>
    ///     The encoder writes the canonical slot value for every input it
    ///     can resolve. <paramref name="secFetchMode"/> empty maps to
    ///     bitmask 0 (no Sec-Fetch headers observed) -- that IS a real,
    ///     differentiated signal in BitmaskScaled space (popcount=0 → -1
    ///     vs popcount=4 → 0 for navigation), so the seed for "bot-raw"
    ///     (clients sending no Sec-Fetch headers at all) cosine-matches
    ///     correctly against minimal observations.
    ///     </para>
    /// </summary>
    public static float[] Encode(
        IdentityVectorLayout layout,
        string? method,
        string? secFetchMode,
        bool upgradeWebsocket)
    {
        var v = new float[layout.Dimension];

        if (!string.IsNullOrEmpty(method))
        {
            // session.method_pattern is a HashLsh slot; reuse the identity
            // encoder so the LSH math is identical between mode-classifier
            // and matcher.
            WriteHashLsh(layout, v, slotName: "session.method_pattern", value: method);
        }

        // Empty Sec-Fetch-Mode → bitmask 0 (the canonical popcount-0 value).
        // BitmaskScaled(0) = -1, distinguishable from BitmaskScaled(15)=0
        // (navigation) or BitmaskScaled(7)=-0.25 (cors).
        var pattern = SecFetchModeToBitmask(secFetchMode) ?? 0;
        WriteBitmaskScaled(layout, v, slotName: "hdr.sec_fetch_pattern", mask: pattern);

        // Always write the upgrade slot as a ±1 bool. Bool encoding is
        // present-true / present-false, never absent — the test API takes
        // a bool, not a nullable, so absence isn't representable. The
        // explicit write keeps seed and observation in the same vector
        // space.
        WriteBool(layout, v, slotName: "hdr.upgrade_insecure_requests", value: upgradeWebsocket);

        return v;
    }

    /// <summary>
    ///     Map the raw Sec-Fetch-Mode header value to the four-header
    ///     presence bitmask the layout's <c>hdr.sec_fetch_pattern</c> slot
    ///     expects. Returns null when the header is missing / empty so the
    ///     caller can skip the slot write (leaves the slot at zero AND keeps
    ///     the unknown-sentinel all-zeros centroid intact). The bitmask
    ///     values match the production
    ///     <c>CachingBrowserModeResolver.SecFetchBitmask</c> derivation:
    ///     Dest=1 | Mode=2 | Site=4 | User=8.
    /// </summary>
    public static int? SecFetchModeToBitmask(string? secFetchMode)
    {
        if (string.IsNullOrEmpty(secFetchMode)) return null;
        return secFetchMode.ToLowerInvariant() switch
        {
            // navigate: top-level nav has all four Sec-Fetch headers
            "navigate" => 15,
            // cors: XHR / fetch — Dest+Mode+Site, no User (gesture-less)
            "cors" => 7,
            // no-cors: sub-resource fetch (img / script / css) — Dest+Site;
            // Sec-Fetch-Mode is "no-cors" but the canonical bitmask is
            // narrower because browsers historically omit Mode/User on
            // sub-resource requests. The narrower mask gives the slot a
            // unique BitmaskScaled value vs cors so cosine can separate the
            // two seeds.
            "no-cors" => 5,
            // unknown header value — present but unrecognised. Encode as
            // bare Mode-only (just bit 2) so the slot reads "saw something"
            // without colliding with the canonical 5 / 7 / 15.
            _ => 2,
        };
    }

    private static void WriteHashLsh(IdentityVectorLayout layout, float[] v, string slotName, string value)
    {
        var slot = layout.FindSlot(slotName);
        if (slot is null) return;
        // Reuse IdentityVectorEncoder by calling EncodeRaw with a single
        // raw-value entry — this keeps the HashLsh math (XxHash64, salt,
        // per-width seed) identical between mode and identity vectors with
        // zero copy-paste risk.
        var encoder = new IdentityVectorEncoder(layout);
        var single = encoder.EncodeRaw(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [slotName] = value,
        });
        for (var i = 0; i < slot.Width; i++)
            v[slot.Offset + i] = single[slot.Offset + i];
    }

    private static void WriteBitmaskScaled(IdentityVectorLayout layout, float[] v, string slotName, int mask)
    {
        var slot = layout.FindSlot(slotName);
        if (slot is null) return;
        var ones = System.Numerics.BitOperations.PopCount((uint)mask);
        var maxBits = slot.Width * 8;
        var ratio = maxBits > 0 ? (double)ones / maxBits : 0.0;
        v[slot.Offset] = (float)(ratio * 2.0 - 1.0);
    }

    private static void WriteBool(IdentityVectorLayout layout, float[] v, string slotName, bool value)
    {
        var slot = layout.FindSlot(slotName);
        if (slot is null) return;
        v[slot.Offset] = value ? 1f : -1f;
    }
}
