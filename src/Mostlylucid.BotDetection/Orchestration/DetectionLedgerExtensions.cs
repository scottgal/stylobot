using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
/// Extensions to build BotDetection's AggregatedEvidence from taxonomy's DetectionLedger.
/// </summary>
public static class DetectionLedgerExtensions
{
    /// <summary>
    ///     Copy an IReadOnlyDictionary into a fresh Dictionary, hinting
    ///     capacity. Per the perf-throughput baseline (~376 KB/req), the
    ///     ToAggregatedEvidence snapshot copy is one of the larger
    ///     per-request allocations -- the dict has 100+ entries on the
    ///     full-pipeline path, and the default ctor's repeated resize/rehash
    ///     is wasted work.
    /// </summary>
    private static Dictionary<string, object> CopyWithCapacity(IReadOnlyDictionary<string, object> src)
    {
        var dst = new Dictionary<string, object>(src.Count);
        foreach (var kv in src) dst[kv.Key] = kv.Value;
        return dst;
    }


    /// <summary>
    /// Builds an AggregatedEvidence from the detection ledger.
    /// </summary>
    public static AggregatedEvidence ToAggregatedEvidence(
        this DetectionLedger ledger,
        string? policyName = null,
        DetectionPolicyAction? policyAction = null,
        string? actionPolicyName = null,
        bool aiRan = false,
        IReadOnlyDictionary<string, object>? premergedSignals = null,
        BotDetectionOptions? options = null,
        SignalSink? sink = null)
    {
        var botProbability = ledger.BotProbability;
        var confidence = ledger.Confidence;

        // Clamp probability when AI hasn't run.
        // Floor defaults to 0.0 (allowing scores to reach zero on strong human evidence).
        // Ceiling prevents high-confidence bot verdicts without AI confirmation.
        // Configurable via BotDetection:NonAiMinProbability / NonAiMaxProbability.
        if (!aiRan)
        {
            var minProb = options?.NonAiMinProbability ?? 0.05;
            var maxProb = options?.NonAiMaxProbability ?? 0.90;
            botProbability = Math.Clamp(botProbability, minProb, maxProb);
        }

        // Compute coverage-based confidence
        var coverageConfidence = ComputeCoverageConfidence(ledger.ContributingDetectors, aiRan);
        confidence = Math.Min(confidence, coverageConfidence);

        // Extract signals needed for context-aware risk band before building evidence
        // (signals dict built below; extract from ledger merged signals here)
        var preSignals = premergedSignals != null
            ? premergedSignals
            : (IReadOnlyDictionary<string, object>)ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Sink-first bool reader: atoms (IpAtom etc.) raise signals via sink.Raise
        // and never populate contribution.Signals, so preSignals built from
        // ledger.MergedSignals misses all sink-raised booleans in production.
        // Existing callers that pass premergedSignals directly (tests) continue to
        // work via the ?? fallback because sink is null in that code path.
        bool ReadBool(string key) =>
            sink?.ReadBoolHint(key, fallback: false)
            ?? (preSignals.TryGetValue(key, out var v) && v is true);

        var earlyThreatForBand = ExtractThreatScoreRaw(preSignals);
        var isConfirmedBadForBand = IsConfirmedBad(preSignals, sink);
        var sessionCountForBand = ExtractSessionCount(preSignals);
        var intentCategory = preSignals.TryGetValue(SignalKeys.IntentCategory, out var ic) ? ic as string : null;
        // Vendor-IP verification (Commercial-side contributor). bool? semantics:
        //   true  = friendly UA + IP matches vendor range -> proceed to pin
        //   false = friendly UA + IP did NOT match -> skip pin (spoofed UA)
        //   null  = no check attempted -> fall back to UA-only friendly logic
        var friendlyIpVerified = preSignals.TryGetValue(SignalKeys.FriendlyIpVerified, out var fipv)
            ? (bool?)Convert.ToBoolean(fipv)
            : null;
        // NodeInfo / fediverse-domain verification (FediverseDomainContributor in
        // this repo). Same bool? semantics: true = +https://instance/ in UA was
        // confirmed by a NodeInfo lookup; false = lookup ran and failed; null = no
        // check attempted (UA was not fediverse-shaped or verifier disabled).
        // Either this OR friendlyIpVerified satisfies the friendly-pin gate.
        var friendlyDomainVerified = preSignals.TryGetValue(SignalKeys.FriendlyDomainVerified, out var fdv)
            ? (bool?)Convert.ToBoolean(fdv)
            : null;

        // Browser-attestation carve-out for the composer's ConfirmedBad gate
        // (see SignatureRiskVerdictComposer + RiskVerdictOptions). The gate
        // reads ONLY directly-observable transport-layer signals: Sec-Fetch-Site
        // attestation present + raw threat score below the no-threat ceiling.
        // It MUST NOT consult botProbability / confidence: those are the rolled-
        // up axes the carve-out is supposed to influence, and tying them in
        // would re-introduce the parallel-axis bug -- risk becomes coupled to
        // probability, the FastPath verdict cache then replays that coupled
        // verdict on every subsequent same-fingerprint request, and a single
        // bad first-hit pins the row hostile forever even though browser
        // attestation is screaming "real Chrome" on every request that follows.
        // Honeypot hits + injection payloads still trip the raw-threat hostile
        // pin -- behaviour wins for crisp negative signals.
        var rvOpts = options?.RiskVerdict ?? new RiskVerdictOptions();
        var hasFetchAttestation = preSignals.TryGetValue(SignalKeys.ProgrammaticFetchAttestation, out var pfa)
                                  && pfa is true;
        var browserAttestationVerified = rvOpts.BrowserAttestationCarveOutEnabled
                                         && hasFetchAttestation
                                         && earlyThreatForBand <= rvOpts.BrowserAttestationMaxThreatScore;

        // Declared-bot override. When the UA self-identifies as a bot, the bot/human
        // verdict is categorical, not probabilistic -- nobody pretends to be a bot.
        // The sigmoid rollup + 0.90 AI-clamp would otherwise leave a clean Googlebot
        // / Mastodon row looking like "70% bot at 0.4 confidence", which is the wrong
        // framing: probability is 1.0, and the only remaining question lives on the
        // identity axis ("do we trust the claim?"). Confidence here means strictly
        // identity confidence -- high once vendor-IP or NodeInfo verification has run
        // (positive OR negative -- a confirmed spoofer is also a confident identity
        // judgement), modest while the UA is still merely declared.
        var declaredBot = ReadBool(SignalKeys.UserAgentIsBot);
        if (declaredBot)
        {
            botProbability = 1.0;
            var verificationAttempted = friendlyIpVerified.HasValue
                || friendlyDomainVerified.HasValue
                || preSignals.ContainsKey(SignalKeys.VerifiedBotChecked);
            confidence = verificationAttempted ? 1.0 : 0.5;
        }

        // ledger.BotType is last-writer-wins (HeuristicEarly often overwrites the
        // authoritative UA pattern's "GoodBot" with a generic "Scraper" guess), but
        // DetermineRiskBand's own YAML fallback recovers from that by looking the
        // bot name up against BotPatternLoader. The previous belt-and-braces helper
        // (FindFriendlyBotType, scanning contributions for a friendly classification)
        // duplicated that recovery and has been deleted.
        //
        // Fall back to the UserAgent signals when ledger.BotType / BotName are unset.
        // The UserAgentContributor reliably writes UserAgentBotType / UserAgentBotName
        // into preSignals via WriteSignals, but the Ephemeral ledger's BotType / BotName
        // top-level properties stay null on the read path -- AddContribution doesn't
        // promote the contribution's PrimaryBotType / PrimaryBotName onto the ledger
        // surface. Without this fallback the friendly-pin gate exited with
        // botType=null,botName=null for every UA the YAML matcher had successfully
        // identified (Mastodon, Pleroma, etc.). The signals are the canonical
        // single source either way; this just teaches the read site to consult them.
        var ledgerBotType = ParseBotType(ledger.BotType)
                            ?? ParseBotType(preSignals.TryGetValue(SignalKeys.UserAgentBotType, out var uabt) ? uabt as string : null);
        var ledgerBotName = ledger.BotName
                            ?? (preSignals.TryGetValue(SignalKeys.UserAgentBotName, out var uabn) ? uabn as string : null);

        // Local-network promotion runs BEFORE the verdict composer so the
        // composer's trusted-and-aligned clamp (Internal => RiskBand.Low) can
        // actually fire. Previously this override fired at line ~220, AFTER
        // DetermineRiskVerdict had already returned -- so the composer never
        // saw BotType=Internal and produced VeryHigh for every internal-client
        // request (which the dashboard rendered as "Internal · Allow · 100% ·
        // Risk Profile VeryHigh"). Reading IpIsLocal here keeps the override's
        // primary-position-trust semantics intact while routing it through
        // the single Compose() site.
        var localIpForVerdict = ReadBool(SignalKeys.IpIsLocal);
        var verdictBotType = localIpForVerdict ? BotType.Internal : ledgerBotType;

        // Compose the risk verdict via the single-source-of-truth composer. The
        // legacy DetermineRiskBand method is now a thin wrapper that builds the
        // SignatureRiskInputs and delegates -- behaviour preserved (the composer
        // subsumes the dimension-max logic, the AI-aware probability bucketing,
        // and the friendly-pin corroboration check that used to live in
        // DetermineRiskBand). The same composer is now used by /api/v1/topbots
        // OverlayRiskVerdict, so the two paths can't desync any more.
        //
        // Switched from DetermineRiskBand's tuple-of-three to the full verdict
        // so HostilePinFired can feed bot_type + ThreatBand overrides below.
        // Operator-visible bug: a confirmed-bad actor claiming Amazonbot was
        // showing bot_type=GoodBot and ThreatBand from the signals dict (None),
        // which made DetectionPolicyMatcher pick "Silent Throttle" instead of
        // Block. Compose marks HostilePinFired correctly; we now use it.
        var (verdict, friendlyPinTrace) = DetermineRiskVerdict(botProbability, confidence, aiRan,
            earlyThreatForBand, isConfirmedBadForBand, sessionCountForBand, intentCategory,
            verdictBotType, ledgerBotName, friendlyIpVerified, friendlyDomainVerified,
            browserAttestationVerified);
        var riskBand = verdict.RiskBand;
        var riskJustification = verdict.Justification;

        // PrimaryBotType stays gated on classification — it's a claim about WHAT KIND of bot
        // ("this looks like a Scraper") and is only meaningful when classified as bot.
        //
        // HostilePin override: when the composer pinned hostile (ConfirmedBad latch,
        // raw threat above the gate, or hostile-cluster membership), a friendly
        // UA-derived bot_type is wrong by definition. Demote to MaliciousBot so
        // the dashboard heading agrees with the verdict and policy rules keyed
        // on bot_type match the correct catalog entry. The original UA-derived
        // name still flows through PrimaryBotName for operator triage.
        // isActuallyBot is computed AFTER the catalog lookup below so it can
        // respect catalog identity -- see catalogBotType-based override.
        // YAML-catalog authority: when the resolved bot name matches a
        // BotPatternLoader entry, the catalog's BotType wins over
        // ledger.BotType. ledger.BotType is last-writer-wins and
        // HeuristicEarly routinely overwrites the UserAgentContributor's
        // authoritative type with a generic "Scraper" guess (GPTBot ends up
        // as Scraper, GoogleOther ends up as Scraper, etc.) so the dashboard
        // surface and the policy surface have to recover the right type.
        //
        // Name source priority (the SINGLE-SOURCE rule for Step 4 of the
        // surgery):
        //   1. signals[IdentityDisplayName] -- matcher's authoritative
        //      composed name (set by FingerprintMatchContributor).
        //   2. signals[UserAgentBotName] -- canonical UA-catalog match
        //      written by UserAgentContributor; safe across the deletion of
        //      per-contributor `result.BotName` writes which fed
        //      ledger.BotName today. The signal IS the source going forward.
        //   3. ledger.BotName -- the legacy contribution-aggregated source,
        //      retained as a safety fallback until the per-contributor
        //      writes are removed in the follow-up commit.
        // Catalog index by construction only contains entries with non-empty
        // BotType so a non-null FindBotTypeByName return IS authoritative.
        var nameForCatalog =
            (preSignals.TryGetValue(SignalKeys.IdentityDisplayName, out var idn) ? idn as string : null)
            ?? (preSignals.TryGetValue(SignalKeys.UserAgentBotName, out var uabn1) ? uabn1 as string : null)
            ?? ledger.BotName;
        var catalogBotType = ParseBotType(BotPatternLoader.Default.FindBotTypeByName(nameForCatalog));
        var ledgerPrimaryBotType = catalogBotType ?? ParseBotType(ledger.BotType);

        // Catalogue identity IS the bot signal -- a YAML/Arcjet match like
        // Bytespider, Googlebot, GPTBot, etc. declares the request a bot
        // regardless of the probability sigmoid. Without this, low-volume
        // traffic from a confirmed AI crawler hovers below the 0.5 probability
        // gate and the dashboard shows it as Human while the BotName column
        // says "Bytespider" -- the operator-reported "BOTS ARE NEVER HUMAN"
        // contradiction. The catalog is the authoritative identity surface;
        // probability is a confidence axis on TOP of identity, not a gate
        // beneath it.
        var isActuallyBot = botProbability >= 0.5 || catalogBotType is not null;
        // Local-network override: traffic from loopback / RFC1918 / docker
        // bridge is operator-owned by definition (admin curl, dashboard self-
        // poll, BDF test runner, service-to-service). Classify as Internal so
        // the dashboard still LISTS the request but the action-policy mapping
        // routes Internal -> logonly instead of Tool -> throttle-tools.
        // Beats UA-derived BotType because UA is trivially spoofable from
        // inside the network too -- the network position is the trust anchor.
        // Beats the hostile-pin too: reputation latches and raw-threat scores
        // can survive into a fresh test run from an earlier hostile session
        // (BDF replay burst, attack probe) and would otherwise lock the
        // operator out of their own LAN. If the LAN itself is compromised,
        // throttling admin curl is the least of the operator's problems --
        // network position is the trust signal here, full stop. The
        // honeypot path tagger + the hostile actor pipeline still record
        // the bad behaviour to the dashboard; only the action is suppressed
        // for LAN sources.
        //
        // Health-endpoint shape guard (Task 3): on health-endpoint paths the
        // classification requires BOTH source trust AND probe shape. "Source
        // alone" (isLocalIp) is not sufficient on a health path because an
        // on-network attacker can trivially send a browser request to /health.
        // Probe shape = positive probe-UA match (kube-probe, curl, etc.) AND
        // no Sec-Fetch-Mode:navigate signal. For non-health paths, local IP
        // always grants Internal (unchanged behaviour).
        var isHealthEndpoint = ReadBool(SignalKeys.HealthEndpoint);
        var isLocalIp = ReadBool(SignalKeys.IpIsLocal);
        var probeUas = options?.HealthEndpoints?.ProbeUserAgents
                       ?? HealthEndpointOptions.DefaultProbeUserAgents;
        var isHealthProbe = isHealthEndpoint && isLocalIp
            && ProbeShapeClassifier.IsProbeShape(preSignals, sink, probeUas);
        // Non-health-endpoint local traffic: always Internal (unchanged).
        // Health-endpoint local traffic: requires probe shape (shape guard).
        var primaryBotType = (isLocalIp && !isHealthEndpoint) || isHealthProbe
            ? BotType.Internal
            : verdict.HostilePinFired
                ? BotType.MaliciousBot
                : (isActuallyBot ? ledgerPrimaryBotType : null);

        // PrimaryBotName is NEVER gated — every fingerprint always has a name (verdict label
        // and name are separate concerns). Prefer the matcher-set identity.display_name
        // (persisted, drift-gated) over the ledger's UA-derived name.
        var primaryBotName = ResolveDisplayName(preSignals, isActuallyBot ? ledger.BotName : null);

        // Verdict-honest override. When the current-request verdict says bot but the
        // resolved name came from a human-browser archetype match (Chrome XHR shape
        // happens to overlap chrome-desktop's centroid; uBlock-Origin Chrome with
        // minimal headers overlaps Mastodon), rewrite to "Unknown Bot {family}".
        // The archetype centroid coincidence does not get to lie about identity when
        // the classifier disagrees. Closes the prod regression on signature
        // fA_cI4MGbTxkwr9-4UsUEg (Chrome Desktop named, Scraper at 0.90) and the BDF
        // missing-browser-headers fixture (Chrome Desktop named, bot prob 0.90).
        // Pairs with the matcher's Priority-1.5 branch which catches the same case
        // on the next request via cached probability for a self-healing persisted name.
        if (isActuallyBot && IsHumanArchetypeWithoutCatalogClaim(preSignals, primaryBotName))
        {
            primaryBotName = Services.FingerprintNameComposer.ComposeUnknownBot(preSignals);
        }

        // Health-probe name override (Task 6). When the request is classified as a
        // legitimate health probe (loopback/RFC1918 source + probe UA shape on a
        // health-endpoint path), set the canonical display name. Runs AFTER the
        // verdict-honest override so any "Unknown Bot" rewrite is superseded.
        // The isHealthProbe flag is computed via the sink-first ReadBool above, so
        // this correctly fires on the production path where health_endpoint is raised
        // via sink.Raise and never appears in preSignals. FingerprintNameComposer
        // is NOT the right site: its preSignals dict never contains health_endpoint
        // (sink-only signal), so a short-circuit there would silently never fire.
        if (isHealthProbe)
            primaryBotName = Services.FingerprintNameComposer.HealthProbeName;

        // Handle early exit
        if (ledger.EarlyExit && ledger.EarlyExitContribution != null)
        {
            return CreateEarlyExitResult(ledger, aiRan, policyName, premergedSignals, sink, options);
        }

        // The orchestrator pools its signal ConcurrentDictionary and clears it
        // for the next request, so the receiver of AggregatedEvidence cannot
        // hold the live reference. A snapshot copy is mandatory. Hint the
        // capacity so the destination Dictionary doesn't resize during the
        // foreach copy; without it the default 0 capacity grows
        // 0 -> 7 -> 17 -> 37 -> 67 -> 137 across five rehash operations to
        // accommodate the typical 100+-signal payload.
        var signals = premergedSignals != null
            ? CopyWithCapacity(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var (rawThreatScore, signalsThreatBand) = ExtractThreatScore(signals);

        // HostilePin override: signals-derived threat band can be None when the
        // bad-actor signal is a reputation latch or honeypot tag rather than a
        // raw threat score (which is what feeds ExtractThreatScore). The verdict
        // already composes those into ThreatBand.Critical; lift it to the
        // AggregatedEvidence surface so DetectionPolicyMiddleware sees Critical
        // and the matching catalog rule (ThreatBand: ["Critical"] -> Block)
        // wins over a friendlier earlier rule.
        var threatBand = verdict.HostilePinFired
            ? (ThreatBand)Math.Max((int)signalsThreatBand, (int)verdict.ThreatBand)
            : signalsThreatBand;

        // Lift threat score to the band floor when the verdict promoted the band.
        // Otherwise DetectionBroadcastMiddleware ships (ThreatBand=Critical,
        // ThreatScore=null) -- it nulls score==0 -- and the dashboard shows a
        // band with no underlying number, which reads as "the bot detector is
        // confused" rather than "reputation latch fired". A floor of 0.80 for
        // Critical gives operators a number consistent with the band.
        var threatScore = threatBand > signalsThreatBand
            ? Math.Max(rawThreatScore, Risk.SignatureRiskVerdictComposer.ThreatBandFloor(threatBand))
            : rawThreatScore;

        // Write risk justification back to signals so downstream consumers can read it
        if (!string.IsNullOrEmpty(riskJustification))
            signals[SignalKeys.RiskJustification] = riskJustification;
        if (!string.IsNullOrEmpty(friendlyPinTrace))
            signals[SignalKeys.RiskFriendlyPinTrace] = friendlyPinTrace;

        double priorProbability = 0.0;
        double contributionDelta = 0.0;
        if (preSignals.TryGetValue(SignalKeys.FingerprintPriorProbability, out var rawPrior)
            && TryReadDouble(rawPrior, out var pp))
        {
            priorProbability = pp;
            contributionDelta = botProbability - priorProbability;
        }

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = botProbability,
            Confidence = confidence,
            PriorProbability = priorProbability,
            RequestContributionDelta = contributionDelta,
            RiskBand = riskBand,
            RiskJustification = riskJustification,
            EarlyExit = false,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = primaryBotName,
            Signals = signals,
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            PolicyAction = policyAction,
            TriggeredActionPolicyName = actionPolicyName,
            AiRan = aiRan,
            ThreatScore = threatScore,
            ThreatBand = threatBand
        };
    }

    private static AggregatedEvidence CreateEarlyExitResult(
        DetectionLedger ledger,
        bool aiRan,
        string? policyName,
        IReadOnlyDictionary<string, object>? premergedSignals = null,
        SignalSink? sink = null,
        BotDetectionOptions? options = null)
    {
        var exitContrib = ledger.EarlyExitContribution!;
        var verdict = ParseEarlyExitVerdict(exitContrib.EarlyExitVerdict);
        // is_bot reflects the UA classification, not the early-exit verdict.
        // Whitelisted ("we accept this client") is a verdict on the ACTION,
        // not a claim about whether the client is a bot -- so when the
        // contributor stamped a real BotType (curl=Tool, etc.) the
        // persisted row must carry is_bot=true so the dashboard's row
        // groupings, summary counts, and BotType chip agree with each
        // other. The previous "Tool but Human Allow" disconnect on
        // internal-network curl rows came from inferring is_bot purely
        // from the verdict. VerifiedGoodBot / VerifiedBadBot remain
        // bots by definition; Whitelisted / Blacklisted defer to the UA
        // classification; everything else stays human.
        var exitBotType = ParseBotType(exitContrib.BotType);
        // BotType.Internal excluded: it's an operator-owned semantic flag, not a
        // UA-pattern bot. Without this exclusion an early-exit Whitelisted /
        // Blacklisted verdict on an Internal-classified row (e.g. RFC1918 source
        // visitor) was getting persisted as is_bot=true with probability=1.0,
        // surfacing real browsers as "100% bot" on the dashboard YOU widget.
        // Mirrors the Skip-path fix in BotDetectionMiddleware:594.
        var hasMeaningfulBotType = exitBotType is not null
                                   and not BotType.Unknown
                                   and not BotType.Internal;
        var isBot = verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot => true,
            EarlyExitVerdict.VerifiedBadBot  => true,
            EarlyExitVerdict.Whitelisted     => hasMeaningfulBotType,
            EarlyExitVerdict.Blacklisted     => hasMeaningfulBotType,
            _                                => false
        };

        var earlySignals = premergedSignals != null
            ? new Dictionary<string, object>(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Same sink-first bool reader as in ToAggregatedEvidence: atoms raise
        // via sink.Raise and never populate earlySignals when premergedSignals is null.
        bool ReadBool(string key) =>
            sink?.ReadBoolHint(key, fallback: false)
            ?? (earlySignals.TryGetValue(key, out var v) && v is true);

        var (earlyThreatScore, earlyThreatBand) = ExtractThreatScore(earlySignals);

        var earlyRiskBand = verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot => RiskBand.VeryLow,
            EarlyExitVerdict.Whitelisted     => RiskBand.VeryLow,
            EarlyExitVerdict.VerifiedBadBot  => RiskBand.VeryHigh,
            EarlyExitVerdict.Blacklisted     => RiskBand.VeryHigh,
            _                                => RiskBand.Medium
        };
        var earlyRiskJustification = verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot => "Cryptographically verified good bot",
            EarlyExitVerdict.Whitelisted     => "Explicitly whitelisted",
            EarlyExitVerdict.VerifiedBadBot  => "Verified bad bot",
            EarlyExitVerdict.Blacklisted     => "Explicitly blacklisted",
            _                                => "Early exit policy"
        };

        // Friendly UA classification overrides reputation-driven VerifiedBadBot, and pulls
        // the friendly UA's BotName so the dashboard shows "Mastodon" rather than the
        // reputation-pattern id ("ip:::ffff::/48") that FastPathReputation supplied.
        // Confirmed-bad and high threat still escalate.
        var primaryBotName = ResolveDisplayName(earlySignals, exitContrib.BotName);
        // Verdict-honest override for the early-exit path. Same rule as the post-
        // orchestration path above: when the early-exit verdict says bot AND the
        // resolved name came from a human-browser archetype hit, rewrite to
        // "Unknown Bot {family}". The reputation/early-bypass shortcut does not get
        // to ship a human-archetype label for a bot verdict either.
        if (isBot && IsHumanArchetypeWithoutCatalogClaim(earlySignals, primaryBotName))
        {
            primaryBotName = Services.FingerprintNameComposer.ComposeUnknownBot(earlySignals);
        }
        // Catalog authority (same rule as the post-orchestration path): when the
        // resolved BotName matches a BotPatternLoader entry, prefer the catalog's
        // BotType over the contributor's emission. Stops the early-exit shortcut
        // from disagreeing with the dashboard's slower path on the same UA.
        var primaryBotType = ParseBotType(BotPatternLoader.Default.FindBotTypeByName(primaryBotName))
                             ?? ParseBotType(exitContrib.BotType);
        if (verdict is EarlyExitVerdict.VerifiedBadBot)
        {
            var friendlyContrib = FindFriendlyBotContribution(ledger);
            var isConfirmedBadEarly = IsConfirmedBad(earlySignals, sink);
            var hostileSignalsPresent = HasHostileSignals(earlySignals, earlyThreatScore, sink);

            if (friendlyContrib != null && !isConfirmedBadEarly && earlyThreatScore < 0.55)
            {
                var friendlyType = ParseBotType(friendlyContrib.BotType);
                earlyRiskBand = RiskBand.Low;
                earlyRiskJustification = $"identified as {friendlyType} (friendly automation; reputation-cache override)";
                primaryBotType = friendlyType;
                // Friendly-contributor name override only kicks in when the matcher
                // hasn't already written IdentityDisplayName. With per-contributor
                // result.BotName writes deleted (Step 4b), the contribution may
                // not carry a BotName; fall through to the UA contributor's
                // canonical UserAgentBotName signal which is written regardless.
                if (!earlySignals.ContainsKey(SignalKeys.IdentityDisplayName))
                {
                    var friendlyName = friendlyContrib.BotName
                        ?? (earlySignals.TryGetValue(SignalKeys.UserAgentBotName, out var fubn) ? fubn as string : null);
                    if (!string.IsNullOrEmpty(friendlyName))
                        primaryBotName = friendlyName;
                }
            }
            // Tool-family demotion arm. Parallel to the friendly-bot override: when the
            // early-exit verdict says VerifiedBadBot AND the resolved name catalogs as
            // BotType.Tool (curl, wget, python-requests, etc.) AND no hostile evidence
            // is present (SecurityToolDetected, honeypot hit, attack-severity, or a
            // real high threat score), demote to BotType.Tool / RiskBand.Elevated --
            // a ConfirmedBad IP-pattern accumulated from raw repeat-count alone is not
            // the same evidence as SecurityToolContributor's verdict. Real hostile-
            // tool detection (sqlmap, nikto) flows through SecurityToolContributor and
            // its verdict survives this arm via HasHostileSignals. See the staging
            // incident pinned in LanCurlClampTests.
            else if (IsToolCatalog(primaryBotName, earlySignals)
                     && !isConfirmedBadEarly
                     && !hostileSignalsPresent)
            {
                earlyRiskBand = RiskBand.Elevated;
                earlyRiskJustification = "Developer tool UA (no hostile signals; reputation-cache override)";
                primaryBotType = BotType.Tool;
            }
        }

        // Network-position-Internal clamp on the early-exit path. Symmetry fix:
        // the non-early-exit composer already clamps to BotType.Internal /
        // RiskBand.VeryLow when SignalKeys.IpIsLocal == true, but the early-exit
        // path did not -- so a stale IP-rep latch survived as VerifiedBadBot
        // for LAN traffic. The clamp runs LAST so it overrides every other arm:
        // operator-owned traffic never reads as VerifiedBadBot regardless of UA
        // classification or reputation history. Honeypot / SecurityToolContributor
        // contributions on a LAN client get demoted by network position; if the
        // LAN itself is compromised the operator has bigger problems than the
        // throttle policy. (See feedback_signals_atoms_pattern + the dashboard
        // Internal-clamp pin in InternalRiskBandClampTests.)
        //
        // Health-endpoint shape guard (Task 3, early-exit parity): mirrors the
        // main-path guard in ToAggregatedEvidence. On a health-endpoint path the
        // Internal clamp requires BOTH local IP AND probe shape; source alone is
        // not sufficient. Non-health local traffic is clamped unconditionally as
        // before.
        if (ReadBool(SignalKeys.IpIsLocal))
        {
            var earlyIsHealthEndpoint = ReadBool(SignalKeys.HealthEndpoint);
            var earlyProbeUas = options?.HealthEndpoints?.ProbeUserAgents
                                ?? HealthEndpointOptions.DefaultProbeUserAgents;
            var earlyIsHealthProbe = earlyIsHealthEndpoint
                                     && ProbeShapeClassifier.IsProbeShape(earlySignals, sink, earlyProbeUas);
            if (!earlyIsHealthEndpoint || earlyIsHealthProbe)
            {
                primaryBotType = BotType.Internal;
                earlyRiskBand = RiskBand.VeryLow;
                earlyRiskJustification = "Internal network traffic (network-trusted; early-exit path)";
                // Health-probe name override (Task 6, early-exit parity). Same rule as
                // the main path: when the request is a confirmed health probe, the
                // canonical display name takes precedence over the UA-derived name.
                if (earlyIsHealthProbe)
                    primaryBotName = Services.FingerprintNameComposer.HealthProbeName;
            }
        }

        if (!string.IsNullOrEmpty(earlyRiskJustification))
            earlySignals[SignalKeys.RiskJustification] = earlyRiskJustification;

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = isBot ? 1.0 : 0.0,
            Confidence = 1.0,
            RiskBand = earlyRiskBand,
            RiskJustification = earlyRiskJustification,
            EarlyExit = true,
            EarlyExitVerdict = verdict,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = primaryBotName,
            Signals = earlySignals,
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            AiRan = aiRan,
            ThreatScore = earlyThreatScore,
            ThreatBand = earlyThreatBand
        };
    }

    /// <summary>
    ///     True when the resolved name came from a human-browser-shaped archetype
    ///     match (IdentityArchetypeKind in {human-browser, human-adblocker}) AND
    ///     no Priority 1 catalog claim is present (ua.bot_name absent / empty).
    ///     A catalog-matched UA is authoritative -- the visitor self-declared as
    ///     "CrunchBot" / "Googlebot" etc. and we honour that even when the request
    ///     ALSO matched an archetype centroid. The override is for the case where
    ///     the matcher landed on a human archetype, no catalog match supplied a
    ///     bot name, AND the verdict says bot anyway: that combination is the
    ///     centroid coincidence the user reported on
    ///     fA_cI4MGbTxkwr9-4UsUEg / missing-browser-headers.
    /// </summary>
    private static bool IsHumanArchetypeWithoutCatalogClaim(
        IReadOnlyDictionary<string, object> signals, string? resolvedName)
    {
        if (string.IsNullOrEmpty(resolvedName)) return false;
        // Belt: a catalog claim wins outright (Googlebot / CrunchBot / Mastodon).
        var hasCatalogClaim = signals.TryGetValue(SignalKeys.UserAgentBotName, out var ubn)
                              && ubn is string s && !string.IsNullOrEmpty(s);
        if (hasCatalogClaim) return false;
        // Braces: only override when the resolved name actually CAME FROM the
        // archetype hit (resolvedName == archetype.Name) AND that archetype is
        // human-shaped. Anything else means the name has another provenance the
        // override has no business rewriting.
        var kind = signals.TryGetValue(SignalKeys.IdentityArchetypeKind, out var k) ? k as string : null;
        if (kind != "human-browser" && kind != "human-adblocker") return false;
        var archetypeName = signals.TryGetValue(SignalKeys.IdentityArchetypeName, out var n) ? n as string : null;
        return !string.IsNullOrEmpty(archetypeName) && string.Equals(archetypeName, resolvedName, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Resolves the display name for this visitor: the matcher-set signal first, then the
    ///     ledger's classifier-supplied <c>BotName</c>. Returns null only when no UA
    ///     is available at all; the dashboard's render layer handles that residual.
    ///     Tier order:
    ///     1. <see cref="SignalKeys.IdentityDisplayName"/> — set by FingerprintMatchContributor
    ///        when the identity layer runs (composed name with discriminator already applied).
    ///     2. <paramref name="fallback"/> — classifier-supplied BotName from the ledger
    ///        (e.g. "Mastodon mastodon.social" after the UserAgentContributor fix).
    ///     3. <see cref="Services.FingerprintNameComposer.Compose"/> — fallback for human
    ///        visitors when Identity is disabled: produces "Chrome 124 / Windows", "Safari /
    ///        iOS", etc. from the raw UA signals so the CLI live table and the dashboard
    ///        never fall back to a signature-hash label.
    /// </summary>
    private static string? ResolveDisplayName(
        IReadOnlyDictionary<string, object> signals, string? fallback)
    {
        var fromSignal = signals.TryGetValue(SignalKeys.IdentityDisplayName, out var v)
            ? v as string : null;
        if (!string.IsNullOrEmpty(fromSignal)) return fromSignal;
        if (!string.IsNullOrEmpty(fallback)) return fallback;
        // Compose from UA signals so humans get "Chrome 124 / Windows" instead of a
        // signature hash when the Identity layer is disabled or hasn't allocated yet.
        return Services.FingerprintNameComposer.Compose(signals);
    }

    private static (double ThreatScore, ThreatBand Band) ExtractThreatScore(
        IReadOnlyDictionary<string, object> signals)
    {
        // Take the max threat score across every contributor that produces one.
        // Previously only intent.threat_score was read, so a request that hit a
        // honeypot path OR carried an injection-class HaxxorContributor signal
        // still resolved to ThreatBand.None on the dashboard -- the operator
        // saw "Threat: None" on an xmlrpc.php brute-force probe because the
        // intent contributor didn't classify the session yet.
        double threatScore = 0.0;

        // 1. Intent classifier (existing, 0-1 scale)
        if (signals.TryGetValue(SignalKeys.IntentThreatScore, out var rawIntent))
            threatScore = Math.Max(threatScore, AsDouble(rawIntent));

        // 2. Project Honeypot DNSBL (writes 0-100; ProjectHoneypotContributor uses
        //    35 = suspicious, 75 = comment-spammer, 100 = harvester). Normalise to
        //    0-1 so it ranks alongside the intent score.
        if (signals.TryGetValue(SignalKeys.HoneypotThreatScore, out var rawHp))
        {
            var hp = AsDouble(rawHp);
            // Some emitters use 0-1 already (DNSBL response parsing varies). Detect
            // and rescale; values >1 are the 0-100 form.
            if (hp > 1.0) hp /= 100.0;
            threatScore = Math.Max(threatScore, hp);
        }

        // 3. HaxxorContributor severity (string). xmlrpc, wp-login, .env, .git,
        //    SQL injection / XSS / SSRF payloads all surface here. Mapping the
        //    discrete severity ladder onto the same 0-1 axis: an injection-class
        //    payload at 'critical' should land on Critical band.
        if (signals.TryGetValue(SignalKeys.AttackSeverity, out var rawSeverity)
            && rawSeverity is string severity)
        {
            var attackScore = severity.ToLowerInvariant() switch
            {
                "critical" => 0.95,
                "high"     => 0.75,
                "medium"   => 0.50,
                "low"      => 0.30,
                _          => 0.0
            };
            threatScore = Math.Max(threatScore, attackScore);
        }

        // Delegate to the single source of truth -- SignatureRiskVerdictComposer.BucketThreat.
        // Until this commit the same boundaries were encoded twice (here + in the composer)
        // and the unification refactor only migrated the composer side. Future edits must
        // touch one place, not two.
        var band = Risk.SignatureRiskVerdictComposer.BucketThreat(threatScore);

        return (threatScore, band);
    }

    private static double AsDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0.0
    };

    private static double ComputeCoverageConfidence(IReadOnlySet<string> detectorsRan, bool aiRan)
    {
        var maxScore = 0.0;
        var score = 0.0;

        void Add(string name, double weight)
        {
            maxScore += weight;
            if (detectorsRan.Contains(name))
                score += weight;
        }

        Add("UserAgent", 1.0);
        Add("Ip", 0.5);
        Add("Header", 1.0);
        Add("ClientSide", 1.0);
        Add("Behavioral", 1.0);
        Add("VersionAge", 0.8);
        Add("Inconsistency", 0.8);
        Add("Heuristic", 2.0);

        // Only include AI in denominator when AI actually ran.
        // When AI is not configured/enabled, it should not penalize confidence.
        if (aiRan)
        {
            maxScore += 2.5;
            score += 2.5;
        }

        return maxScore == 0 ? 0 : score / maxScore;
    }

    /// <summary>
    /// Multi-dimensional risk band classification.
    ///
    /// Risk = max(probability_band, threat_band, persistence_band)
    ///
    /// This correctly handles:
    /// - A human manually running SQLi scans (low bot probability but high threat score = VeryHigh)
    /// - A persistent scraper with no threat indicators (high probability + many requests = VeryHigh)
    /// - A single wget request with no threat (high probability but no context = High, not VeryHigh)
    /// - An automated crawler confirmed in reputation history (confirmed bad = VeryHigh regardless)
    ///
    /// VeryHigh without AI requires one of: probability >= 0.85, OR confirmed bad actor, OR
    /// probability >= 0.70 with active threat OR >= 5 requests.
    /// </summary>
    /// <summary>
    ///     Thin wrapper that builds <see cref="Risk.SignatureRiskInputs"/> from the
    ///     pipeline-side primitives and delegates to
    ///     <see cref="Risk.SignatureRiskVerdictComposer.Compose"/>. The body that
    ///     used to live here -- a parallel implementation of friendly-pin /
    ///     dimension-max bucketing -- was the duplication the c2708102 refactor
    ///     was meant to remove; this commit completes that migration. Keeps the
    ///     YAML BotName -> friendly-type fallback that the legacy method
    ///     specifically relied on (the composer treats IsFriendlyBotType as
    ///     pre-resolved).
    /// </summary>
    internal static (RiskBand Band, string Justification, string FriendlyPinTrace) DetermineRiskBand(
        double botProbability, double confidence, bool aiRan,
        double threatScore, bool isConfirmedBad, int sessionRequestCount,
        string? intentCategory = null,
        BotType? botType = null,
        string? botName = null,
        bool? friendlyIpVerified = null,
        bool? friendlyDomainVerified = null)
    {
        var (verdict, trace) = DetermineRiskVerdict(
            botProbability, confidence, aiRan, threatScore, isConfirmedBad,
            sessionRequestCount, intentCategory, botType, botName,
            friendlyIpVerified, friendlyDomainVerified);
        return (verdict.RiskBand, verdict.Justification, trace);
    }

    /// <summary>
    ///     Same inputs and dispatch as <see cref="DetermineRiskBand"/> but returns the
    ///     full <see cref="Risk.SignatureRiskVerdict"/>. ToAggregatedEvidence needs
    ///     <see cref="Risk.SignatureRiskVerdict.HostilePinFired"/> +
    ///     <see cref="Risk.SignatureRiskVerdict.ThreatBand"/> so a confirmed-bad latch
    ///     overrides a friendly UA-derived <c>PrimaryBotType</c> and a
    ///     None-band threat score, instead of leaking the friendly identity into
    ///     the policy matcher's <c>BotType</c> + <c>ThreatBand</c> inputs.
    /// </summary>
    internal static (Risk.SignatureRiskVerdict Verdict, string FriendlyPinTrace) DetermineRiskVerdict(
        double botProbability, double confidence, bool aiRan,
        double threatScore, bool isConfirmedBad, int sessionRequestCount,
        string? intentCategory = null,
        BotType? botType = null,
        string? botName = null,
        bool? friendlyIpVerified = null,
        bool? friendlyDomainVerified = null,
        bool browserAttestationVerified = false)
    {
        // YAML catalog wins. ledger.BotType is last-writer-wins (HeuristicEarly
        // often overwrites the authoritative UA pattern's bot_type with a generic
        // Scraper guess). When the bot_name matches a YAML pattern, the catalog's
        // type is authoritative for that named identity -- the same rule applied
        // at the PrimaryBotType assignment so the risk-band and dashboard
        // surfaces stay in agreement.
        var yamlType = ParseBotType(BotPatternLoader.Default.FindBotTypeByName(botName));
        // Network-position-Internal must beat the YAML catalog. Without this,
        // the bot-patterns YAML maps "StyloBot Internal" -> Tool, so even when
        // the upstream caller passes BotType.Internal (derived from IpIsLocal,
        // an authoritative network-trust signal), yamlType=Tool would win the
        // null-coalesce and the composer's trusted-and-aligned clamp would
        // never see BotType=Internal. The UA pattern alone can be spoofed; the
        // network position cannot. Pass-through-Internal is therefore the only
        // case where the catalog's classification is overridden -- everything
        // else preserves "YAML wins" precedence.
        var resolvedBotType = botType == BotType.Internal
            ? BotType.Internal
            : (yamlType ?? botType);
        var isFriendlyBotType = BotTypeClassification.IsFriendly(botType)
                                || BotTypeClassification.IsFriendly(yamlType);

        var inputs = new Risk.SignatureRiskInputs
        {
            PrimarySignature = string.Empty, // legacy callers don't carry it here
            BotProbability = botProbability,
            Confidence = confidence,
            RawThreatScore = threatScore,
            FriendlyVerified = (friendlyIpVerified == true) || (friendlyDomainVerified == true),
            ConfirmedBad = isConfirmedBad,
            DeclaredBot = false, // declared-bot already handled upstream (probability=1.0)
            BotType = resolvedBotType?.ToString(),
            BotName = botName,
            IsFriendlyBotType = isFriendlyBotType,
            AiRan = aiRan,
            SessionRequestCount = sessionRequestCount,
            IntentCategory = intentCategory,
            FriendlyIpVerified = friendlyIpVerified,
            FriendlyDomainVerified = friendlyDomainVerified,
            BrowserAttestationVerified = browserAttestationVerified,
        };

        var verdict = Risk.SignatureRiskVerdictComposer.Compose(inputs);

        // Reconstruct the legacy FriendlyPinTrace format from the verdict's
        // reasons list so the dashboard's existing parser keeps working.
        string trace;
        if (verdict.FriendlyPinFired)
        {
            var reason = verdict.Reasons.FirstOrDefault(r => r.StartsWith("friendly_pin:", StringComparison.Ordinal))
                         ?? "friendly_pin: verified";
            trace = "fired:" + reason.Substring("friendly_pin:".Length).Trim();
        }
        else if (!isFriendlyBotType)
        {
            trace = $"not-applicable:botType={botType?.ToString() ?? "null"},yamlType={yamlType?.ToString() ?? "null"},botName={botName ?? "null"}";
        }
        else
        {
            var blockedReason = verdict.Reasons.FirstOrDefault(r => r.StartsWith("friendly_pin_blocked:", StringComparison.Ordinal));
            if (blockedReason is not null)
                trace = "skipped:" + blockedReason.Substring("friendly_pin_blocked:".Length).Trim();
            else if (verdict.HostilePinFired)
                trace = "skipped:hostile_pin_fired";
            else
                trace = $"skipped:no_corroboration (UA claims {botName ?? "friendly bot"} as {resolvedBotType?.ToString() ?? "null"})";
        }

        return (verdict, trace);
    }


    private static bool TryReadDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f:  value = f; return true;
            case int i:    value = i; return true;
            case long l:   value = l; return true;
            default:       value = 0.0; return false;
        }
    }

    private static double ExtractThreatScoreRaw(IReadOnlyDictionary<string, object> signals)
    {
        if (!signals.TryGetValue(SignalKeys.IntentThreatScore, out var rawScore)) return 0.0;
        return rawScore switch
        {
            double d => d,
            float f  => f,
            int i    => i,
            _        => 0.0
        };
    }

    private static bool IsConfirmedBad(IReadOnlyDictionary<string, object> signals, SignalSink? sink = null)
    {
        // ReputationCanAbort is written into contribution.Signals by the reputation
        // contributor, so the dict read is correct here.
        if (signals.TryGetValue(SignalKeys.ReputationCanAbort, out var canAbort) && canAbort is true)
            return true;
        // ReputationFastAbortActive is raised via sink.Raise in production; the dict
        // fallback preserves existing test callers that pass a premergedSignals dict.
        bool ReadBool(string key) =>
            sink?.ReadBoolHint(key, fallback: false)
            ?? (signals.TryGetValue(key, out var v) && v is true);
        if (ReadBool(SignalKeys.ReputationFastAbortActive))
            return true;
        return false;
    }

    private static int ExtractSessionCount(IReadOnlyDictionary<string, object> signals)
    {
        if (!signals.TryGetValue(SignalKeys.SessionRequestCount, out var raw)) return 0;
        return raw switch
        {
            int i    => i,
            long l   => (int)l,
            double d => (int)d,
            _        => 0
        };
    }

    private static DetectionContribution? FindFriendlyBotContribution(DetectionLedger ledger)
    {
        // A friendly classification is friendly regardless of which direction it pushed
        // the bot-probability needle. Examples that the old "delta > 0" gate ate:
        //   - A UA pattern match says GoodBot and contributes +0.0 (it's labelling, not
        //     scoring probability).
        //   - A reputation cache says VerifiedBot and reduces probability (-0.2) because
        //     this UA has been seen 1000 times as legitimate -- that is the strongest
        //     friendly signal there is, but a negative delta dropped it.
        // We take the friendly contribution with the largest positive delta if any are
        // positive (a confident "this IS a friendly bot" beats a labelling-only signal),
        // otherwise the first friendly contribution at all. Either way, MJ12bot / Ahrefs
        // / DuckDuckBot stop being thrown back into VeryHigh because the labelling
        // detector happened to have delta=0.
        DetectionContribution? best = null;
        foreach (var contrib in ledger.Contributions)
        {
            if (string.IsNullOrEmpty(contrib.BotType)) continue;
            if (!BotTypeClassification.IsFriendly(ParseBotType(contrib.BotType))) continue;
            if (best is null || contrib.ConfidenceDelta > best.ConfidenceDelta)
                best = contrib;
        }
        return best;
    }

    private static BotType? ParseBotType(string? botType)
    {
        if (string.IsNullOrEmpty(botType))
            return null;

        if (Enum.TryParse<BotType>(botType, true, out var result))
            return result;

        // Handle atoms library values that don't map directly to enum names
        if (botType.Equals("VerifiedGood", StringComparison.OrdinalIgnoreCase))
            return BotType.VerifiedBot;

        return null;
    }

    private static EarlyExitVerdict? ParseEarlyExitVerdict(string? verdict)
    {
        if (string.IsNullOrEmpty(verdict))
            return null;

        if (Enum.TryParse<EarlyExitVerdict>(verdict, true, out var result))
            return result;

        return null;
    }

    /// <summary>
    ///     True when the resolved name (or the contemporaneous UA-catalog signal)
    ///     maps to <see cref="BotType.Tool"/>. Used by the early-exit demotion arm to
    ///     refuse a VerifiedBadBot verdict for developer tools (curl, wget,
    ///     python-requests) whose only evidence is a ConfirmedBad IP-rep latch.
    ///     Honours catalog identity: <c>signals[UserAgentBotType]</c> first, then a
    ///     BotPatternLoader lookup of the resolved name (catches the case where the
    ///     type signal hasn't been attached but the name was composed from the UA).
    /// </summary>
    private static bool IsToolCatalog(string? resolvedName, IDictionary<string, object> signals)
    {
        if (signals.TryGetValue(SignalKeys.UserAgentBotType, out var bt)
            && bt is string s
            && string.Equals(s, nameof(BotType.Tool), StringComparison.Ordinal))
            return true;
        if (string.IsNullOrEmpty(resolvedName)) return false;
        var catalogType = BotPatternLoader.Default.FindBotTypeByName(resolvedName);
        return string.Equals(catalogType, nameof(BotType.Tool), StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when the request carries hostile evidence beyond the raw IP-rep
    ///     repeat-count: SecurityToolContributor, Honeypot, AttackSeverity, or a
    ///     threat score over the friendly-pin gate. The Tool-family demotion arm
    ///     refuses to fire when this returns true so the SecurityToolContributor
    ///     verdict path is left intact for genuinely hostile tools (sqlmap, nikto).
    /// </summary>
    private static bool HasHostileSignals(IDictionary<string, object> signals, double threatScore, SignalSink? sink = null)
    {
        if (threatScore >= 0.55) return true;
        // SecurityToolDetected is raised via sink.Raise in production; the dict
        // fallback preserves existing test callers that pass a premergedSignals dict.
        bool ReadBool(string key) =>
            sink?.ReadBoolHint(key, fallback: false)
            ?? (signals.TryGetValue(key, out var v) && v is true);
        if (ReadBool(SignalKeys.SecurityToolDetected))
            return true;
        if (signals.TryGetValue(SignalKeys.HoneypotThreatScore, out var hp))
        {
            var hpVal = hp switch
            {
                double d => d,
                float f  => f,
                int i    => (double)i,
                long l   => (double)l,
                _        => 0.0
            };
            // Honeypot writes 0-100 in the production emitters and 0-1 in some legacy
            // paths; treat anything >0 as hostile because a honeypot hit is binary
            // by definition (the bot followed a no-follow trap).
            if (hpVal > 0) return true;
        }
        if (signals.TryGetValue(SignalKeys.AttackSeverity, out var sev) && sev is string severity)
        {
            var sevLower = severity.ToLowerInvariant();
            if (sevLower is "critical" or "high" or "medium") return true;
        }
        return false;
    }
}