using System.Globalization;
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
    ///     Per-fingerprint timestamp format appended to names whose base would otherwise
    ///     collide for distinct fingerprints (e.g. two Mastodon instances both producing
    ///     just "Mastodon"). 10 chronologically-sortable digits, yyMMddHHmm, UTC. The
    ///     hyphen between date + time keeps it readable: "Mastodon 251125-1325" =
    ///     2025-11-25 13:25 UTC.
    /// </summary>
    private const string FirstSeenFormat = "yyMMdd-HHmm";

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
    ///     <paramref name="firstSeen"/> stamps the name with a per-fingerprint creation
    ///     time so two visitors with the same base name (e.g. two different Mastodon
    ///     instances when the UA carries no +URL discriminator) still produce visually
    ///     distinct names. Each fingerprint has its own FirstSeen, so collisions resolve.
    ///     </para>
    ///     <para>
    ///     <paramref name="previousName"/> drives the hysteresis rule that stops names
    ///     flickering between "analysing" and "Chrome on Windows" request-to-request. If
    ///     the fresh compose would be a Priority-4 fallback ("analysing" / "unknown xxx")
    ///     but the previous compose found a real name, the previous wins. The matcher's
    ///     Path 2+3 recompose (in <c>EmitDisplayNameSignal</c>) passes the persisted
    ///     <c>Fingerprint.DisplayName</c> here.
    ///     </para>
    /// </summary>
    public static string Compose(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId = null,
        string? userAgent = null,
        string? previousName = null,
        DateTime? firstSeen = null)
    {
        var signature = GetString(signals, SignalKeys.PrimarySignature);
        var country = GetString(signals, SignalKeys.GeoCountryCode);
        var fresh = ComposeFresh(signals, fingerprintId, userAgent, firstSeen, signature, country);

        // Hysteresis: if the fresh compose would be a Priority-4 fallback ("analysing" /
        // "unknown xxx") but we have a previous non-fallback name, keep the previous one.
        // Stops the visible name from churning when signal presence varies request-to-
        // request (matcher runs at priority 6, before UserAgentContributor at priority 10,
        // so the first compose for a brand-new fingerprint often lacks ua.family).
        if (IsFallback(fresh) && !string.IsNullOrEmpty(previousName) && !IsFallback(previousName))
            return previousName;
        return fresh;
    }

    private static string ComposeFresh(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId,
        string? userAgent,
        DateTime? firstSeen,
        string? signature,
        string? country)
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
            composed = AppendFirstSeen(composed, firstSeen);
            return Unique(composed, signature, country);
        }

        // Priority 2: matched archetype name + variance term.
        var archetypeName = GetString(signals, SignalKeys.IdentityArchetypeName);
        if (!string.IsNullOrEmpty(archetypeName))
        {
            var variance = GetVarianceTerm(signals);
            var composed = string.IsNullOrEmpty(variance) ? archetypeName : $"{archetypeName} ({variance})";
            composed = AppendFirstSeen(composed, firstSeen);
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
            composed = AppendFirstSeen(composed, firstSeen);
            return Unique(composed, signature, country);
        }

        // Priority 4: last-resort id-prefix label. Better than "analysing" because the
        // prefix at least identifies which fingerprint this is.
        if (!string.IsNullOrEmpty(fingerprintId))
            return Unique($"unknown {fingerprintId[..Math.Min(8, fingerprintId.Length)]}", signature, country);

        return Unique("analysing", signature, country);
    }

    /// <summary>
    ///     True when <paramref name="composedName"/> is a Priority-4 fallback ("analysing"
    ///     or "unknown xxx" prefix) - either form is the "we don't know yet" sentinel and
    ///     should never overwrite a real name. Strips the Unique() country/sig parens
    ///     before testing the base.
    /// </summary>
    public static bool IsFallback(string composedName)
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

    private static string AppendFirstSeen(string baseName, DateTime? firstSeen)
        => firstSeen is { } ts
            ? $"{baseName} {ts.ToUniversalTime().ToString(FirstSeenFormat, CultureInfo.InvariantCulture)}"
            : baseName;

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
