using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
/// Extensions to build BotDetection's AggregatedEvidence from taxonomy's DetectionLedger.
/// </summary>
public static class DetectionLedgerExtensions
{
    /// <summary>
    /// Builds an AggregatedEvidence from the detection ledger.
    /// </summary>
    public static AggregatedEvidence ToAggregatedEvidence(
        this DetectionLedger ledger,
        string? policyName = null,
        PolicyAction? policyAction = null,
        string? actionPolicyName = null,
        bool aiRan = false,
        IReadOnlyDictionary<string, object>? premergedSignals = null,
        BotDetectionOptions? options = null)
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

        var earlyThreatForBand = ExtractThreatScoreRaw(preSignals);
        var isConfirmedBadForBand = IsConfirmedBad(preSignals);
        var sessionCountForBand = ExtractSessionCount(preSignals);
        var intentCategory = preSignals.TryGetValue(SignalKeys.IntentCategory, out var ic) ? ic as string : null;
        // Vendor-IP verification (Commercial-side contributor). bool? semantics:
        //   true  = friendly UA + IP matches vendor range -> proceed to pin
        //   false = friendly UA + IP did NOT match -> skip pin (spoofed UA)
        //   null  = no check attempted -> fall back to UA-only friendly logic
        var friendlyIpVerified = preSignals.TryGetValue(SignalKeys.FriendlyIpVerified, out var fipv)
            ? (bool?)Convert.ToBoolean(fipv)
            : null;
        // Find any contributor that classified this UA as a friendly bot type. The ledger's
        // single BotType property is last-writer-wins (HeuristicEarly often overwrites the
        // UA's authoritative pattern match with a generic "Scraper" guess). Scanning all
        // contributions surfaces the strongest identification.
        var friendlyBotType = FindFriendlyBotType(ledger);
        var ledgerBotType = friendlyBotType ?? ParseBotType(ledger.BotType);

        var (riskBand, riskJustification, friendlyPinTrace) = DetermineRiskBand(botProbability, confidence, aiRan,
            earlyThreatForBand, isConfirmedBadForBand, sessionCountForBand, intentCategory,
            ledgerBotType, ledger.BotName, friendlyIpVerified);

        // PrimaryBotType stays gated on classification — it's a claim about WHAT KIND of bot
        // ("this looks like a Scraper") and is only meaningful when classified as bot.
        var isActuallyBot = botProbability >= 0.5;
        var primaryBotType = isActuallyBot ? ParseBotType(ledger.BotType) : null;

        // PrimaryBotName is NEVER gated — every fingerprint always has a name (verdict label
        // and name are separate concerns). Prefer the matcher-set identity.display_name
        // (persisted, drift-gated) over the ledger's UA-derived name.
        var primaryBotName = ResolveDisplayName(preSignals, isActuallyBot ? ledger.BotName : null);

        // Handle early exit
        if (ledger.EarlyExit && ledger.EarlyExitContribution != null)
        {
            return CreateEarlyExitResult(ledger, aiRan, policyName, premergedSignals);
        }

        var signals = premergedSignals != null
            ? new Dictionary<string, object>(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var (threatScore, threatBand) = ExtractThreatScore(signals);

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
        IReadOnlyDictionary<string, object>? premergedSignals = null)
    {
        var exitContrib = ledger.EarlyExitContribution!;
        var verdict = ParseEarlyExitVerdict(exitContrib.EarlyExitVerdict);
        var isBot = verdict is EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.VerifiedBadBot;

        var earlySignals = premergedSignals != null
            ? new Dictionary<string, object>(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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
        var primaryBotType = ParseBotType(exitContrib.BotType);
        var primaryBotName = ResolveDisplayName(earlySignals, exitContrib.BotName);
        if (verdict is EarlyExitVerdict.VerifiedBadBot)
        {
            var friendlyContrib = FindFriendlyBotContribution(ledger);
            var isConfirmedBadEarly = IsConfirmedBad(earlySignals);
            if (friendlyContrib != null && !isConfirmedBadEarly && earlyThreatScore < 0.55)
            {
                var friendlyType = ParseBotType(friendlyContrib.BotType);
                earlyRiskBand = RiskBand.Low;
                earlyRiskJustification = $"identified as {friendlyType} (friendly automation; reputation-cache override)";
                primaryBotType = friendlyType;
                // The friendly-contributor BotName only overrides when the matcher's
                // display name signal wasn't present, so a matched archetype name wins.
                if (!string.IsNullOrEmpty(friendlyContrib.BotName)
                    && !earlySignals.ContainsKey(SignalKeys.IdentityDisplayName))
                    primaryBotName = friendlyContrib.BotName;
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
    ///     Resolves the display name for this visitor: the matcher-set signal first, then the
    ///     ledger's classifier-supplied <c>BotName</c>. Returns null when neither is present;
    ///     the dashboard's render layer (SbTopBots.DescriptiveBotName etc.) synthesises a
    ///     descriptive label from threat / behaviour signals on the row in that case.
    ///     Previously this also called FingerprintNameComposer.Compose as a third fallback,
    ///     which is now redundant -- the matcher already calls Compose at allocation time
    ///     and only writes the signal when a real name was derivable.
    /// </summary>
    private static string? ResolveDisplayName(
        IReadOnlyDictionary<string, object> signals, string? fallback)
    {
        var fromSignal = signals.TryGetValue(SignalKeys.IdentityDisplayName, out var v)
            ? v as string : null;
        if (!string.IsNullOrEmpty(fromSignal)) return fromSignal;
        return string.IsNullOrEmpty(fallback) ? null : fallback;
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

        var band = threatScore switch
        {
            >= 0.80 => ThreatBand.Critical,
            >= 0.55 => ThreatBand.High,
            >= 0.35 => ThreatBand.Elevated,
            >= 0.15 => ThreatBand.Low,
            _ => ThreatBand.None
        };

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
    internal static (RiskBand Band, string Justification, string FriendlyPinTrace) DetermineRiskBand(
        double botProbability, double confidence, bool aiRan,
        double threatScore, bool isConfirmedBad, int sessionRequestCount,
        string? intentCategory = null,
        BotType? botType = null,
        string? botName = null,
        bool? friendlyIpVerified = null)
    {
        // Friendly bot types (search engines, fediverse link previewers, monitoring,
        // explicitly-verified good bots) get pinned to Low even when probability is
        // near the AI-clamp ceiling. Threat / confirmed-bad still escalate below.
        // Two ways into this branch:
        //   1. botType is in the friendly set (BotTypeClassification.IsFriendly).
        //   2. botType propagation failed upstream and we got "Unknown" / null, but
        //      botName matches a YAML pattern whose bot_type IS friendly. The YAML
        //      pattern files are the single source of truth here -- without this
        //      fallback a wiring bug surfaces as a VeryHigh-risk DuckDuckBot row.
        //
        // The FriendlyPinTrace return value records the decision either way so the
        // dashboard can show "this would have pinned to Low but the threat score
        // bypassed the gate" rather than the operator having to guess why a known
        // SEO crawler (MJ12bot, AhrefsBot) ended up VeryHigh. Format is structured
        // so it can be parsed: "fired:<source>" or "skipped:<reason>" or
        // "not-applicable:<reason>".
        var yamlType = ParseBotType(BotPatternLoader.Default.FindBotTypeByName(botName));
        var ledgerFriendly = BotTypeClassification.IsFriendly(botType);
        var yamlFriendly = BotTypeClassification.IsFriendly(yamlType);
        var hasFriendlyCandidate = ledgerFriendly || yamlFriendly;
        // Threat-score gate applies universally: a UA pretending to be Googlebot while
        // probing .env files still gets the standard treatment.
        //
        // isConfirmedBad gate is DROPPED when the YAML pattern authoritatively says this
        // is a friendly bot. The YAML catalog is curated and is more authoritative than
        // the reputation cache, which routinely flags benign high-volume crawlers
        // (MJ12bot, AhrefsBot, SemrushBot) as "can_abort" purely because of request
        // volume -- volume is a property of crawlers, not of malicious actors. The
        // reputation cache still wins when we only have weak friendly evidence
        // (a contributor labelled it friendly without a matching YAML entry).
        string friendlyTrace;
        if (!hasFriendlyCandidate)
        {
            friendlyTrace = $"not-applicable:botType={botType?.ToString() ?? "null"},yamlType={yamlType?.ToString() ?? "null"},botName={botName ?? "null"}";
        }
        else if (threatScore >= BotTypeClassification.FriendlyThreatGate)
        {
            friendlyTrace = $"skipped:threatScore={threatScore:F2} >= gate({BotTypeClassification.FriendlyThreatGate:F2}) (had friendly candidate {(ledgerFriendly ? botType : yamlType)})";
        }
        else if (isConfirmedBad && !yamlFriendly)
        {
            // Only weak (ledger-only) friendly evidence and the reputation cache says
            // bad: trust the reputation cache.
            friendlyTrace = $"skipped:isConfirmedBad (weak friendly via ledger only: {botType})";
        }
        else if (friendlyIpVerified == false)
        {
            // UA looks friendly but vendor-IP check failed. Treat as spoofed UA --
            // probability + threat dimensions decide the band below as if the UA
            // had never matched a friendly pattern.
            friendlyTrace = $"skipped:ip_not_verified (UA claims {botName ?? "friendly bot"} as {(ledgerFriendly ? botType : yamlType)} but client IP not in vendor range)";
        }
        else if (ledgerFriendly)
        {
            string kind;
            if (friendlyIpVerified == true) kind = "ledger+ip";
            else if (isConfirmedBad)        kind = "ledger+yaml-overrides-reputation";
            else                            kind = "ledger";
            return (RiskBand.Low,
                $"identified as {botType} (friendly automation)",
                $"fired:{kind}:{botType}");
        }
        else // yamlFriendly == true (proven by hasFriendlyCandidate && !ledgerFriendly)
        {
            string kind;
            if (friendlyIpVerified == true) kind = "yaml+ip";
            else if (isConfirmedBad)        kind = "yaml-overrides-reputation";
            else                            kind = "yaml";
            return (RiskBand.Low,
                $"identified as {botName} (friendly automation; yaml bot_type {yamlType})",
                $"fired:{kind}:{botName}:{yamlType}");
        }

        // Low confidence: not enough data to assess reliably
        if (confidence < 0.3)
            return botProbability >= 0.5
                ? (RiskBand.Medium, $"Low detection confidence ({confidence:F2}); probability {botProbability:F2}", friendlyTrace)
                : (RiskBand.Unknown, "Insufficient data for reliable risk assessment", friendlyTrace);

        var reasons = new List<string>(4);

        // Dimension 1: bot probability band
        RiskBand probabilityBand;
        if (aiRan)
        {
            probabilityBand = botProbability switch
            {
                >= 0.80 => RiskBand.VeryHigh,
                >= 0.50 => RiskBand.High,
                >= 0.20 => RiskBand.Medium,
                >= 0.05 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons.Add($"AI probability {botProbability:F2}");
        }
        else
        {
            // Without AI, require stronger evidence for VeryHigh:
            // pure probability alone can reach VeryHigh at 0.85 (matching the middleware threshold).
            // Below that, persistence or threat must be present to escalate further.
            probabilityBand = botProbability switch
            {
                >= 0.85 => RiskBand.VeryHigh,
                >= 0.65 => RiskBand.High,
                >= 0.50 => RiskBand.Medium,
                >= 0.35 => RiskBand.Elevated,
                >= 0.15 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons.Add($"probability {botProbability:F2}");
        }

        // Dimension 2: threat score (independent of automation - a human can attack too)
        var threatBandRisk = threatScore switch
        {
            >= 0.80 => RiskBand.VeryHigh,
            >= 0.55 => RiskBand.High,
            >= 0.35 => RiskBand.Medium,
            >= 0.15 => RiskBand.Elevated,
            _       => RiskBand.VeryLow
        };
        if (threatBandRisk >= RiskBand.Medium)
        {
            var threatLabel = !string.IsNullOrEmpty(intentCategory) && intentCategory != "browsing"
                ? $"{intentCategory} activity (threat={threatScore:F2})"
                : $"threat score {threatScore:F2}";
            reasons.Add(threatLabel);
        }

        // Dimension 3: persistence (repeated confirmed behavior adds weight regardless of bot probability)
        var persistenceBand = RiskBand.VeryLow;
        if (isConfirmedBad)
        {
            persistenceBand = RiskBand.VeryHigh;
            reasons.Add("confirmed bad actor");
        }
        else if (botProbability >= 0.70 && sessionRequestCount >= 5)
        {
            // Persistent suspected bot: multiple requests + elevated probability = escalate to VeryHigh
            persistenceBand = RiskBand.VeryHigh;
            reasons.Add($"{sessionRequestCount} requests");
        }
        else if (sessionRequestCount >= 20)
        {
            persistenceBand = RiskBand.High;
            reasons.Add($"{sessionRequestCount} requests");
        }
        else if (sessionRequestCount >= 10)
        {
            persistenceBand = RiskBand.Medium;
            reasons.Add($"{sessionRequestCount} requests");
        }

        // Final band = max across all three dimensions
        var finalBand = (RiskBand)new[] { (int)probabilityBand, (int)threatBandRisk, (int)persistenceBand }.Max();

        if (reasons.Count == 0)
        {
            var lowLabel = finalBand <= RiskBand.Low ? "No significant indicators" : $"probability {botProbability:F2}";
            return (finalBand, lowLabel, friendlyTrace);
        }

        return (finalBand, string.Join("; ", reasons), friendlyTrace);
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

    private static bool IsConfirmedBad(IReadOnlyDictionary<string, object> signals)
    {
        if (signals.TryGetValue(SignalKeys.ReputationCanAbort, out var canAbort) && canAbort is true)
            return true;
        if (signals.TryGetValue(SignalKeys.ReputationFastAbortActive, out var abortActive) && abortActive is true)
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

    private static BotType? FindFriendlyBotType(DetectionLedger ledger)
        => FindFriendlyBotContribution(ledger) is { } c ? ParseBotType(c.BotType) : null;

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
}