using System.Reflection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Loads identity archetypes from embedded YAML files in
///     <c>Definitions/IdentityArchetypes/*.yaml</c>, compiles each into a layout-conformant
///     <see cref="IdentityArchetype"/> with centroid + dimension_mask blobs, and exposes a
///     nearest-archetype scan over the in-memory set.
///
///     Archetypes are read-only at runtime; the calibration service refreshes them on its own
///     cycle. Same singleton holds both the YAML-seeded and the calibration-refined versions.
/// </summary>
public sealed class IdentityArchetypeRegistry
{
    private readonly ILogger<IdentityArchetypeRegistry> _logger;
    private readonly IdentityVectorEncoder _encoder;
    private IReadOnlyList<IdentityArchetype> _archetypes;

    public IdentityArchetypeRegistry(
        ILogger<IdentityArchetypeRegistry> logger,
        IdentityVectorEncoder encoder)
    {
        _logger = logger;
        _encoder = encoder;
        _archetypes = LoadFromEmbeddedResources();
        _logger.LogInformation("Loaded {Count} identity archetypes from embedded resources", _archetypes.Count);
    }

    public IReadOnlyList<IdentityArchetype> All => _archetypes;

    /// <summary>
    ///     Replace the in-memory archetype set (used by the calibration service after refining
    ///     each archetype's centroid against its descendants' mean).
    /// </summary>
    public void Replace(IReadOnlyList<IdentityArchetype> refreshed)
    {
        _archetypes = refreshed;
    }

    /// <summary>
    ///     Promote a freshly-loaded arcjet well-known-bot catalogue into the
    ///     archetype registry. Called by <c>WellKnownBotRefreshService</c>
    ///     after a successful catalog download. Each entry that does NOT
    ///     already exist (by archetype_id) becomes a new root archetype with
    ///     a UA-family pin at 0.9 confidence -- slightly lower than the
    ///     BotPatterns synthesizer (0.95) because arcjet ids are sometimes
    ///     pattern shells rather than human-recognised UA tokens. Existing
    ///     archetypes (YAML or BotPatterns-derived) win and are preserved.
    ///     Idempotent: repeat refreshes don't duplicate entries.
    /// </summary>
    public int IngestWellKnownBots(
        IEnumerable<(string Id, string DisplayName, string BotType)> entries)
    {
        var current = new List<IdentityArchetype>(_archetypes);
        var seenIds = new HashSet<string>(
            current.Select(a => a.ArchetypeId),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.DisplayName)) continue;
            var archetypeId = KebabCase(entry.DisplayName);
            if (string.IsNullOrEmpty(archetypeId)) continue;
            if (!seenIds.Add(archetypeId)) continue;

            try
            {
                var dto = new IdentityArchetypeYaml
                {
                    ArchetypeId = archetypeId,
                    Name = entry.DisplayName,
                    Description = $"Auto-promoted from arcjet well-known-bots catalogue (id: {entry.Id}).",
                    ArchetypeKind = MapBotTypeToArchetypeKind(entry.BotType),
                    ArchetypeRole = "client",
                    Dimensions = new Dictionary<string, IdentityArchetypeDimensionYaml>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["hdr.ua_family"] = new IdentityArchetypeDimensionYaml
                        {
                            Value = entry.DisplayName,
                            Confidence = 0.9,
                        }
                    }
                };
                current.Add(Compile(dto));
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to promote arcjet well-known-bot {Id} to archetype",
                    entry.Id);
            }
        }

        if (added > 0)
        {
            _archetypes = current;
            _logger.LogInformation(
                "Promoted {Added} arcjet well-known-bot entries to root archetypes (total now {Total})",
                added, current.Count);
        }

        return added;
    }

    /// <summary>
    ///     Lookup by archetype id (case-insensitive). Returns null when the id is null, empty,
    ///     or not present in the registry. Used by <see cref="FingerprintMatchContributor"/>
    ///     to resolve a matched fingerprint's <c>InferredClientType</c> to a display name
    ///     without iterating <see cref="All"/> on every match.
    /// </summary>
    public IdentityArchetype? TryGetById(string? archetypeId)
    {
        if (string.IsNullOrEmpty(archetypeId)) return null;
        foreach (var a in _archetypes)
            if (string.Equals(a.ArchetypeId, archetypeId, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }

    /// <summary>
    ///     Brute-force scan: per-archetype masked-cosine score against every archetype.
    ///     "Masked" means each archetype's own <see cref="IdentityArchetype.DimensionMask"/>
    ///     weights its contribution per dim, with the mask read straight from the YAML's
    ///     <c>confidence</c> per asserted dim. Dims the archetype is silent on (mask = 0)
    ///     contribute nothing -- that IS the meaning of "no claim" in the YAML, and that
    ///     is what fixes the umbrella-centroid problem unweighted cosine suffered from:
    ///     a broad archetype (mastodon, googlebot) no longer wins by accidentally
    ///     overlapping the observation on many low-importance dims, because each match
    ///     is now scaled by the archetype's own confidence in that dim.
    ///
    ///     Score formula (per archetype):
    ///       score = Σᵢ mask[i] · obs[i] · centroid[i]
    ///                 / sqrt(Σᵢ mask[i] · centroid[i]² · Σᵢ mask[i] · obs[i]²)
    ///
    ///     The normaliser keeps scores comparable across archetypes that assert
    ///     different numbers of dims. Returns null if no archetypes are loaded; always
    ///     returns the nearest by score regardless of absolute value -- callers that
    ///     want a minimum-score gate apply it on the returned
    ///     <see cref="ArchetypeMatch.Score"/> (the matcher gates name signal emission
    ///     via <c>Match.MinArchetypeMatchScore</c> without changing the seeding
    ///     behaviour the rest of the pipeline relies on).
    /// </summary>
    public ArchetypeMatch? FindNearest(float[] vector)
    {
        if (_archetypes.Count == 0) return null;
        IdentityArchetype? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var a in _archetypes)
        {
            var s = MaskedSimilarity(vector, a);
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }
        return best is null ? null : new ArchetypeMatch(best, bestScore);
    }

    /// <summary>
    ///     Same as <see cref="FindNearest"/> but uses <see cref="ScoreAgainstRaw"/> for scoring.
    /// </summary>
    public ArchetypeMatch? FindNearestRaw(float[] rawVector)
    {
        if (_archetypes.Count == 0 || rawVector is null) return null;
        IdentityArchetype? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var a in _archetypes)
        {
            var s = ScoreAgainstRaw(rawVector, a);
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }
        return best is null ? null : new ArchetypeMatch(best, bestScore);
    }

    /// <summary>
    ///     Per-archetype similarity score for <paramref name="vector"/>. Same scale
    ///     as <see cref="FindNearest"/>; the dashboard's drift surface reads this
    ///     for the fingerprint's <c>ArchetypeOrigin</c> alongside the current-nearest.
    /// </summary>
    public double? ScoreAgainst(float[] vector, IdentityArchetype? archetype)
    {
        if (archetype is null || vector is null) return null;
        if (vector.Length != archetype.Centroid.Length) return null;
        return MaskedSimilarity(vector, archetype);
    }

    /// <summary>
    ///     Score a raw (unnormalized) observation vector against an archetype using its raw
    ///     centroid (<see cref="IdentityArchetype.CentroidRaw"/>). Falls back to the normalized
    ///     centroid for backwards-compatibility when CentroidRaw is null.
    /// </summary>
    public double ScoreAgainstRaw(float[] rawVector, IdentityArchetype archetype)
    {
        if (rawVector is null) throw new ArgumentNullException(nameof(rawVector));
        if (archetype is null) throw new ArgumentNullException(nameof(archetype));
        var centroidForScoring = archetype.CentroidRaw ?? archetype.Centroid;
        return MaskedSimilarityCore(rawVector, centroidForScoring, archetype.DimensionMask,
            archetype.VarianceVector ?? DefaultVarianceFor(archetype));
    }

    /// <summary>
    ///     Mask-weighted-distance similarity over the slots the archetype asserts AND
    ///     the observation actually populated. Replaces masked-cosine, which suffered
    ///     an "umbrella centroid" inflation: a sparse observation scored highly
    ///     against archetypes with many low- or zero-valued centroid slots
    ///     (googlebot's <c>sec_fetch_pattern=0</c>, <c>UIR=false</c>, mastodon's
    ///     minimal-header assertions) because cosine over sparse vectors is
    ///     degenerate -- slots where both sides are near-zero drop out of numerator
    ///     and denominator entirely, leaving the metric ungrounded.
    ///
    ///     For each slot the archetype asserts, we check whether the observation
    ///     populated it (any per-dim value non-zero). Slots the observation didn't
    ///     populate are SKIPPED -- the archetype can't claim credit on a dim the
    ///     request didn't provide, and the observation can't be penalised for not
    ///     having data the archetype was specific about. This makes a Chrome+uBlock
    ///     XHR that strips <c>Sec-Ch-Ua-Mobile</c> no longer trivially "matches"
    ///     googlebot's <c>sec_ch_ua_mobile=false</c> assertion.
    ///
    ///     Per-slot contribution is the squared difference summed across the slot's
    ///     dims, scaled by the archetype's mask (= YAML confidence). The total
    ///     normalises by the populated-slot mask weight so archetypes with different
    ///     specificities compare fairly, then converts to similarity via 1/(1+avg).
    ///     Archetypes whose asserted slots are ALL unpopulated by the observation
    ///     score zero -- no overlap, no win, no inflation.
    /// </summary>
    private double MaskedSimilarity(float[] vector, IdentityArchetype archetype)
    {
        return MaskedSimilarityCore(vector, archetype.Centroid, archetype.DimensionMask,
            archetype.VarianceVector ?? DefaultVarianceFor(archetype));
    }

    private double MaskedSimilarityCore(float[] vector, float[] centroid, float[] mask, float[] variance)
    {
        const float presenceEpsilon = 1e-6f;
        const double varianceFloor = 1e-4; // prevents div-by-zero and log(0)

        double weightedLogLikelihood = 0;
        double totalMask = 0;

        foreach (var slot in _encoder.Layout.Slots)
        {
            if (slot.Offset >= mask.Length) continue;
            var slotMask = (double)mask[slot.Offset];
            if (slotMask <= 0) continue;

            var end = Math.Min(slot.Offset + slot.Width, Math.Min(vector.Length, centroid.Length));
            var obsPopulated = false;
            for (var i = slot.Offset; i < end; i++)
            {
                if (Math.Abs(vector[i]) > presenceEpsilon) { obsPopulated = true; break; }
            }
            if (!obsPopulated) continue;

            for (var i = slot.Offset; i < end; i++)
            {
                double diff = vector[i] - centroid[i];
                double v = i < variance.Length ? Math.Max(varianceFloor, variance[i]) : varianceFloor;
                // Gaussian per-dim log-likelihood (constants dropped, monotone-preserving):
                //   LL = -0.5 * diff^2 / v   - 0.5 * log(v)
                // First term penalises deviation. Second term rewards specificity (tight var -> large
                // positive bonus). Together they make tight archetypes preferred for near-centroid
                // observations and broad archetypes preferred for distant ones.
                double logLik = -0.5 * (diff * diff) / v - 0.5 * Math.Log(v);
                weightedLogLikelihood += slotMask * logLik;
            }
            totalMask += slotMask * (end - slot.Offset);
        }
        if (totalMask <= 0) return 0.0;

        var avgLogLikelihood = weightedLogLikelihood / totalMask;
        // Sigmoid bounds the score to (0, 1) regardless of variance and deviation magnitudes.
        return 1.0 / (1.0 + Math.Exp(-avgLogLikelihood));
    }

    /// <summary>
    ///     Default per-dimension variance derived from the archetype's own mask confidence.
    ///     High mask confidence means the archetype "really means it" on that dim, so variance
    ///     is tight; low confidence means the dim is loosely asserted, so variance is broad.
    ///         variance[i] = (1 - confidence[i])^2 * baseScale + varianceFloor
    ///     baseScale of 0.05 yields ~1e-4 for high-confidence dims and ~0.04 for low-confidence
    ///     ones, giving the Gaussian-NLL specificity reward a workable dynamic range without any
    ///     descendant calibration runs.
    /// </summary>
    private static float[] DefaultVarianceFor(IdentityArchetype archetype)
    {
        // L2-normalised vectors (both centroid and observation) produce non-trivial
        // per-dim diffs even on matching raw inputs because the normaliser divides by
        // the magnitude of the populated-dim set, which differs between centroid and
        // observation. A small baseScale would let the Gaussian penalty explode on
        // those tiny "structural" diffs and drown the specificity reward. A larger
        // baseScale keeps default variance broad enough that the specificity term
        // dominates for high-confidence archetypes while the penalty term still
        // bites when diff is genuinely large (far observation).
        const float baseScale = 1.0f;
        const float varianceFloor = 1e-4f;
        var mask = archetype.DimensionMask;
        var result = new float[mask.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            var conf = Math.Clamp(mask[i], 0f, 1f);
            var diff = 1f - conf;
            result[i] = diff * diff * baseScale + varianceFloor;
        }
        return result;
    }

    private IReadOnlyList<IdentityArchetype> LoadFromEmbeddedResources()
    {
        var assembly = typeof(IdentityArchetypeRegistry).Assembly;

        var results = new List<IdentityArchetype>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("IdentityArchetypes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dto = YamlSerializer.Deserialize<IdentityArchetypeYaml>(ms.ToArray());
                if (dto is null || string.IsNullOrEmpty(dto.ArchetypeId)) continue;

                var compiled = Compile(dto);
                if (seenIds.Add(compiled.ArchetypeId))
                    results.Add(compiled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load identity archetype from {Resource}", resourceName);
            }
        }

        // Promote every BotPatternEntry to a synthetic root archetype.
        // Roots are seed basins (per the system's identity architecture):
        // they aren't gospel verdicts, behaviour overrides them per-fingerprint
        // via centroid drift, and specific variants emerge as descendants.
        // Without a root for each known bot identity, a Mastodon-shape can
        // only drift toward the nearest of the few hand-written YAML
        // archetypes (chrome-xhr's umbrella warning describes this exact
        // failure mode). Each pattern entry produces a UA-family-pinned root
        // with confidence 0.95 so the matcher actually scores it; the rest
        // of the centroid is unconstrained so descendants emerge from real
        // observations rather than from guessed shape.
        var explicitCount = results.Count;
        foreach (var entry in BotPatternLoader.Default.AllPatterns)
        {
            try
            {
                var synthesized = SynthesizeFromBotPattern(entry);
                if (synthesized is null) continue;
                // Explicit YAML wins on ID collision -- the hand-crafted root
                // expresses richer dimensional pins (Sec-Fetch shape, Accept
                // collapse, etc.) than the single UA-family assertion the
                // synthesizer can derive from a pattern row.
                if (!seenIds.Add(synthesized.ArchetypeId)) continue;
                results.Add(Compile(synthesized));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to synthesize archetype from bot pattern {Pattern}", entry.Pattern);
            }
        }
        _logger.LogInformation(
            "Identity archetype catalogue: {Explicit} from IdentityArchetypes/*.yaml + {Synthesized} promoted from BotPatterns/*.yaml = {Total} roots",
            explicitCount, results.Count - explicitCount, results.Count);
        return results;
    }

    /// <summary>
    ///     Promote a <see cref="BotPatternEntry"/> to a synthetic
    ///     <see cref="IdentityArchetypeYaml"/>. The synthesized root carries
    ///     a single UA-family assertion at high confidence -- enough for the
    ///     matcher to recognise the bot's UA shape, with no further
    ///     dimensional constraints so observations are free to refine the
    ///     centroid in whichever direction the bot actually behaves.
    /// </summary>
    internal static IdentityArchetypeYaml? SynthesizeFromBotPattern(BotPatternEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.BotName)) return null;

        var archetypeId = KebabCase(entry.BotName);
        if (string.IsNullOrEmpty(archetypeId)) return null;

        // ai_category is an orthogonal signal in the bot pattern catalogue --
        // a bot can be bot_type=GoodBot AND ai_category=Search (PerplexityBot,
        // OpenAI search variants). Operators care about the AI distinction
        // for routing / rate-limit reasons, so promote ai_category-bearing
        // rows to ai-bot regardless of trust posture. Trust still rides on
        // the per-fingerprint centroid (drift toward verified-bot basin if
        // the bot behaves; toward malicious-bot basin if it doesn't).
        var kind = !string.IsNullOrWhiteSpace(entry.AiCategory)
            ? "ai-bot"
            : MapBotTypeToArchetypeKind(entry.BotType);

        return new IdentityArchetypeYaml
        {
            ArchetypeId = archetypeId,
            Name = entry.BotName,
            Description = string.IsNullOrEmpty(entry.Vendor)
                ? $"Auto-promoted from BotPatterns catalogue (pattern: {entry.Pattern})."
                : $"Auto-promoted from BotPatterns catalogue (pattern: {entry.Pattern}, vendor: {entry.Vendor}).",
            ArchetypeKind = kind,
            ArchetypeRole = "client",
            Dimensions = new Dictionary<string, IdentityArchetypeDimensionYaml>(StringComparer.OrdinalIgnoreCase)
            {
                ["hdr.ua_family"] = new IdentityArchetypeDimensionYaml
                {
                    Value = entry.BotName,
                    Confidence = 0.95,
                }
            }
        };
    }

    /// <summary>
    ///     Map a <see cref="BotPatternEntry.BotType"/> string to an
    ///     <c>archetype_kind</c> value. Mirrors the kinds used in the
    ///     hand-written IdentityArchetypes/*.yaml files so dashboard
    ///     groupings stay consistent across explicit + synthesized roots.
    /// </summary>
    internal static string MapBotTypeToArchetypeKind(string? botType) => botType?.ToLowerInvariant() switch
    {
        "searchengine" or "verifiedbot" or "monitoringbot" => "verified-bot",
        "goodbot"                                          => "verified-bot",
        "aibot"                                            => "ai-bot",
        "socialmediabot"                                   => "social-bot",
        "scraper"                                          => "scraper",
        "tool"                                             => "tool",
        "exploitscanner" or "maliciousbot" or "clickfraud" => "malicious-bot",
        _                                                  => "bot"
    };

    /// <summary>
    ///     Lower-case identifier with non-alphanumerics collapsed to single
    ///     hyphens. Used to derive deterministic <c>archetype_id</c> values
    ///     from bot names ("GPTBot" -> "gptbot", "Bytespider" -> "bytespider",
    ///     "AhrefsBot" -> "ahrefsbot", "Mastodon" -> "mastodon").
    /// </summary>
    internal static string KebabCase(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        var lastWasHyphen = true;
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private IdentityArchetype Compile(IdentityArchetypeYaml dto)
    {
        // Build the raw-values dictionary the encoder consumes. Each named YAML dimension fills
        // its corresponding slot; unnamed dimensions stay absent (encoder leaves them at 0).
        var rawValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in dto.Dimensions ?? new())
            rawValues[name] = entry.Value;

        var centroid = _encoder.Encode(rawValues);
        var centroidRaw = _encoder.EncodeRaw(rawValues);

        // Mask: for every dim the YAML asserts, set the slot's full width to that confidence.
        // Anything the YAML doesn't mention stays at 0 (the archetype makes no claim about it).
        var mask = new float[_encoder.Layout.Dimension];
        foreach (var (name, entry) in dto.Dimensions ?? new())
        {
            var slot = _encoder.Layout.FindSlot(name);
            if (slot is null) continue;
            var confidence = (float)Math.Clamp(entry.Confidence, 0.0, 1.0);
            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
                mask[i] = confidence;
        }

        return new IdentityArchetype
        {
            ArchetypeId = dto.ArchetypeId,
            Name = dto.Name ?? dto.ArchetypeId,
            Description = dto.Description,
            ArchetypeKind = dto.ArchetypeKind ?? "unknown",
            ArchetypeRole = dto.ArchetypeRole ?? "client",
            Centroid = centroid,
            CentroidRaw = centroidRaw,
            DimensionMask = mask,
            DescendantCount = 0,
            LastRefinedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Nearest CLIENT archetype to <paramref name="vector"/>, ignoring mode
    ///     archetypes (chrome-xhr, future signalr / prefetch / ws entries).
    ///     <para>
    ///     Used by <see cref="Orchestration.ContributingDetectors.FingerprintMatchContributor"/>
    ///     to pick the archetype that drives <c>archetypeOrigin</c>, the
    ///     displayed identity name, and the drift "Origin -> Current"
    ///     comparison. The full <see cref="FindNearest"/> still considers
    ///     modes -- they're needed as seed priors so a Chrome XHR doesn't
    ///     drift to googlebot at allocation -- but the identity-display
    ///     surface MUST consult this client-only view, otherwise the
    ///     dashboard's drift banner reads "Chrome Desktop -> Chrome XHR
    ///     drift 65%" which is a mode shift presented as identity change
    ///     (per the composite-browser-mode-fingerprints spec, that's a
    ///     category error -- one identity plays many modes).
    ///     </para>
    /// </summary>
    public ArchetypeMatch? FindNearestClient(float[] vector)
    {
        if (_archetypes.Count == 0) return null;
        IdentityArchetype? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var a in _archetypes)
        {
            if (a.IsMode) continue;
            var s = MaskedSimilarity(vector, a);
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }
        return best is null ? null : new ArchetypeMatch(best, bestScore);
    }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class IdentityArchetypeYaml
{
    public string? ArchetypeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ArchetypeKind { get; set; }
    /// <summary>
    ///     <c>client</c> (default) or <c>mode</c>. See <see cref="IdentityArchetype.ArchetypeRole"/>
    ///     for semantics. Only chrome-xhr.yaml carries this today; future
    ///     signalr / prefetch / ws-upgrade entries should also set
    ///     <c>archetype_role: mode</c> when migrated out of BrowserModes/.
    /// </summary>
    public string? ArchetypeRole { get; set; }
    public Dictionary<string, IdentityArchetypeDimensionYaml>? Dimensions { get; set; }

    /// <summary>
    ///     Optional per-dimension variance override (length must equal identity vector dimension).
    ///     When null, variance is derived from confidence values at compile time.
    /// </summary>
    public List<float>? VariancePerDimension { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class IdentityArchetypeDimensionYaml
{
    public object? Value { get; set; }
    public double Confidence { get; set; } = 1.0;
}
