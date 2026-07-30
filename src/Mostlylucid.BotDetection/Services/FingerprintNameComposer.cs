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
    ///     Canonical display name for a legitimate automated health probe (kube-probe,
    ///     curl, Go-http-client, etc.) hitting a health-check endpoint from a trusted
    ///     source (loopback / RFC1918). Set at the classification site in
    ///     <see cref="Orchestration.DetectionLedgerExtensions.ToAggregatedEvidence"/>
    ///     where both the sink-read <c>health_endpoint</c> flag and the
    ///     <see cref="HealthEndpoints.ProbeShapeClassifier"/> result are already known.
    ///     Treated as a non-fallback name by <see cref="IsFallback"/>: it contains no
    ///     "/", is not "analysing", and is not "Unknown ...", so it survives the
    ///     hysteresis gate and will not be overwritten by a UA-derived fallback.
    /// </summary>
    public const string HealthProbeName = "Health Probe";

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
    ///     <c>Fingerprint.InducedName</c> here.
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

        // Priority 2: projection from observed signals. Spec
        //   docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md
        // Name reflects what THIS fingerprint looks like right now: browser
        // family + version + OS + OS version + directly-observed modifiers.
        // Two fingerprints differ iff their signals differ -> their names
        // must differ. Unique by construction.
        //
        // INFERRED archetype names (header-drift, Mastodon Family centroid
        // grazing, etc.) are explicitly EXCLUDED here -- those go to the
        // drift-badge column. Only directly-observed signals feed the name.
        //
        // Source order for each component:
        //   1. signal dict (UserAgentContributor wrote it)
        //   2. UserAgentParser fallback (matcher races UserAgentContributor;
        //      uap-core gives us the full Family+Version+Os+OsVersion tuple)
        var family = GetString(signals, SignalKeys.UserAgentFamily);
        var version = GetString(signals, SignalKeys.UserAgentFamilyVersion);
        var os = GetString(signals, SignalKeys.UserAgentOs);
        string? osVersion = null;
        var rawUaForParse = !string.IsNullOrEmpty(userAgent)
            ? userAgent
            : GetString(signals, SignalKeys.UserAgent);
        if (!string.IsNullOrEmpty(rawUaForParse) &&
            (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(version)
             || string.IsNullOrEmpty(os) || string.IsNullOrEmpty(osVersion)))
        {
            // Pattern-match against the nullable tuple explicitly. The original
            // `parsed is var (a, b)` form deconstructs eagerly, which throws on
            // a null tuple (uap-core's no-match return) and silently kills the
            // matcher's fire-and-forget recompose. Use `is { } x` then
            // deconstruct.
            var parsed = Helpers.UapCoreUserAgentParser.Default.Parse(rawUaForParse);
            if (parsed is { } parsedHit)
            {
                family ??= parsedHit.Family;
                version ??= parsedHit.Version;
            }
            var parsedOs = Helpers.UapCoreUserAgentParser.Default.Os(rawUaForParse);
            if (parsedOs is { } parsedOsHit)
            {
                os ??= parsedOsHit.Family;
                osVersion ??= parsedOsHit.Version;
            }
        }

        string? finalName;
        if (!string.IsNullOrEmpty(family) && family != "Other")
        {
            // Short-but-distinct display name: family + MAJOR version + OS NAME. The full
            // patch version (149.0.0.0) and the OS version (10.15.7) are dropped -- they
            // blew the name out to ~30 chars and overflowed the list cells without adding
            // useful distinction (operator 2026-07-10: not a 50-char name, but NOT 50 bare
            // "Chrome" rows either). family+major+os keeps a fleet of Chrome visitors
            // distinguishable ("Chrome 149 macOS" vs "Chrome 148 Windows") while staying
            // list-length; genuine same-name collisions are disambiguated by the matcher
            // via BuildDistinctiveModifier (ASN / country).
            var parts = new List<string>(3) { family };
            var major = ExtractMajorVersion(version);
            if (!string.IsNullOrEmpty(major)) parts.Add(major);
            if (!string.IsNullOrEmpty(os) && os != "Other") parts.Add(os);
            _ = osVersion; // parsed above for the fallback chain; intentionally not shown.
            var baseName = string.Join(' ', parts);
            var modifiers = CollectObservedModifiers(signals);
            finalName = string.IsNullOrEmpty(modifiers) ? baseName : $"{baseName} {modifiers}";
        }
        else
        {
            // Priority 4: last resort. Do NOT blurt the raw UA into the name column --
            // it is lazy, long, and not a name (operator 2026-07-10: "we're being lazy and
            // just blurting whatever is in the UA... 'welcome to cupertino'"). The raw UA
            // was never one of the three allowed name shapes anyway (bot name | browser
            // family | Unknown <disc>), so the old prefix-blurt contradicted the contract.
            // ComposeUnknownTerminal keeps the name SHORT and per-fingerprint / per-network
            // DISTINCT (Unknown <fp8> when a fingerprint id is known, else Unknown AS<asn>
            // / Unknown <country>) so rows stay distinguishable without dumping UA content.
            // The full UA remains visible in its own dedicated block on the detail page.
            finalName = ComposeUnknownTerminal(signals, fingerprintId, rawUaForClaim);
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
            finalName = ComposeUnknownTerminal(signals, fingerprintId, rawUaForClaim);
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
        // "Unknown is not a valid state" (operator directive 2026-07-30). The verdict says
        // this is a bot but Priority 1 found no catalog claim. Instead of "Unknown Bot" we
        // synthesise what it is BEHAVING LIKE (scanner / scraper / attacker / config-crawler)
        // and WHO/WHERE it is (self-identified domain / hosting org / ASN / country). The
        // browser family is deliberately NOT used here: this override fires precisely when the
        // family-derived (human archetype) name disagreed with a bot verdict, so naming it
        // "Unknown Bot Chrome" would re-assert the identity we just overrode.
        var rawUa = !string.IsNullOrEmpty(userAgent)
            ? userAgent
            : GetString(signals, SignalKeys.UserAgent);
        return SynthesizeBehavioralIdentity(signals, fingerprintId: null, rawUa, treatAsBot: true);
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
    ///     Terminal name for a request that produced no Priority 1-3 name. "Unknown is not a
    ///     valid state" (operator directive 2026-07-30): we ALWAYS have enough evidence to say
    ///     WHAT the actor is behaving like and WHO/WHERE it is, so this delegates to
    ///     <see cref="SynthesizeBehavioralIdentity"/> and never emits the word "Unknown".
    ///     Kept as a named method because <see cref="ComposeFresh"/> calls it from two sites
    ///     (the Priority-4 branch and the contract-shape re-route).
    /// </summary>
    private static string ComposeUnknownTerminal(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId,
        string? rawUa = null)
        => SynthesizeBehavioralIdentity(signals, fingerprintId, rawUa, treatAsBot: false);

    /// <summary>
    ///     Synthesise "{what it is behaving like} · {who/where it is}" from signals already on
    ///     the blackboard, so an actor with no catalog match is NEVER labelled "Unknown"
    ///     (operator directive 2026-07-30: "UNKNOWN IS NOT A VALID STATE ... what is it
    ///     BEHAVING LIKE"). The name re-derives from the CURRENT request's behaviour on every
    ///     compose, so it updates as the fingerprint drifts (scanner today, attacker tomorrow).
    ///     <para>
    ///     Role (behaviour) comes from <see cref="DeriveBehavioralRole"/>; identity (org / ASN /
    ///     country / self-declared domain) from <see cref="DeriveIdentityQualifier"/>. At least
    ///     one is essentially always present in production; the trailing fingerprint-id and
    ///     final "Unclassified Client" guards exist only so the method is total (never null /
    ///     empty / "Unknown") for the invariant test corpus.
    ///     </para>
    /// </summary>
    private static string SynthesizeBehavioralIdentity(
        IReadOnlyDictionary<string, object> signals,
        string? fingerprintId,
        string? rawUa,
        bool treatAsBot)
    {
        var role = DeriveBehavioralRole(signals, treatAsBot);
        var identity = DeriveIdentityQualifier(signals, rawUa);

        if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(identity))
            return $"{role} · {identity}";
        if (!string.IsNullOrEmpty(role))
            return role;
        if (!string.IsNullOrEmpty(identity))
            return identity;

        // No behaviour and no network identity: still name it by the stable fingerprint id
        // rather than "Unknown". "Client {fp8}" is recognised by IsFallback so any later
        // real name (catalog / browser / behavioural role) overrides it.
        if (!string.IsNullOrEmpty(fingerprintId) && fingerprintId.Length >= 8)
            return $"Client {fingerprintId[..8]}";
        return "Unclassified Client";
    }

    /// <summary>
    ///     Behavioural role -- "what is it behaving like" -- from the highest-specificity
    ///     signal present, all already computed by upstream atoms. Returns null only when no
    ///     behavioural signal exists AND the verdict isn't bot-leaning, so the caller falls
    ///     back to an identity-only name. The role labels are a display mapping of already-
    ///     computed classifications (like <see cref="ShortOs"/>), not a detection catalog.
    /// </summary>
    private static string? DeriveBehavioralRole(IReadOnlyDictionary<string, object> signals, bool treatAsBot)
    {
        // Attack/probe surface (HaxxorAtom / CveFingerprintAtom) -- most specific.
        if (GetFlag(signals, SignalKeys.AttackConfigExposure)) return "Config Scanner";
        if (GetFlag(signals, SignalKeys.AttackAdminScan)) return "Admin Scanner";
        if (GetFlag(signals, SignalKeys.CveProbeDetected)) return "Vuln Scanner";
        if (GetFlag(signals, SignalKeys.AttackPathProbe)) return "Path Scanner";

        // Session intent (IntentAtom): attacking / scanning / reconnaissance / ad_fraud / browsing.
        var intent = GetString(signals, SignalKeys.IntentCategory);
        switch (intent)
        {
            case "attacking": return "Attacker";
            case "scanning": return "Scanner";
            case "reconnaissance": return "Recon Bot";
            case "ad_fraud": return "Click Fraud";
        }

        if (GetFlag(signals, SignalKeys.AiScraperDetected)) return "AI Scraper";

        // Coarse bot-type taxonomy (UserAgentAtom writes the BotType enum name).
        var botType = GetString(signals, SignalKeys.UserAgentBotType)
                      ?? GetString(signals, SignalKeys.IdentityBotType);
        var roleFromType = botType switch
        {
            nameof(BotType.Scraper)        => "Scraper",
            nameof(BotType.ExploitScanner) => "Exploit Scanner",
            nameof(BotType.AiBot)          => "AI Bot",
            nameof(BotType.Tool)           => "Tool",
            nameof(BotType.MonitoringBot)  => "Monitor",
            nameof(BotType.ClickFraud)     => "Click Fraud",
            nameof(BotType.MaliciousBot)   => "Malicious Bot",
            nameof(BotType.SearchEngine)   => "Crawler",
            nameof(BotType.SocialMediaBot) => "Social Bot",
            _                               => null
        };
        if (!string.IsNullOrEmpty(roleFromType)) return roleFromType;

        // No named behaviour, but the verdict leans bot -> generic automated role (recognised
        // by IsFallback so a specific role/browser/catalog name still overrides it later).
        if (treatAsBot || GetDouble(signals, SignalKeys.IdentityCachedBotProbability) >= BotProbabilityNamingThreshold)
            return "Automated Client";

        return null;
    }

    /// <summary>
    ///     "Who / where" qualifier for a synthesised name, most-identifying first:
    ///       1. A domain the UA self-declares (Cortex-Xpanse, research scanners) -- the actor
    ///          literally told us who it is (<see cref="UserAgentDiscriminator.ExtractSelfIdentDomain"/>).
    ///       2. Hosting provider / ASN org -- network-level identity, present from request 1.
    ///       3. ASN number, then country code.
    ///     Raw IP / CIDR is intentionally NOT used: it is PII-adjacent and the "/" would fail
    ///     <see cref="FingerprintNameComposerContract.IsAllowedShape"/>. Returns null when no
    ///     network identity is known at all.
    /// </summary>
    private static string? DeriveIdentityQualifier(IReadOnlyDictionary<string, object> signals, string? rawUa)
    {
        var selfDomain = UserAgentDiscriminator.ExtractSelfIdentDomain(rawUa);
        if (!string.IsNullOrEmpty(selfDomain)) return selfDomain;

        var provider = SanitizeQualifier(GetString(signals, SignalKeys.IpProvider));
        if (!string.IsNullOrEmpty(provider)) return provider;

        var asnOrg = SanitizeQualifier(GetString(signals, SignalKeys.IpAsnOrg));
        if (!string.IsNullOrEmpty(asnOrg)) return asnOrg;

        var asn = GetString(signals, SignalKeys.IpAsn);
        if (!string.IsNullOrEmpty(asn)) return $"AS{asn}";

        var country = GetString(signals, SignalKeys.GeoCountryCode);
        if (!string.IsNullOrEmpty(country)) return country;

        return null;
    }

    /// <summary>
    ///     Strip characters an org/provider string may carry that
    ///     <see cref="FingerprintNameComposerContract.IsAllowedShape"/> forbids ("Amazon (AWS)",
    ///     "Foo / Bar") so a synthesised name always passes the shape gate. Collapses the
    ///     resulting whitespace; returns null if nothing usable remains.
    /// </summary>
    private static string? SanitizeQualifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var cleaned = value.Replace('(', ' ').Replace(')', ' ').Replace('/', ' ');
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>
    ///     Tolerant boolean read: sink hints project into the signal dict as either a real
    ///     <see cref="bool"/> or the string "true" depending on the composition path, so a
    ///     plain <c>is bool</c> check silently misses half of them (the sink-&gt;evidence
    ///     typing seam that has bitten naming before). Accepts both.
    /// </summary>
    private static bool GetFlag(IReadOnlyDictionary<string, object> signals, string key)
    {
        if (!signals.TryGetValue(key, out var v)) return false;
        return v switch
        {
            bool b   => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _        => false
        };
    }

    /// <summary>
    ///     Terminal name written when a request truly had no UA. Public so
    ///     readers can decide whether to render it as a dimmed placeholder
    ///     versus a real name. Recognised by <see cref="IsFallback"/> so
    ///     subsequent real names overwrite it on persistence.
    /// </summary>
    public const string NoUserAgentFallback = "No User-Agent";

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
            // Back-compat: legacy "Unknown ..." / "Missing UA ..." names persisted before the
            // 2026-07-30 "Unknown is not a valid state" change must still read as fallback so a
            // fresh synthesised name overrides them.
            || baseName == "Unknown"
            || baseName.StartsWith("unknown ", StringComparison.Ordinal)
            || baseName.StartsWith("Unknown ", StringComparison.Ordinal)
            || baseName.StartsWith("Missing UA", StringComparison.Ordinal)
            // Generic behavioural-synth residuals (no specific role found): overridable by a
            // later specific role / browser family / catalog name, so the visible label
            // upgrades from "Automated Client · Azure" to "Config Scanner · ..." on drift.
            || baseName == "Unclassified Client"
            || baseName.StartsWith("Automated Client", StringComparison.Ordinal)
            || baseName.StartsWith("Client ", StringComparison.Ordinal))
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
    ///     Returns the "+ X + Y" tail of directly-observed presentation
    ///     modifiers (uBlock, Tor, private headers) when their signals are
    ///     true in the dict, or empty when none are present. Inferred
    ///     archetype labels are NEVER inputs here -- only signals that the
    ///     fingerprint was computed from. Spec
    ///     docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md.
    /// </summary>
    private static string CollectObservedModifiers(IReadOnlyDictionary<string, object> signals)
    {
        var bits = new List<string>(3);
        if (GetBool(signals, "presentation.has_ublock")) bits.Add("uBlock");
        if (GetBool(signals, "transport.is_tor")) bits.Add("Tor");
        if (GetBool(signals, "header.privacy_drift")) bits.Add("private headers");
        return bits.Count == 0 ? string.Empty : "+ " + string.Join(" + ", bits);
    }

    /// <summary>
    ///     Serialise the projection-input subset of <paramref name="signals"/>
    ///     for persistence on a <c>fingerprint_name_history</c> row. Cold-storage
    ///     drift signifier -- lets the visitor list "was: X" pill + the
    ///     signature-detail history strip render the old fingerprint state
    ///     without joining to <c>fingerprint_observations</c>. Spec
    ///     docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md.
    /// </summary>
    /// <remarks>
    ///     Only the keys the projector actually reads end up in the JSON,
    ///     plus archetype name/kind for drift-comparison context even though
    ///     they don't feed the name. Caller passes <c>null</c> when no live
    ///     signal dict is available (legacy callers); the helper returns
    ///     <c>null</c> so the column stays NULL.
    /// </remarks>
    public static string? SerialiseProjectionSnapshot(IReadOnlyDictionary<string, object>? signals)
    {
        if (signals is null || signals.Count == 0) return null;
        string[] keys =
        {
            SignalKeys.UserAgentFamily,
            SignalKeys.UserAgentFamilyVersion,
            SignalKeys.UserAgentOs,
            SignalKeys.UserAgentBotName,
            SignalKeys.UserAgentBotInstance,
            SignalKeys.IdentityArchetypeName,
            SignalKeys.IdentityArchetypeKind,
            SignalKeys.VerifiedBotSpoofed,
            "presentation.has_ublock",
            "transport.is_tor",
            "header.privacy_drift",
        };
        var snap = new Dictionary<string, object?>(capacity: keys.Length);
        foreach (var k in keys)
            if (signals.TryGetValue(k, out var v)) snap[k] = v;
        return snap.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(snap);
    }

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
