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
    ///     expected "Chrome on Windows" name from request 1.
    ///     </para>
    ///     <para>
    ///     <paramref name="previousName"/> drives the hysteresis rule that stops names
    ///     flickering between "analysing" and "Chrome on Windows" request-to-request. If
    ///     the fresh compose would be a Priority-4 fallback ("analysing" / "unknown xxx")
    ///     but the previous compose found a real name, the previous wins. The matcher's
    ///     Path 2+3 recompose (in <c>EmitDisplayNameSignal</c>) passes the persisted
    ///     <c>Fingerprint.DisplayName</c> here.
    ///     </para>
    ///     <para>
    ///     Same-name collisions between distinct fingerprints (e.g. two Mastodon instances
    ///     when the UA carries no <c>+URL</c> discriminator) are NOT disambiguated here.
    ///     The display layer (CLI sidebar, dashboard list) appends a "variant N" suffix
    ///     when it sees duplicate base names; first-seen / last-seen render as separate
    ///     columns so the name stays clean.
    ///     </para>
    /// </summary>
    public static string? Compose(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId = null,
        string? userAgent = null,
        string? previousName = null)
    {
        var fresh = ComposeFresh(signals, userAgent);

        // Hysteresis: if we have no fresh result but a previous real name exists, keep the
        // previous one so the visible label doesn't disappear when signal presence varies
        // request-to-request (matcher runs at priority 6, before UserAgentContributor at
        // priority 10, so the first compose for a brand-new fingerprint often lacks ua.family).
        if (fresh is null && !string.IsNullOrEmpty(previousName))
            return previousName;
        return fresh;
    }

    private static string? ComposeFresh(
        IReadOnlyDictionary<string, object> signals,
        string? userAgent)
    {
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
            return composed;
        }

        // Priority 2: matched archetype name + variance term -- but ONLY when the matched
        // archetype is human-browser-shaped. Naming invariant: if Priority 1 didn't fire,
        // the UA is not a self-declared bot. Matching a bot-shaped archetype (verified-bot
        // / tool / headless / anything not human-browser) in that case is a fingerprint
        // coincidence -- typically header drift on a real browser that happens to overlap
        // a bot family's centroid. Naming a real visitor "Mastodon Family (header drift)"
        // or "Googlebot (header drift)" is exactly the false labelling we forbid. Bot-shaped
        // archetypes fall through to Priority 3 (UA family + OS) which produces the right
        // label for the actual UA. Self-declared bot UAs are already handled at Priority 1.
        var archetypeName = GetString(signals, SignalKeys.IdentityArchetypeName);
        var archetypeKind = GetString(signals, SignalKeys.IdentityArchetypeKind);
        if (!string.IsNullOrEmpty(archetypeName) && archetypeKind == "human-browser")
        {
            var variance = GetVarianceTerm(signals);
            return string.IsNullOrEmpty(variance) ? archetypeName : $"{archetypeName} ({variance})";
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
            return string.IsNullOrEmpty(variance) ? composed : $"{composed} ({variance})";
        }
        // Priorities used to flow through a Unique() wrapper that appended a
        // "(country:sigprefix)" suffix. That was removed (operators objected to the
        // status-as-name pollution); the wrapper is gone too -- the return paths above
        // are the final names.

        // No usable signal yet. Return null so the caller can decide whether to emit
        // anything at all -- typically that means "leave bot_name blank in storage and
        // let the dashboard's render layer synthesise a descriptive label from threat
        // / behaviour signals on the row". Avoids the old "analysing" / "unknown xxx"
        // placeholders ever reaching the dashboard or persisting in fingerprint records.
        return null;
    }

    /// <summary>
    ///     True when <paramref name="composedName"/> is null/empty or a legacy Priority-4
    ///     fallback ("analysing" / "unknown xxx" / "(country:sigprefix)"-decorated form of
    ///     either). Compose no longer produces these -- it returns null instead -- but
    ///     historical persisted display_name rows from before that change still match.
    ///     Strips a legacy " (...)" suffix before testing the base.
    /// </summary>
    public static bool IsFallback(string? composedName)
    {
        if (string.IsNullOrEmpty(composedName)) return true;
        var paren = composedName.IndexOf(" (", StringComparison.Ordinal);
        var baseName = paren > 0 ? composedName[..paren] : composedName;
        return baseName == "analysing"
            || baseName.StartsWith("unknown ", StringComparison.Ordinal);
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
    ///     Returns a single distinctive modifier from current signals, ordered most-specific
    ///     to least. The matcher calls this when a freshly-composed display name already
    ///     belongs to a different fingerprint -- the rule is "same name = same fingerprint",
    ///     so a collision means the new fingerprint MUST carry something extra in its name,
    ///     and that something has to be derived from what makes it different (ASN, country,
    ///     IP /16 block), never a random hash. Returns null when no usable signal is present;
    ///     callers should then fall back to the short fp-id prefix.
    /// </summary>
    public static string? BuildDistinctiveModifier(IReadOnlyDictionary<string, object> signals, int attempt = 0)
    {
        var asn = GetString(signals, SignalKeys.IpAsn);
        var country = GetString(signals, SignalKeys.GeoCountryCode);
        var ip = GetString(signals, SignalKeys.ClientIp);

        // attempt 0: ASN -- distinguishes AmazonBot US-East-1 (AS16509) from AmazonBot EU (AS14618).
        // attempt 1: country -- generic geographic split when ASN is missing or matches.
        // attempt 2: /16 IP prefix -- last-resort numeric block discriminator.
        return attempt switch
        {
            0 when !string.IsNullOrEmpty(asn) => $"AS{asn}",
            1 when !string.IsNullOrEmpty(country) => country,
            2 when !string.IsNullOrEmpty(ip) => TrimToSlash16(ip),
            _ => null
        };
    }

    private static string? TrimToSlash16(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.0.0/16";
        var colon = ip.IndexOf(':');
        if (colon > 0) return ip[..Math.Min(ip.Length, colon + 5)] + "::/32";
        return null;
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
