# Declared-bot scoring (6.8.7+)

## Why this exists

Before 6.8.7, a self-declaring bot UA (Googlebot, Mastodon, MJ12bot, generic `python-requests/2.31.0`, etc.) went through the same sigmoid + clamp + coverage-throttled pipeline as ambiguous traffic. The dashboard showed clean known bots as **"~70% bot at ~0.4 confidence"**: lukewarm, hedged, and wrong.

The framing error is in the question being asked. For a UA that literally says `Googlebot/2.1`, "is this a bot?" is not the question -- **nobody pretends to be a bot.** The bot/human verdict is *categorical*, not probabilistic. The only open question is whether the identity *claim* is genuine -- which is a different axis.

## The two axes

| Axis | Meaning | Source of truth |
|------|---------|-----------------|
| `BotProbability` | Is this a bot? | Categorical when UA declares (1.0); sigmoid rollup otherwise |
| `Confidence` | Do I trust the identity claim? | Whether verification has run (`friendly.ip_verified`, `friendly.domain_verified`, `verifiedbot.checked`) |

For everything that is *not* a declared bot, both axes work exactly as they did before -- weighted sigmoid for probability, agreement × coverage × detector-count for confidence.

## The declared-bot override

`DetectionLedgerExtensions.ToAggregatedEvidence` checks `SignalKeys.UserAgentIsBot` after the normal aggregation and before the `RiskBand` decision. When the signal is `true`:

```
botProbability = 1.0
confidence     = anyVerificationAttempted ? 1.0 : 0.5
```

`anyVerificationAttempted` is true when *any* of these signals are present:

- `SignalKeys.FriendlyIpVerified` -- vendor-IP range check ran (Commercial)
- `SignalKeys.FriendlyDomainVerified` -- NodeInfo / fediverse-domain lookup ran (FOSS)
- `SignalKeys.VerifiedBotChecked` -- `VerifiedBotAtom` ran reverse-DNS or honest-bot resolution

**A failed verification is still high confidence.** Detecting a spoofer (UA claims Googlebot, IP says no) is a confident identity judgement -- in the negative. The dashboard should *not* display "0.5 confidence" on a confirmed spoofer; the operator's read on the row is "we know what this is and it's lying."

## What the override does NOT touch

- **Verified-good early-exit.** `VerifiedBotAtom` short-circuits with `DetectionContribution.VerifiedGoodBot` for IP/FCrDNS-confirmed Googlebot, Bingbot, etc. `CreateEarlyExitResult` already returns `BotProbability = 1.0, Confidence = 1.0` for that path -- the override never fires.
- **Friendly-pin RiskBand.** `DetermineRiskBand`'s friendly-bot path (verified Mastodon, MJ12bot, DuckDuckBot, etc.) still pins `RiskBand.Low` based on classification + corroboration. The override changes the displayed probability/confidence numbers; it doesn't change the response routing.
- **The ledger itself.** `DetectionLedger.Aggregate` is shared with other consumers (RetrievalCore, ResponseDetectionOrchestrator). The override is applied at the read-overlay layer in `DetectionLedgerExtensions`, not at the rollup.
- **Humans.** `UserAgentIsBot == false` (the explicit human path) does not trip the override; probability and confidence follow the existing sigmoid + coverage logic.

## Dashboard read

The signature dashboard surfaces `BotProbability` and `Confidence` from `AggregatedEvidence`. After 6.8.7 a Mastodon row with no `friendly.*` wiring reads:

| Field | Pre-6.8.7 | Post-6.8.7 |
|-------|-----------|------------|
| Bot Probability | ~0.7 (clamped to 0.90 ceiling) | **1.0** |
| Confidence | ~0.4 (coverage-throttled) | **0.5** |
| RiskBand | Medium / High | Unchanged (friendly-pin still applies when verified) |

With NodeInfo wiring fired (`friendly.domain_verified = true`):

| Field | Value |
|-------|-------|
| Bot Probability | **1.0** |
| Confidence | **1.0** |
| RiskBand | **Low** (friendly automation, domain verified) |

With a spoofer (`verifiedbot.checked = true`, `verifiedbot.spoofed = true`):

| Field | Value |
|-------|-------|
| Bot Probability | **1.0** |
| Confidence | **1.0** (we are certain it is a spoofer) |
| RiskBand | High / VeryHigh (driven by `StrongBotContribution` from `VerifiedBotAtom` + threat signals) |

## Tests pinning this behaviour

`Mostlylucid.BotDetection.Test/Orchestration/DefaultPolicyAndCoverageTests`:

- `DeclaredBot_WithoutVerification_PinsProbabilityToOne_AndConfidenceToHalf`
- `DeclaredBot_WithDomainVerification_PinsBothToOne`
- `DeclaredBot_WithFailedVerification_KeepsConfidenceHigh`
- `Human_NoOverride_FollowsExistingRollup`

If you change the override semantics, update these four tests in lockstep.
