using System.IO.Hashing;
using System.Text;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Defines the slot layout for an identity vector. Each named feature occupies a known slot
///     range; high-cardinality strings are spread across multiple slots via locality-sensitive
///     hashing. The total dimension count <see cref="Dimension"/> is fixed at deployment startup
///     and stored in <c>identity_vector_layout</c>; changing it requires a layout-version bump
///     and one-shot re-encoding migration.
///     See docs/architecture/fingerprint-match.md.
/// </summary>
public sealed class IdentityVectorLayout
{
    /// <summary>Layout version number; bumped when the slot map changes.</summary>
    public int Version { get; }

    /// <summary>Total vector dimension count (D).</summary>
    public int Dimension { get; }

    private readonly IdentityVectorSlot[] _slots;
    private readonly Dictionary<string, IdentityVectorSlot> _byName;

    public IdentityVectorLayout(int version, IEnumerable<IdentityVectorSlot> slots)
    {
        Version = version;
        _slots = slots.ToArray();
        _byName = _slots.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var sum = 0;
        for (var i = 0; i < _slots.Length; i++) sum += _slots[i].Width;
        Dimension = sum;
    }

    public IReadOnlyList<IdentityVectorSlot> Slots => _slots;

    // Encoder-internal accessor that lets the hot path iterate the array directly
    // without paying for IEnumerable<T>.GetEnumerator() boxing. Public Slots stays
    // typed as IReadOnlyList<T> so existing callers are unaffected.
    internal IdentityVectorSlot[] SlotsArray => _slots;

    /// <summary>Lookup a slot by name; null if no such slot exists in this layout.</summary>
    public IdentityVectorSlot? FindSlot(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    ///     Default v1 layout. Concrete slot list captures every dimension named in the spec's
    ///     vector composition section. Slot widths and offsets are fixed in this version; bumps
    ///     require <see cref="Version"/> increment.
    /// </summary>
    public static IdentityVectorLayout DefaultV1()
    {
        var slots = new List<IdentityVectorSlot>();
        var offset = 0;

        IdentityVectorSlot Add(string name, int width, IdentityVectorEncoding encoding)
        {
            var slot = new IdentityVectorSlot(name, offset, width, encoding);
            slots.Add(slot);
            offset += width;
            return slot;
        }

        // Network
        Add("network.asn", 4, IdentityVectorEncoding.HashLsh);
        Add("network.ip_subnet", 2, IdentityVectorEncoding.HashLsh);
        Add("network.country", 2, IdentityVectorEncoding.HashLsh);
        Add("network.region", 1, IdentityVectorEncoding.HashLsh);
        Add("network.city", 1, IdentityVectorEncoding.HashLsh);
        Add("network.is_datacenter", 1, IdentityVectorEncoding.Bool);
        Add("network.is_vpn", 1, IdentityVectorEncoding.Bool);
        Add("network.is_tor", 1, IdentityVectorEncoding.Bool);

        // Locale
        Add("locale.accept_language_primary", 2, IdentityVectorEncoding.HashLsh);
        Add("locale.accept_language_count", 1, IdentityVectorEncoding.LogNormCount);
        Add("locale.save_data", 1, IdentityVectorEncoding.Bool);

        // Header bag
        Add("hdr.accept", 4, IdentityVectorEncoding.HashLsh);
        Add("hdr.accept_encoding_ordered", 4, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_brands_ordered", 6, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_mobile", 1, IdentityVectorEncoding.Bool);
        Add("hdr.sec_ch_ua_platform", 2, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_arch", 2, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_bitness", 1, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_model", 4, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_ch_ua_full_version", 4, IdentityVectorEncoding.HashLsh);
        Add("hdr.sec_fetch_pattern", 1, IdentityVectorEncoding.BitmaskScaled);
        Add("hdr.header_order_hash", 6, IdentityVectorEncoding.HashLsh);
        Add("hdr.header_case_pattern", 4, IdentityVectorEncoding.HashLsh);
        Add("hdr.upgrade_insecure_requests", 1, IdentityVectorEncoding.Bool);
        Add("hdr.dnt", 1, IdentityVectorEncoding.Bool);
        Add("hdr.sec_gpc", 1, IdentityVectorEncoding.Bool);
        Add("hdr.priority", 1, IdentityVectorEncoding.Bool);
        Add("hdr.cache_control_pragma", 2, IdentityVectorEncoding.HashLsh);
        Add("hdr.te", 1, IdentityVectorEncoding.HashLsh);
        Add("hdr.connection_pattern", 1, IdentityVectorEncoding.HashLsh);

        // HTTP-library tells
        Add("tool.x_requested_with", 1, IdentityVectorEncoding.Bool);
        Add("tool.custom_header_signature", 3, IdentityVectorEncoding.HashLsh);

        // Transport (zero on plaintext; quality slot records that)
        Add("transport.tls_ja4", 4, IdentityVectorEncoding.HashLsh);
        Add("transport.h2_settings_hash", 3, IdentityVectorEncoding.HashLsh);
        Add("transport.alpn", 2, IdentityVectorEncoding.HashLsh);
        Add("transport.tcp_p0f", 3, IdentityVectorEncoding.HashLsh);

        // Session / behavioural
        Add("session.cookie_count", 1, IdentityVectorEncoding.LogNormCount);
        Add("session.has_returning_cookie", 1, IdentityVectorEncoding.Bool);
        Add("session.entry_page_family", 4, IdentityVectorEncoding.HashLsh);
        Add("session.referer_host_family", 4, IdentityVectorEncoding.HashLsh);
        Add("session.request_rate", 1, IdentityVectorEncoding.LogNormCount);
        Add("session.session_age", 1, IdentityVectorEncoding.LogNormCount);
        Add("session.method_pattern", 1, IdentityVectorEncoding.HashLsh);
        Add("session.path_entropy", 1, IdentityVectorEncoding.Scalar);

        // UA family (Chrome / Firefox / Safari / Googlebot / curl / etc., as produced by
        // UserAgentParser.Parse). The literal UA-family identity was previously NOT in the
        // vector, which let umbrella archetypes (googlebot, mastodon) win the cosine pick
        // against chrome-* for any browser XHR / API request whose Sec-Fetch / Sec-Ch-Ua /
        // Upgrade-Insecure-Requests dims collapsed to bot-shaped values under adblockers or
        // by spec. Encoding the UA family directly means archetypes can simply assert what
        // browser they represent and cosine separates families cleanly.
        // v3: widened 2 -> 16 dims. The auto-promotion of ~150 bot-pattern entries to root
        // archetypes (each asserting hdr.ua_family with a unique BotName string) made the
        // 2-dim LSH umbrella-centroid bug visible: two distinct family strings can hash to
        // within ~0.1 per-dim in a 2-d space, which under the Gaussian-NLL scoring (tight
        // variance + uncorrelated dims) reads as a "near match" and lets sparse synthesized
        // archetypes (selenium, bonfire, sharkey) beat rich hand-written ones (chrome-desktop,
        // mobile-safari) for first-request allocation on real browser traffic. At width 8 the
        // BdfReplay HumanScenario chrome / firefox cases recover (sharkey / selenium collisions
        // gone) but safari iOS still drifted to python-requests via a specific-pair collision.
        // 16 dims drops collision probability another order of magnitude and gives clean
        // separation across all the canonical UA family strings the catalog catalogue carries.
        Add("hdr.ua_family", 16, IdentityVectorEncoding.HashLsh);

        // Browser-characteristic consistency (client-attested, script v2.1.0+). Bool
        // dims (+1 present / -1 absent / 0 not-observed, presence-gated out when the
        // beacon never arrived). Populated only for beacon-carrying requests; scored
        // by the browser_char centroid catalogue keyed {family}:{major}:{mode}. The
        // browser_char DimensionMask weights the FEATURE dims LOW (spoofable) and the
        // ENGINE dims HIGH (un-spoofable substrate a fake cannot move), so poisoning
        // the feature dims cannot drag a mature centroid.
        Add("client.feat.popover", 1, IdentityVectorEncoding.Bool);
        Add("client.feat.css_has", 1, IdentityVectorEncoding.Bool);
        Add("client.feat.array_findlast", 1, IdentityVectorEncoding.Bool);
        Add("client.feat.structured_clone", 1, IdentityVectorEncoding.Bool);
        Add("client.feat.webgpu", 1, IdentityVectorEncoding.Bool);
        Add("client.triple.view_tx", 1, IdentityVectorEncoding.Bool);
        Add("client.triple.speculation", 1, IdentityVectorEncoding.Bool);
        Add("client.triple.storage_access", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.v8_break_iterator", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.error_capture_stack", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.stack_v8", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.stack_smjsc", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.regex_lookbehind", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.show_open_file_picker", 1, IdentityVectorEncoding.Bool);
        Add("client.eng.user_agent_data", 1, IdentityVectorEncoding.Bool);

        // Quality
        Add("quality.dimension_presence_ratio", 1, IdentityVectorEncoding.Scalar);
        Add("quality.transport_quality", 1, IdentityVectorEncoding.Scalar);
        Add("quality.cleartext_flag", 1, IdentityVectorEncoding.Bool);
        Add("quality.layout_version", 1, IdentityVectorEncoding.Scalar);

        // v3: hdr.ua_family widened 2 -> 8 dims. Stored v2 centroids will mismatch on the
        // total Dimension count and the matcher's version check will treat them as stale,
        // triggering fresh allocation on the next request per fingerprint. DisplayName
        // (persisted on the Fingerprint row, not the centroid) survives the bump.
        // v4: added 15 client-attested browser-characteristic dims (client.feat.* /
        // client.triple.* / client.eng.*). Same forward-only story -- v3 centroids
        // mismatch the new Dimension count and re-allocate on next request (a one-time
        // warm-up restage). SHIP THIS BUMP ON A QUIET WINDOW, not mid-incident.
        return new IdentityVectorLayout(version: 4, slots);
    }
}

/// <summary>
///     One named feature in the identity vector. <see cref="Offset"/> and <see cref="Width"/>
///     pinpoint the slot range; <see cref="Encoding"/> selects how the raw value is mapped to
///     those slots.
/// </summary>
public sealed record IdentityVectorSlot(
    string Name,
    int Offset,
    int Width,
    IdentityVectorEncoding Encoding);

public enum IdentityVectorEncoding
{
    /// <summary>Locality-sensitive hash spread across the slot's width. Small input changes move only one slot.</summary>
    HashLsh,
    /// <summary>±1 for true/false; 0 if absent.</summary>
    Bool,
    /// <summary>tanh(log(1 + count) / k) into a single slot.</summary>
    LogNormCount,
    /// <summary>Bitmask scaled to [-1, 1] across the slot's width.</summary>
    BitmaskScaled,
    /// <summary>Raw scalar in [0, 1] (or [-1, 1]); written as-is, clamped to slot.</summary>
    Scalar,
}

/// <summary>
///     Encodes raw feature inputs into a layout-conformant float vector. The encoder is stateless
///     and deterministic given the layout; same inputs produce the same vector.
/// </summary>
public sealed class IdentityVectorEncoder
{
    private readonly IdentityVectorLayout _layout;
    private const double LogNormK = 4.0;
    private const ulong LshSalt = 0x5A1D1A0E_BFCE9E11UL;

    public IdentityVectorEncoder(IdentityVectorLayout layout) => _layout = layout;

    public IdentityVectorLayout Layout => _layout;

    /// <summary>
    ///     Build a vector from a map of slot name → raw value. Missing slots stay at 0. The
    ///     resulting vector is L2-normalised at the end so cosine and weighted cosine are
    ///     well-behaved.
    /// </summary>
    public float[] Encode(IReadOnlyDictionary<string, object?> rawValues)
    {
        var v = EncodeCore(rawValues);
        L2Normalise(v);
        return v;
    }

    /// <summary>
    ///     Encode without the terminal L2 normalization. Use when comparing raw signal magnitudes
    ///     between an observation and a centroid is more meaningful than comparing positions on
    ///     the unit hypersphere. Required for variance-aware scoring in IdentityArchetypeRegistry.
    /// </summary>
    public float[] EncodeRaw(IReadOnlyDictionary<string, object?> rawValues)
    {
        return EncodeCore(rawValues);
    }

    private float[] EncodeCore(IReadOnlyDictionary<string, object?> rawValues)
    {
        var v = new float[_layout.Dimension];
        var presentSlotCount = 0;

        // Hoisted UTF-8 scratch buffer for HashLsh slots. Real values (ASN strings, country
        // codes, accept-encoding lists, JA4 hashes, header-order hashes) are well under 256
        // bytes; longer strings fall back to a heap byte[] so correctness is preserved.
        // Hoisting outside the loop avoids stackalloc-in-loop pessimisation by the JIT.
        Span<byte> stackBuf = stackalloc byte[256];

        // Index over the concrete array rather than `foreach (var slot in _layout.Slots)`;
        // the public Slots is typed IReadOnlyList<T>, whose enumerator is boxed per call
        // (~80 B/req in MemoryDiagnoser). The internal SlotsArray accessor lets the JIT
        // emit straight bounds-checked array indexing here.
        var slots = _layout.SlotsArray;
        for (var sx = 0; sx < slots.Length; sx++)
        {
            var slot = slots[sx];
            if (!rawValues.TryGetValue(slot.Name, out var raw) || raw is null)
                continue;

            switch (slot.Encoding)
            {
                case IdentityVectorEncoding.Bool:
                    v[slot.Offset] = raw switch
                    {
                        bool b => b ? 1f : -1f,
                        _ => 0f
                    };
                    break;

                case IdentityVectorEncoding.LogNormCount:
                    v[slot.Offset] = (float)Math.Tanh(Math.Log(1.0 + ToDouble(raw)) / LogNormK);
                    break;

                case IdentityVectorEncoding.Scalar:
                    v[slot.Offset] = (float)Math.Clamp(ToDouble(raw), -1.0, 1.0);
                    break;

                case IdentityVectorEncoding.BitmaskScaled:
                    {
                        var mask = ToInt(raw);
                        var ones = System.Numerics.BitOperations.PopCount((uint)mask);
                        var maxBits = slot.Width * 8;
                        var ratio = maxBits > 0 ? (double)ones / maxBits : 0.0;
                        v[slot.Offset] = (float)(ratio * 2.0 - 1.0);
                    }
                    break;

                case IdentityVectorEncoding.HashLsh:
                    {
                        var s = raw as string ?? raw.ToString() ?? string.Empty;
                        var byteCount = Encoding.UTF8.GetByteCount(s);
                        Span<byte> bytes = byteCount <= stackBuf.Length
                            ? stackBuf[..byteCount]
                            : new byte[byteCount];
                        Encoding.UTF8.GetBytes(s, bytes);
                        // Spread across slot.Width slots using independent hash seeds so
                        // a small change in `bytes` moves at most one slot.
                        for (var i = 0; i < slot.Width; i++)
                        {
                            var seed = LshSalt + (ulong)slot.Offset + (ulong)i;
                            var h = XxHash64.HashToUInt64(bytes, (long)seed);
                            v[slot.Offset + i] = (float)(((double)h / ulong.MaxValue) * 2.0 - 1.0);
                        }
                    }
                    break;
            }

            presentSlotCount++;
        }

        // Quality slot: dimension presence ratio (overrides any prior value).
        var presenceSlot = _layout.FindSlot("quality.dimension_presence_ratio");
        if (presenceSlot is not null)
            v[presenceSlot.Offset] = (float)((double)presentSlotCount / Math.Max(1, _layout.Slots.Count));

        // Quality slot: layout version.
        var versionSlot = _layout.FindSlot("quality.layout_version");
        if (versionSlot is not null)
            v[versionSlot.Offset] = _layout.Version;

        return v;
    }

    private static double ToDouble(object o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        IConvertible c => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        _ => 0
    };

    private static int ToInt(object o) => o switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        bool b => b ? 1 : 0,
        IConvertible c => c.ToInt32(System.Globalization.CultureInfo.InvariantCulture),
        _ => 0
    };

    private static void L2Normalise(float[] v)
    {
        double sumSq = 0;
        foreach (var f in v) sumSq += f * f;
        if (sumSq <= 0) return;
        var norm = (float)Math.Sqrt(sumSq);
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }
}
