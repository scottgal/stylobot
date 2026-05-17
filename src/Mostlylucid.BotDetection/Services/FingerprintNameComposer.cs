using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Pure-function display-name composer. Single source of truth for the four-priority
///     naming logic. Callable without a service instance, so <c>FingerprintMatchContributor</c>
///     can compute names synchronously during fingerprint allocation and persist them on the
///     <c>Fingerprint</c> record.
///
///     <see cref="DeterministicBotNameSynthesizer"/> delegates here too so the async/LLM-fallback
///     path and the matcher's sync path produce the same names.
///
///     Priorities:
///       1. Known bot name from UA parsing (<c>ua.bot_name</c>).
///       2. Matched archetype name + drift variance (<c>identity.archetype_name</c>).
///       3. UA family + OS characterization (<c>ua.family</c> on <c>user_agent.os</c>).
///       4. Short fingerprint-id prefix as last-resort label (only when no UA at all).
///
///     Never returns null or empty.
/// </summary>
internal static class FingerprintNameComposer
{
    /// <summary>
    ///     Suffix appended to the displayed name when VerifiedBotContributor flagged the
    ///     claim as spoofed (UA says Googlebot but IP isn't in Google's published range)
    ///     or the rDNS resolved to a host that doesn't match the claimed identity. The
    ///     marker is part of the public name surface - dashboards and CLIs read for it
    ///     to colour-code or filter; do not change it without coordinating with consumers.
    /// </summary>
    public const string SpoofedMarker = " (!)";

    /// <summary>
    ///     Compose a display name from request signals.
    ///     <para>
    ///     <paramref name="fingerprintId"/> when present feeds the cold-state Priority 4
    ///     fallback ("unknown abc123de") in place of "analysing".
    ///     </para>
    ///     <para>
    ///     <paramref name="userAgent"/> lets Priority 3 self-rescue when the UA contributor
    ///     hasn't run yet (the matcher at priority 6 fires before UserAgent at priority 10,
    ///     so <c>ua.family</c> / <c>user_agent.os</c> signals are absent at compose time).
    ///     When the signals are missing but a UA string is available, we parse it via
    ///     <see cref="UserAgentParser"/> directly so Chrome on Windows visitors get the
    ///     expected "Chrome on Windows (US:abcd)" name from request 1.
    ///     </para>
    /// </summary>
    public static string Compose(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId = null,
        string? userAgent = null)
    {
        var signature = GetString(signals, SignalKeys.PrimarySignature);
        var country = GetString(signals, SignalKeys.GeoCountryCode);

        // Priority 1: known bot name from UA parsing. When the UA carries a per-instance
        // discriminator (the +URL comment convention used by Mastodon, Pleroma, Misskey,
        // Lemmy, etc.), append the instance hostname so a fediverse link-preview stampede
        // shows as N distinct signatures ("Mastodon mastodon.social", "Mastodon mas.to")
        // rather than one giant pile that looks like a single misbehaving client.
        //
        // Deceptive bots: when the UA claims a verifiable identity (Googlebot, Bingbot,
        // GPTBot, etc.) but the IP didn't match the vendor's published range or the
        // rDNS lookup failed, VerifiedBotContributor flags VerifiedBotSpoofed=true.
        // We surface that with a "(!)" marker in the displayed name so an operator
        // scanning the dashboard immediately sees the deception attempt instead of
        // the bot blending in with legitimate Googlebot traffic.
        var botName = GetString(signals, SignalKeys.UserAgentBotName);
        if (!string.IsNullOrEmpty(botName) && botName != "unknown")
        {
            var rawUa = GetString(signals, SignalKeys.UserAgent) ?? userAgent;
            var discriminator = UserAgentDiscriminator.ExtractDiscriminator(rawUa);
            var composed = string.IsNullOrEmpty(discriminator) ? botName : $"{botName} {discriminator}";
            if (IsSpoofedClaim(signals)) composed += SpoofedMarker;
            return Unique(composed, signature, country);
        }

        // Priority 2: matched archetype name + variance term.
        var archetypeName = GetString(signals, SignalKeys.IdentityArchetypeName);
        if (!string.IsNullOrEmpty(archetypeName))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? archetypeName : $"{archetypeName} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 3: UA family + OS characterization. Reads the signals first; falls back
        // to parsing the supplied UA string when signals are absent (matcher hot path runs
        // before UserAgentContributor).
        var family = GetString(signals, SignalKeys.UserAgentFamily);
        var os = GetString(signals, SignalKeys.UserAgentOs);
        if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(userAgent))
            family = UserAgentParser.Parse(userAgent).Family;
        if (string.IsNullOrEmpty(os) && !string.IsNullOrEmpty(userAgent))
            os = UserAgentParser.ExtractOs(userAgent);

        if (!string.IsNullOrEmpty(family) && family != "Other")
        {
            var composed = !string.IsNullOrEmpty(os) ? $"{family} on {os}" : family;
            var variance = GetVarianceTerm(signals);
            if (!string.IsNullOrEmpty(variance)) composed = $"{composed} ({variance})";
            return Unique(composed, signature, country);
        }

        // Priority 4: last-resort id-prefix label. Better than "analysing" because the
        // prefix at least identifies which fingerprint this is.
        if (!string.IsNullOrEmpty(fingerprintId))
            return Unique($"unknown {fingerprintId[..Math.Min(8, fingerprintId.Length)]}", signature, country);

        return Unique("analysing", signature, country);
    }

    /// <summary>
    ///     Returns a single variance term describing how this fingerprint deviates from its
    ///     matched centroid, derived from the drift-top-slot signal. Returns null when no
    ///     drift signal is present.
    /// </summary>
    public static string? GetVarianceTerm(IReadOnlyDictionary<string, object> signals)
    {
        var slot = GetString(signals, SignalKeys.IdentityDriftTopSlot);
        if (string.IsNullOrEmpty(slot)) return null;

        var country = GetString(signals, SignalKeys.GeoCountryCode);
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
            _ => GetString(signals, SignalKeys.IdentityDriftTopCategory) switch
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
    ///     Decorates the base name with a (country:sigprefix) suffix when available, so
    ///     distinct fingerprints sharing a base name remain visually distinguishable in the
    ///     dashboard. "Chrome Desktop" becomes "Chrome Desktop (US:abcd)".
    /// </summary>
    private static string Unique(string baseName, string? signature, string? country)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        if (!string.IsNullOrEmpty(signature) && signature.Length >= 4) parts.Add(signature[..4]);
        return parts.Count > 0 ? $"{baseName} ({string.Join(":", parts)})" : baseName;
    }

    internal static string? GetString(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s) ? s : null;

    internal static double GetDouble(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) && v is double d ? d : 0;

    private static bool IsSpoofedClaim(IReadOnlyDictionary<string, object> signals)
    {
        return GetBool(signals, SignalKeys.VerifiedBotSpoofed)
               || GetBool(signals, SignalKeys.VerifiedBotRdnsMismatch);
    }

    private static bool GetBool(IReadOnlyDictionary<string, object> signals, string key)
        => signals.TryGetValue(key, out var v) && v is bool b && b;
}
