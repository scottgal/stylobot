# Bot-vs-Human Classification: Data-Source Rationalisation

Status: **Draft spec — for review before implementation**
Owner: dashboard + detection
Date: 2026-07-01

## 1. Problem

"Is this traffic a bot or a human?" is computed **more than 30 different ways** across the
gateway and dashboard, from **different stored fields**, with **different thresholds**. They
disagree. On a security product that is not a cosmetic bug — a panel that reads *"100% bot /
VeryHigh"* next to its own visitors at *"1–2%"* destroys operator trust.

### Evidence (live, 2026-07-01)
`/stylobot/hub` endpoint panel:
- `avg(bot_probability) = 0.025` (human), "MOST REGULAR VISITORS" = Chrome @ 1% and 2% (human)
- yet the headline reads **"100% bot rate · 836 bots · 0 humans · RISK PROFILE: VeryHigh"**

Because `is_bot = true` for 895/902 hub requests **at prob 0.025**, and the endpoint widget
counts `is_bot`, not `bot_probability`.

### Root causes (from the write/read audit)
1. **`is_bot` is not a function of `bot_probability`.** Three divergent write-side gates:
   - `DetectionBroadcastMiddleware.cs:484` — `IsBot = BotProbability > 0.5 || hasAuthoritativeBotType`
   - `BotDetectionMiddleware.cs:676` — `cachedIsBot = hasMeaningfulBotType || BotProbability > 0.5`
   - `DetectionLedgerExtensions.cs:227` — `isActuallyBot = botProbability >= 0.5 || catalogBotType is not null`
   The `|| <BotType present>` clause makes `is_bot` true at prob 0.025.
2. **`BotType` (a descriptive label) drives the bot/human decision** in ~6 places
   (Internal / MaliciousBot / catalog-match force `is_bot`/risk). A label must never override
   the number.
3. **Read-side reads the wrong field.** 31+ aggregations count raw `is_bot` (or read `risk_band`
   directly); only 7 derive from `bot_probability`. Endpoint / country / user-agent bot-rates,
   request-level summary counts, visitor filters, and risk-band histograms are all in the
   divergent set.
4. **Threshold drift.** Binary uses `0.5`; traffic buckets use `0.3 / 0.8`; block uses
   `BotThreshold = 0.7`. No single scheme.

## 2. Principle

**`bot_probability` (+ `confidence`) is the ONLY source of truth. Every other bot/human field
— `is_bot`, `risk_band`, bot%, bot counts, buckets — is a pure, deterministic derivation of it
through ONE classifier. Nothing computes its own answer. `bot_type` is a descriptive label,
never a driver of the bot/human decision.**

This is the same fix already applied to the fingerprint `cached_risk_band` (derive from the
blended probability via one composer); we extend it to the whole surface.

## 3. The canonical classifier

Single home: `Mostlylucid.BotDetection.Risk.SignatureRiskVerdictComposer` (already the risk-band
authority). Add one pure function, used everywhere in C# and mirrored in SQL:

```csharp
public enum TrafficClass { Human, Uncertain, Bot }

// Pure function of the ONLY truth. No BotType, no reputation, no honeypot input.
public static TrafficClass Classify(double botProbability, ClassificationThresholds t)
    => botProbability >= t.BotFloor     ? TrafficClass.Bot
     : botProbability <  t.HumanCeiling ? TrafficClass.Human
     :                                    TrafficClass.Uncertain;

public static bool IsBot(double p, ClassificationThresholds t) => Classify(p, t) == TrafficClass.Bot;
```

- `RiskBand` stays derived via the existing `BucketRisk(prob, confidence)` — same input, same
  authority. Never read a stored `risk_band` that could disagree; derive it.
- **`is_bot` binary** = `Classify(...) == Bot`. The `Uncertain` band is what the summary's
  "uncertain" column already wants — one scheme, three classes, no separate 0.3/0.8 vs 0.5.

## 4. Configurable settings (`BotDetection:Classification`)

Per house rule (all thresholds on an Options class):

| Setting | Default | Meaning |
|---|---|---|
| `HumanCeiling` | `0.30` | `prob < HumanCeiling` ⇒ Human |
| `BotFloor` | `0.70` | `prob >= BotFloor` ⇒ Bot (aligns with existing `BotThreshold`) |
| (between) | — | Uncertain |

One scheme replaces the scattered `0.5` / `0.3` / `0.8` / `0.7` literals. `BotFloor` is unified
with the action `BotThreshold` so "counted as a bot" and "acted on as a bot" agree.

## 5. Write-side rationalisation (sever label→decision coupling)

`is_bot` must become a pure function of `bot_probability`; `BotType` becomes label-only.

| File:Line | Change |
|---|---|
| `DetectionBroadcastMiddleware.cs:484` | `IsBot = Composer.IsBot(BotProbability, thresholds)` — drop `|| hasAuthoritativeBotType` |
| `BotDetectionMiddleware.cs:676` | `cachedIsBot = Composer.IsBot(v.BotProbability, thresholds)` — drop `hasMeaningfulBotType ||` |
| `DetectionLedgerExtensions.cs:227` | `isActuallyBot = Composer.IsBot(botProbability, thresholds)` — drop `|| catalogBotType is not null` |
| `DetectionLedgerExtensions.cs:246-250` | `BotType` assignment stays (it IS the label), but must NOT feed back into `isActuallyBot`. Internal/MaliciousBot remain as labels only. |
| `DetectionLedgerExtensions.cs:382-389` | Early-exit verdicts (VerifiedGoodBot/BadBot, Whitelisted/Blacklisted): these set the **probability** (0.0 / 1.0) explicitly, then `is_bot` derives from it — do not set `is_bot` independently. |
| `SignatureRiskVerdictComposer.cs:156-171` | Friendly/hostile pins already influence `bot_probability`/band through the composer; keep the pin → band path, but the pin must express itself as probability/band, not as a raw `is_bot` override downstream. |

Note: if a detector is confident something is a bot, it must say so **by raising
`bot_probability`**, not by stamping a `BotType` and forcing `is_bot`. This keeps one dial.

## 6. Read-side rationalisation (all aggregates derive)

Replace every raw `is_bot` / `risk_band` read with a derivation from `bot_probability` at the
threshold. SQL is trivial: `COUNT(*) FILTER (WHERE bot_probability >= @botFloor)`.

Both stores (`SqliteDashboardEventStore`, `PostgreSQLDashboardEventStore`):

| Metric | Current (divergent) | Target |
|---|---|---|
| Summary `BotRequests` | `COUNT(is_bot)` (`:502` / `:522`) | `COUNT(*) FILTER (WHERE bot_probability >= @botFloor)` |
| Summary `HumanRequests` | `total - bots` | `COUNT(*) FILTER (WHERE bot_probability < @humanCeiling)` |
| Summary `UncertainRequests` | `NOT is_bot AND confidence<0.5` | `bot_probability` in `[HumanCeiling, BotFloor)` |
| `RiskBandCounts` | `GROUP BY risk_band` | derive band from `bot_probability, confidence` (or ensure stored band already consistent per §7) |
| Endpoint `BotRate/BotCount/HumanCount` | `is_bot` (`Sqlite:1031`, `Pg:973`) | probability threshold |
| Country `BotRate/BotCount` | `is_bot` (`Sqlite:915/945/982`, `Pg:790/811`) | probability threshold |
| Top-bots `IsKnownBot` | latest `is_bot` (`Sqlite:792`, `Pg:695`) | `bot_probability >= @botFloor` |
| Visitor filters/counts | `.IsBot` (`WidgetRenderHelpers.cs:401-416`) | `Composer.IsBot(v.BotProbability)` |
| By-bot-type roll | `.IsBot` (`TrafficController.cs:399`) | `Composer.IsBot(v.BotProbability)` |
| User-agent bot count/rate | `.IsBot` (`StyloBotDashboardMiddleware.cs:2710/2724`) | probability threshold |
| YOU pill RiskBand | reads `risk_band` (`:6925`) | derive from headline probability |
| Traffic counters/timeseries/families | already `bot_probability` but at `0.3/0.8` (`TrafficController.cs:216/257/278`) | use the SAME `HumanCeiling/BotFloor` |

Preferred implementation: a single `IsBot`/`Classify` helper the C# readers call, and a shared
SQL fragment (`bot_probability >= @botFloor`) the two stores paste — so there is literally one
threshold constant flowing to every site.

## 7. `is_bot` / `risk_band` at rest

Two acceptable strategies (pick one in review):
- **(A) Derive-at-read only.** Aggregations never read stored `is_bot`/`risk_band`; they always
  compute from `bot_probability`. Stored `is_bot`/`risk_band` become vestigial (or dropped).
  Cleanest single-source guarantee; matches "compute at read".
- **(B) Store consistently + read consistently.** Fix the write-side (§5) so stored `is_bot` is
  already `Classify(prob)`, and readers still derive. Belt-and-suspenders; needs a one-time
  backfill of historical rows (`UPDATE ... SET is_bot = (bot_probability >= botFloor)`).

Recommendation: **(A)** for aggregations (no backfill needed, no drift possible) + do §5 anyway
so any consumer of the stored boolean is also correct.

## 8. Tests & guards (stop the regression)

1. **Consistency unit test** on the composer: for a grid of (prob, confidence),
   `IsBot`, `Classify`, and `BucketRisk` never contradict (no "Bot + VeryLow", no "Human + VeryHigh").
2. **Static guard** (like `DashboardLinkIntegrityTests`): fail CI if a dashboard read path or an
   event-store aggregation SQL references a raw `is_bot` column for a bot/human COUNT/RATE.
   Allow only the shared `bot_probability >= @botFloor` fragment / the `Composer.IsBot` helper.
3. **Cross-surface invariant test**: seed detections, then assert endpoint bot-rate == the
   probability-derived rate for the same rows == what the visitor list would classify. One dataset,
   all surfaces agree.

## 9. Rollout

1. Land the classifier + thresholds + write-side (§3–§5), FOSS + commercial.
2. Reroute readers (§6), both stores.
3. Add guards (§8).
4. Strategy (A): no data migration. Strategy (B): backfill `is_bot`.
5. Deploy to **staging.stylo.bot** first (prod cluster is being rebuilt), verify the
   `/stylobot/hub` panel reads human, then promote.

## 10. Non-goals (explicit — not silently cut)

- Not changing how `bot_probability` itself is computed (the detectors/EWMA). Only how the ONE
  number fans out to every derived field. (The separate reduced-header/WS-mode scoring — task #35 —
  is where the *probability* for protocol requests gets fixed; this spec makes sure that once the
  probability is right, every surface agrees.)
- Not removing `bot_type` — it stays as a label, just decoupled from the decision.