namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Deterministic bot name synthesizer that generates meaningful names from detection signals
///     without requiring an LLM. Provides immediate naming on first detection, before the
///     LLM description service has had time to process. LLM-generated names override these
///     when available.
///
///     Name pattern: "{Behavior} {Tool/Family}" e.g. "Rapid Scraper", "Stealthy Crawler",
///     "Rotating Python Bot", "Credential Stuffer"
/// </summary>
public sealed class DeterministicBotNameSynthesizer : IBotNameSynthesizer
{
    public bool IsReady => true;

    public Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        return Task.FromResult<string?>(GenerateName(signals));
    }

    public Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        var name = GenerateName(signals);
        var desc = GenerateDescription(signals);
        return Task.FromResult<(string?, string?)>((name, desc));
    }

    private static string GenerateName(IReadOnlyDictionary<string, object?> signals)
    {
        var signature = GetString(signals, "signature.primary");
        var country = GetString(signals, "geo.country_code");

        // Priority 1: known bot name from UA parsing (highest confidence).
        var botName = GetString(signals, "ua.bot_name");
        if (!string.IsNullOrEmpty(botName) && botName != "unknown")
            return Unique(botName, signature, country);

        // Priority 2: matched archetype name + drift variance term.
        // This is the path most visitors take when Identity:Enabled = true.
        var archetypeName = GetString(signals, "identity.archetype_name");
        if (!string.IsNullOrEmpty(archetypeName))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? archetypeName : $"{archetypeName} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 3: UA family fallback (Identity disabled, or first request before match).
        // Humans on this path get e.g. "Chrome (US:abcd)" instead of "Automated Bot".
        var family = GetString(signals, "ua.family");
        if (!string.IsNullOrEmpty(family))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? family : $"{family} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 4: cold state, no UA info at all.
        return Unique("analysing", signature, country);
    }

    /// <summary>
    ///     Returns a single variance term describing how this fingerprint deviates from its
    ///     matched centroid, derived from the drift-top-slot signal. The slot name maps to a
    ///     human-readable label; categories ("network", "hdr", "locale", "tool") fall through
    ///     to a generic label class when the specific slot is unmapped.
    ///
    ///     Returns null when no drift signal is present — the synthesizer then emits a plain
    ///     archetype/family name with no parenthetical decoration.
    /// </summary>
    private static string? GetVarianceTerm(IReadOnlyDictionary<string, object?> signals)
    {
        var slot = GetString(signals, "identity.drift_top_slot");
        if (string.IsNullOrEmpty(slot)) return null;

        var country = GetString(signals, "geo.country_code");
        return slot switch
        {
            "network.country" when !string.IsNullOrEmpty(country) => $"from {country}",
            "network.country" => "geo shift",
            "network.asn" => "new ASN",
            "network.is_datacenter" => "datacenter",
            "network.is_vpn" => "VPN",
            "network.is_tor" => "Tor",
            "locale.accept_language_primary" or "locale.accept_language_count" => "language shift",
            "hdr.accept" or "hdr.accept_encoding_ordered" => "stripped headers",
            "hdr.header_order_hash" or "hdr.header_case_pattern" => "reordered headers",
            "hdr.upgrade_insecure_requests" or "hdr.dnt" or "hdr.sec_gpc" => "privacy headers",
            var s when s.StartsWith("hdr.sec_ch_ua_", StringComparison.OrdinalIgnoreCase) => "missing client hints",
            var s when s.StartsWith("tool.", StringComparison.OrdinalIgnoreCase) => "tooled",
            _ => GetString(signals, "identity.drift_top_category") switch
            {
                "network" => "network drift",
                "hdr" => "header drift",
                "locale" => "locale drift",
                "tool" => "tooled",
                _ => "drifted"
            }
        };
    }

    /// <summary>
    ///     Make every name unique using signature hash + geography.
    ///     "curl" becomes "curl (DE:a8f2)" -- unique, recognizable, geographically contextual.
    ///     The LLM will later replace this with a centroid-derived descriptive name.
    /// </summary>
    private static string Unique(string baseName, string? signature, string? country)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        if (!string.IsNullOrEmpty(signature) && signature.Length >= 4) parts.Add(signature[..4]);

        return parts.Count > 0 ? $"{baseName} ({string.Join(":", parts)})" : baseName;
    }

    private static string? GenerateDescription(IReadOnlyDictionary<string, object?> signals)
    {
        var parts = new List<string>();

        var family = GetString(signals, "ua.family");
        if (!string.IsNullOrEmpty(family))
            parts.Add($"User-Agent family: {family}");

        var intent = GetString(signals, "intent.category");
        if (!string.IsNullOrEmpty(intent))
            parts.Add($"Intent: {intent}");

        var pageRate = GetDouble(signals, "waveform.page_rate");
        if (pageRate > 0)
            parts.Add($"Request rate: {pageRate:F1} pages/min");

        var assetRatio = GetDouble(signals, "waveform.asset_ratio");
        if (assetRatio < 0.01)
            parts.Add("No static assets loaded (headless behavior)");

        return parts.Count > 0 ? string.Join(". ", parts) + "." : null;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s) ? s : null;

    private static double GetDouble(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is double d ? d : 0;
}
