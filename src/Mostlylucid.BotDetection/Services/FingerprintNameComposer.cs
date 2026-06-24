using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
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
///     Priorities (claim-first, user directive 2026-06-15
///     "We always start with what is claimed and TEST IT. Not start with a match, use that
///     and ignore what's claimed."):
///       1. UA-STRING CLAIM. Extract the bot/tool identity directly from the raw UA via the
///          YAML bot-patterns catalog (<see cref="BotPatternLoader.MatchUserAgent"/>). The
///          cached <c>ua.bot_name</c> signal is consulted first as a fast path, but absent
///          that we read the catalog directly from <c>ua.raw</c>. This is the load-bearing
///          inversion: the YAML catalog match is the source of truth, the cached signal is
///          a convenience. Verification (IP-range / rDNS) runs in parallel and appends
///          <see cref="SpoofedMarker"/> when it disagrees with the claim -- the operator
///          sees both the claim AND the verdict.
///       2. Matched archetype name + drift variance (<c>identity.archetype_name</c>),
///          gated to human-browser archetypes only -- a non-self-declared visitor whose
///          centroid grazes a bot archetype is a fingerprint coincidence, not an identity.
///       3. UA family + OS characterization (<c>ua.family</c> on <c>user_agent.os</c>).
///       4. Raw UA prefix as last-resort label (truncated). Marked as fallback so
///          hysteresis still lets a real Priority 1-3 name override it later.
///
///     Returns null only when the request carries no UA at all.
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
        var fresh = ComposeFresh(signals, userAgent, fingerprintId);

        // Hysteresis: keep a previously-persisted REAL name (Priority 1-3) over either
        //   (a) a null fresh result, OR
        //   (b) a fresh Priority-4 UA-prefix FALLBACK
        // so the visible label doesn't churn between "Googlebot" and "Mozilla/5.0..."
        // when bot_name is missing from this request's signal dict. previousName is only
        // honoured when it is itself NOT a fallback -- a previously-stored UA prefix or
        // legacy "analysing" should yield to any non-null fresh result.
        if (!string.IsNullOrEmpty(previousName) && !IsFallback(previousName) &&
            (fresh is null || IsFallback(fresh)))
            return previousName;
        return fresh;
    }

    private static string? ComposeFresh(
        IReadOnlyDictionary<string, object> signals,
        string? userAgent,
        string? fingerprintId = null)
    {
        // Priority 1: UA-STRING CLAIM (claim-first, user directive 2026-06-15).
        //
        // Start with what the visitor CLAIMS to be. Two paths feed P1, in order:
        //   (1a) the cached ua.bot_name signal -- fast, populated by UserAgentContributor
        //        when the orchestrator has already run UA pattern matching for THIS
        //        request and stuffed the result into the blackboard.
        //   (1b) the raw UA string -- read directly via BotPatternLoader.MatchUserAgent
        //        (substring scan over every catalogued claim marker in the YAML files).
        //        This path is the load-bearing one for matcher-runs-before-UA-contributor
        //        races: matcher Priority 6 fires before UserAgentContributor Priority 10
        //        on every request, so ua.bot_name is absent on the matcher's first call
        //        and a centroid drift onto a chrome-with-privacy-headers archetype used
        //        to leak "Chrome on macOS (privacy headers)" out as the name for a real
        //        Mastodon UA. Reading the YAML catalog directly bypasses the cached
        //        signal entirely and produces "Mastodon mastodon.social" on request 1.
        //
        // Per feedback_no_word_lists: the claim catalogue lives in YAML
        // (Definitions/BotPatterns/*.bot-patterns.yaml), NOT in code here. Adding a new
        // fediverse server / AI crawler / CLI tool is a YAML edit.
        //
        // When the UA carries a per-instance discriminator (the +URL comment convention
        // used by Mastodon, Pleroma, Misskey, Lemmy, etc.), append the instance hostname
        // so a fediverse link-preview stampede shows as N distinct signatures
        // ("Mastodon mastodon.social", "Mastodon mas.to") rather than one giant pile
        // that looks like a single misbehaving client.
        //
        // Verification runs in parallel: when the UA claims a verifiable identity
        // (Googlebot, Bingbot, GPTBot, etc.) but the IP didn't match the vendor's
        // published range or the rDNS lookup failed, VerifiedBotContributor flags
        // VerifiedBotSpoofed=true. We surface that with a " (!)" marker -- the name
        // shows the CLAIM AND the verdict, never one without the other.
        var rawUaForClaim = GetString(signals, SignalKeys.UserAgent) ?? userAgent;
        var claimedBotName = ExtractClaimedBotName(signals, rawUaForClaim);
        if (!string.IsNullOrEmpty(claimedBotName))
        {
            // Read the cached ua.bot_instance signal first; the UserAgentContributor
            // emits it alongside ua.bot_name for every request now (claim-verify-trust
            // gap #2 2026-06-15). The direct-extraction fallback below covers the
            // matcher-runs-before-UserAgentContributor race -- matcher Priority 6
            // fires before UA contributor Priority 10, so the signal is absent on
            // the matcher's first read of a fresh request.
            var discriminator = GetString(signals, SignalKeys.UserAgentBotInstance);
            if (string.IsNullOrEmpty(discriminator))
                discriminator = UserAgentDiscriminator.ExtractDiscriminator(rawUaForClaim);
            // Don't double-append the discriminator when the cached UserAgentBotName
            // already carries it. UserAgentContributor pre-composes "{name} {instance}"
            // when it writes the cached bot_name signal, so reading that back here and
            // appending the instance again produced "SemrushBot semrush.com semrush.com".
            // Suffix-check on the claimed name catches that path without losing the
            // discriminator on the matcher-runs-first race where the cached signal
            // isn't written yet.
            var composed = string.IsNullOrEmpty(discriminator)
                  || claimedBotName.EndsWith(discriminator, StringComparison.OrdinalIgnoreCase)
                ? claimedBotName
                : $"{claimedBotName} {discriminator}";
            if (IsSpoofedClaim(signals)) composed += SpoofedMarker;
            return composed;
        }

        // Verdict-honest naming is enforced at the post-orchestration layer
        // (DetectionLedgerExtensions.BuildEvidenceFromLedger), NOT here in the
        // matcher's Compose. At matcher time (priority 6) the UA contributor
        // (priority 10) hasn't run yet, so ua.bot_name is absent for everything
        // -- and BotPatternLoader's well-known list is narrower than uap-core's
        // family parser, so a known catalog bot like CrunchBot fails Priority 1
        // here even though the UA contributor would later identify it. Firing
        // "Unknown Bot" at this layer was renaming legitimate catalog bots.
        // The ledger-side override has full signal access (ua.bot_name +
        // ledger.BotProbability + IdentityArchetypeName/Kind) and is the right
        // place for the verdict-honest rewrite.

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
        // human-browser AND human-adblocker are both "real human" kinds for
        // naming purposes -- the adblocker variant is the system's first-class
        // human-positive signal, so a fingerprint that lands on a uBlock
        // Origin / AdGuard / generic-adblocker root must surface that name
        // as the display name instead of falling through to plain UA family.
        if (!string.IsNullOrEmpty(archetypeName)
            && (archetypeKind == "human-browser" || archetypeKind == "human-adblocker"))
        {
            return archetypeName;
        }

        // Priority 3: UA family + OS characterization. Reads the signals first; falls back
        // to parsing the raw UA string when signals are absent. Two sources of the raw UA:
        // the explicit userAgent parameter (matcher's hot path passes it) AND signals[ua.raw]
        // (UserAgentContributor writes it on every request). Either one is enough to
        // self-rescue Priority 3 when ua.family / user_agent.os signals haven't been
        // written yet -- matcher Priority 6 runs before UserAgentContributor Priority 10,
        // so signals[ua.family] is missing on the matcher's first call but signals[ua.raw]
        // is populated by the time BuildEvidenceFromLedger calls ResolveDisplayName.
        // Reading signals[ua.raw] here closes the gap that left "Chrome on macOS" humans
        // falling through to a UA prefix label at Priority 4 (or null when even that
        // failed). The dashboard was rendering 8-char signature hashes for these rows
        // before this fix.
        var family = GetString(signals, SignalKeys.UserAgentFamily);
        var rawUaForParse = !string.IsNullOrEmpty(userAgent)
            ? userAgent
            : GetString(signals, SignalKeys.UserAgent);
        if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(rawUaForParse))
            family = UserAgentParser.Parse(rawUaForParse).Family;

        string? finalName;
        if (!string.IsNullOrEmpty(family) && family != "Other")
        {
            // Display-name contract (T5, 2026-06-22, user-approved): priority-3
            // returns the UA family UNCHANGED. No OS prefix, no version, no
            // archetype suffix, no "w/" -- those belong to the drift / detail
            // surfaces, not the name. See spec at
            //   docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md
            // and FingerprintNameComposerContract. The user-directed ban is
            // explicit: "Mac Chrome 149 w/ uBlock -- multi-word descriptive
            // synthetics, even if accurate, fight the bot-name / browser-family
            // / unknown trichotomy. Drop. (Task #121 was the wrong direction.)"
            finalName = family;
        }
        else
        {
            // Priority 4: raw UA prefix as a visible last-resort label. User direction
            // 2026-06-15: showing the head of the actual UA string is always more useful
            // than a null/anonymous label or the legacy "analysing" / "unknown xxx"
            // placeholders. IsFallback below recognises this shape (UAs always contain
            // a "/") so hysteresis still lets a real Priority 1-3 name win when it
            // later becomes available, and the persist site still treats it as a
            // fallback to keep it out of long-term fingerprint records.
            var fallbackUa = GetString(signals, SignalKeys.UserAgent) ?? userAgent;
            if (!string.IsNullOrEmpty(fallbackUa))
            {
                finalName = fallbackUa.Length > UaPrefixMaxLength
                    ? fallbackUa[..UaPrefixMaxLength] + "…"
                    : fallbackUa;
            }
            else
            {
                // No UA at all -- emit a canonical Unknown terminal. NoUserAgentFallback
                // is retained as a const so IsFallback still recognises legacy stored
                // "No User-Agent" rows for hysteresis-aware overwrite, but Compose
                // never produces that string.
                finalName = ComposeUnknownTerminal(signals, fingerprintId);
            }
        }

        // Defensive contract gate (T5, 2026-06-22). The priority-1/2 returns above
        // short-circuit before this point and carry their own shapes (bot name with
        // optional discriminator/spoofed marker, or matched archetype name). Anything
        // that funnels through priority-3/4 must conform to the three allowed shapes
        // (bot name | browser family | Unknown <hex>) or it is re-routed to the
        // Unknown terminal so we never emit a lying multi-token name string.
        // See docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md.
        if (!FingerprintNameComposerContract.IsAllowedShape(finalName))
        {
            finalName = ComposeUnknownTerminal(signals, fingerprintId);
        }
        return finalName;
    }

    /// <summary>
    ///     Cached bot-probability threshold for the verdict-honest naming branch.
    ///     At or above this value the name composer treats the fingerprint as a bot
    ///     for naming purposes -- the archetype matcher is ignored and the name is
    ///     emitted via ComposeUnknownBot. 0.5 catches Medium / Elevated /
    ///     High / VeryHigh risk bands; below it the matcher's archetype name still
    ///     wins so human visitors continue to display their real browser identity.
    /// </summary>
    public const double BotProbabilityNamingThreshold = 0.5;

    /// <summary>
    ///     "Unknown Bot" terminal for fingerprints the bot classifier flagged but
    ///     for which Priority 1 produced no catalog match. Uniquifies on real UA
    ///     signals so adjacent rows are still distinguishable:
    ///       family + version + os -> "Unknown Bot Win Chrome 149"
    ///       family + os            -> "Unknown Bot Win Chrome"
    ///       family + version       -> "Unknown Bot Chrome 149"
    ///       family                 -> "Unknown Bot Chrome"
    ///       no UA at all           -> "Unknown Bot"
    ///     The "Unknown Bot " prefix is recognised by IsFallback so a later
    ///     Priority 1 catalog match (e.g., the UA contributor finally writes
    ///     ua.bot_name) overrides this name on next compose -- it's a verdict-
    ///     anchored placeholder, not a permanent label.
    ///
    ///     Public because the verdict-honest override at
    ///     DetectionLedgerExtensions.BuildEvidenceFromLedger calls this directly
    ///     to rewrite a human-browser archetype name when the CURRENT-request
    ///     verdict disagrees. The matcher's Priority-1.5 branch above catches
    ///     the same case on the 2nd+ request via cached probability; both layers
    ///     compose so a misclassified row gets verdict-honest naming on the
    ///     first request AND has the persisted name self-heal on the next.
    /// </summary>
    public static string ComposeUnknownBot(
        IReadOnlyDictionary<string, object> signals,
        string? userAgent = null)
    {
        var family = GetString(signals, SignalKeys.UserAgentFamily);
        var familyVersion = GetString(signals, SignalKeys.UserAgentFamilyVersion);
        var os = GetString(signals, SignalKeys.UserAgentOs);
        var rawUaForParse = !string.IsNullOrEmpty(userAgent)
            ? userAgent
            : GetString(signals, SignalKeys.UserAgent);
        if ((string.IsNullOrEmpty(family) || string.IsNullOrEmpty(familyVersion)) && !string.IsNullOrEmpty(rawUaForParse))
        {
            var parsed = UserAgentParser.Parse(rawUaForParse);
            if (string.IsNullOrEmpty(family)) family = parsed.Family;
            if (string.IsNullOrEmpty(familyVersion)) familyVersion = parsed.Version;
        }
        if (string.IsNullOrEmpty(os) && !string.IsNullOrEmpty(rawUaForParse))
            os = UserAgentParser.ExtractOs(rawUaForParse);

        if (string.IsNullOrEmpty(family) || family == "Other")
            return "Unknown Bot";

        var major = ExtractMajorVersion(familyVersion);
        var osShort = ShortOs(os);
        return (osShort, major) switch
        {
            ({ Length: > 0 } o, { Length: > 0 } v) => $"Unknown Bot {o} {family} {v}",
            ({ Length: > 0 } o, _)                 => $"Unknown Bot {o} {family}",
            (_, { Length: > 0 } v)                 => $"Unknown Bot {family} {v}",
            _                                       => $"Unknown Bot {family}"
        };
    }

    /// <summary>
    ///     Major version from a uap-core version string ("122.0.6261.94" -> "122",
    ///     "13" -> "13", "" -> null). Single source for major-version extraction;
    ///     no parallel implementations in views.
    /// </summary>
    private static string? ExtractMajorVersion(string? familyVersion)
    {
        if (string.IsNullOrEmpty(familyVersion)) return null;
        var dot = familyVersion.IndexOf('.');
        var major = dot > 0 ? familyVersion[..dot] : familyVersion;
        return major.Length == 0 ? null : major;
    }

    /// <summary>
    ///     Short OS form for display: "Windows" -> "Win", "Mac OS X" / "macOS" -> "Mac".
    ///     Other OS families pass through unchanged ("iOS", "Android", "Linux",
    ///     "Chrome OS"). Returns null when input is null/empty so callers can branch
    ///     on missing OS.
    /// </summary>
    private static string? ShortOs(string? os)
    {
        if (string.IsNullOrEmpty(os)) return null;
        return os switch
        {
            "Windows"               => "Win",
            "Mac OS X" or "macOS"   => "Mac",
            _                        => os
        };
    }

    /// <summary>
    ///     The "Unknown" terminal name. Picks the most informative discriminator from
    ///     what is actually known about the request, in order of preference:
    ///       1. Fingerprint-id prefix (8 hex chars) when allocation has happened.
    ///       2. ASN ("Unknown AS15169") -- network-level identity, present from the
    ///          first request because the IP contributors run before UA parsing.
    ///       3. Country code ("Unknown US") -- geo signal, also network-level.
    ///       4. Bare "Unknown" -- only when literally no discriminating signal exists.
    ///
    ///     Never emits "Unknown 00000000" -- that all-zero placeholder is a lying name
    ///     that pretends a fingerprint exists when it does not. If we cannot honestly
    ///     produce a prefix, we say so by using a real-signal discriminator or none.
    /// </summary>
    private static string ComposeUnknownTerminal(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId)
    {
        if (!string.IsNullOrEmpty(fingerprintId) && fingerprintId.Length >= 8)
            return $"Unknown {fingerprintId[..8]}";

        var asn = GetString(signals, SignalKeys.IpAsn);
        if (!string.IsNullOrEmpty(asn)) return $"Unknown AS{asn}";

        var country = GetString(signals, SignalKeys.GeoCountryCode);
        if (!string.IsNullOrEmpty(country)) return $"Unknown {country}";

        return "Unknown";
    }

    /// <summary>
    ///     Terminal name written when a request truly had no UA. Public so
    ///     readers can decide whether to render it as a dimmed placeholder
    ///     versus a real name. Recognised by <see cref="IsFallback"/> so
    ///     subsequent real names overwrite it on persistence.
    /// </summary>
    public const string NoUserAgentFallback = "No User-Agent";

    /// <summary>
    ///     Cap on the visible UA prefix produced by Priority 4. Long enterprise UAs
    ///     (Skype, Outlook, build tooling) can run 200+ characters and would blow up
    ///     dashboard row layouts; the full UA is always available on the signature
    ///     detail page where it has its own dedicated visible block.
    /// </summary>
    private const int UaPrefixMaxLength = 48;

    /// <summary>
    ///     True when <paramref name="composedName"/> is null/empty, a legacy fallback
    ///     ("analysing" / "unknown xxx" / "(country:sigprefix)"-decorated form of either),
    ///     OR a Priority-4 raw-UA-prefix label.
    ///     <para>
    ///     The UA-prefix shape is detected by the presence of a "/" -- every UA carries
    ///     one ("Mozilla/5.0", "Mastodon/4.3.0", "curl/8.0"), and the real Priority 1-3
    ///     composed names never do ("Googlebot", "Chrome on Windows", "Mastodon
    ///     mastodon.social"). Marking these as fallback keeps hysteresis honest: a
    ///     previously-stored real name will still override a fresh UA prefix on
    ///     subsequent requests, and the matcher's persist site will not overwrite a
    ///     stored real name with a UA prefix.
    ///     </para>
    ///     Strips a legacy " (...)" suffix before testing the base name.
    /// </summary>
    public static bool IsFallback(string? composedName)
    {
        if (string.IsNullOrEmpty(composedName)) return true;
        if (composedName == NoUserAgentFallback) return true;
        var paren = composedName.IndexOf(" (", StringComparison.Ordinal);
        var baseName = paren > 0 ? composedName[..paren] : composedName;
        if (baseName == "analysing"
            || baseName == "Unknown"
            || baseName.StartsWith("unknown ", StringComparison.Ordinal)
            || baseName.StartsWith("Unknown ", StringComparison.Ordinal))
            return true;
        // Raw-UA-prefix detection. Every UA token carries a "/" between the product
        // name and version; the structured Priority 1-3 outputs never do.
        return composedName.Contains('/');
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

    /// <summary>
    ///     Resolve the bot/tool/client name CLAIMED by this request, in priority order:
    ///     <list type="number">
    ///         <item><c>ua.bot_name</c> from the signal dict, when UserAgentContributor
    ///             has already populated it for this request.</item>
    ///         <item>Direct scan of the raw UA via <see cref="BotPatternLoader.MatchUserAgent"/>.
    ///             The loader walks every catalogued substring (search engines, AI
    ///             scrapers, fediverse servers, developer tools, social media, monitoring,
    ///             SEO tools) and returns the first hit. This is the path that rescues
    ///             the matcher-before-UserAgentContributor race -- the catalog is the
    ///             source of truth, not the cached signal.</item>
    ///     </list>
    ///     The catalogue itself lives in YAML (<c>Definitions/BotPatterns/*.bot-patterns.yaml</c>)
    ///     per <c>feedback_no_word_lists</c>; adding a new claim marker is a YAML edit.
    ///     Returns null when neither source yields a usable name.
    /// </summary>
    private static string? ExtractClaimedBotName(
        IReadOnlyDictionary<string, object> signals,
        string? rawUa)
    {
        var cached = GetString(signals, SignalKeys.UserAgentBotName);
        if (!string.IsNullOrEmpty(cached) && cached != "unknown")
            return cached;

        if (string.IsNullOrEmpty(rawUa)) return null;
        var (_, botName) = BotPatternLoader.Default.MatchUserAgent(rawUa, WellKnownBotIndex.Default);
        return string.IsNullOrEmpty(botName) ? null : botName;
    }
}
