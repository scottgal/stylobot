# Rate-Limit Core — design

**Date:** 2026-05-25
**Status:** spec, awaiting plan

## Goal

Real throttling. Capacity limits, not per-request `Task.Delay`. An operator says "Scraper bots get 60 req/min, 100 KB/s outbound, scoped to `/api/*`", and the gateway enforces both the request rate and the response data rate at runtime. Limits apply at any scope from global down to a single endpoint+method, and inherit through the tree unless overridden. Subjects of a limit are the things an operator already reasons about on the dashboard: fingerprint, bot type, bot name, geo, customer-pinned identity, subnet.

The detection-policy grammar (separate spec) is the *selector* — "which rule fires for this request". This spec is the *enforcer* — "what does the rule do once it fires" — when that action is rate-limiting.

## What exists today

`Mostlylucid.BotDetection/Actions/`:

- `throttle-stealth` — silent `Task.Delay`
- `throttle-tools` — HTTP 429 with `Retry-After` + exponential backoff inside a single request
- `throttle-status` — fast 429 with fixed `Retry-After: 60`

All three are per-request delays or fixed-response actions. None of them maintain a budget. There is no token bucket, no leaky bucket, no carry-over between requests, no concept of "this client has used N of M tokens." Bots that obey `Retry-After` voluntarily back off; bots that don't keep getting delayed forever. The dashboard cannot show "X has 12 of 60 tokens remaining" because there is no bucket.

The policy-grammar-core-experience plan (`2026-05-24-policy-grammar-core-experience.md`) introduces `RateLimitActionPolicy` as a token bucket keyed on `PrimarySignature`. That plan covers the request-rate primitive but only at fingerprint-scope, and does not address data-rate limiting, subject composition, or scope inheritance.

## Design

### 1. Two primitives

```csharp
public interface IRateLimiter
{
    // Returns true if a token was available; false if over budget.
    // Caller dispatches the OverLimitAction when this returns false.
    bool TryAcquire(RateLimitSubject subject, int tokens = 1);

    // Diagnostic snapshot for the dashboard.
    RateLimitState GetState(RateLimitSubject subject);
}

public interface IDataRateLimiter
{
    // Wraps the response body Stream. Writes drain a leaky-bucket and
    // block-await when the bucket is full -- effective bandwidth cap.
    Stream WrapResponseStream(RateLimitSubject subject, Stream inner, long bytesPerSecond);
}
```

**Request rate** is a classic token bucket per `(subject, scope)` pair: capacity = `BurstSize`, refill at `RequestsPerMinute / 60` per second. `TryAcquire` is non-blocking — the over-limit caller routes to the rule's `OverLimitAction` (typically `throttle-status` or `block-soft`).

**Data rate** is a leaky-bucket wrapping `HttpContext.Response.Body`. Writes that would exceed the per-second budget block-await the next refill slot. The client sees their effective download bandwidth capped at `BytesPerSecond`. This is the actual "throttle" the operator wants — the bot can keep its connection open but cannot drain content any faster than the limit.

Both primitives are subject-keyed via `RateLimitSubject` (next section). State is process-wide via a `ConcurrentDictionary<RateLimitSubject, Bucket>` in FOSS. Commercial swaps in a Redis-backed `IRateLimitStateStore` so multi-gateway clusters share the budget. The same in-memory default applies to FOSS that the existing `IClusterBackplane` pattern uses for other shared state.

### 2. Subjects — what gets rate-limited

A subject is one or more typed predicates that key a bucket. A single rule can name multiple subjects; the request consumes from *each* matching bucket and is over-limit if *any* bucket is empty. AND semantics across subjects.

| Subject kind | Source | Example |
|---|---|---|
| `Fingerprint` | `detection.PrimarySignature` | per-actor cap |
| `BotType` | `detection.BotType` | "Scraper" -- whole-class cap |
| `BotName` | `detection.BotName` | "GPTBot", "Googlebot" -- vendor-specific |
| `Country` | `detection.CountryCode` (ISO-2) | "RU", "CN" |
| `Region` | derived from `CountryCode` | "EU", "APAC" -- mapping in config |
| `PinnedLabel` | operator-set `CustomBotName` | "abusive-aggregator-X" |
| `IpSubnet` | `/24` of remote IP (HMAC-hashed) | fallback when fingerprint rotates |

YAML form:

```yaml
RateLimits:
  scraper-class:
    Subjects:
      - Type: BotType
        Value: Scraper
    RequestRate:
      RequestsPerMinute: 60
      BurstSize:         10
    DataRate:
      BytesPerSecond:    102400        # 100 KB/s
    OverLimitAction:     throttle-status

  ru-region:
    Subjects:
      - Type: Country
        Values: [RU, BY]
    RequestRate:
      RequestsPerMinute: 30
    OverLimitAction:     block-soft

  vip-allowlist:
    Subjects:
      - Type: PinnedLabel
        Values: [partner-feed, prod-monitoring]
    RequestRate:
      RequestsPerMinute: 1000          # generous cap, mostly observe
    OverLimitAction:     throttle-status

  composite-aggressive-AI:
    # A request that's BOTH AI-typed AND from US is more dangerous than
    # either alone -- both buckets must have tokens to pass.
    Subjects:
      - Type: BotType
        Value: AI
      - Type: Country
        Value: US
    RequestRate:
      RequestsPerMinute: 10
    OverLimitAction:     block-soft
```

The composite case (`scraper AND US`) intersects: a US scraper consumes from this dedicated bucket *in addition to* any single-subject buckets it also matches. Over-limit on any of those routes to `OverLimitAction`.

### 3. Scope and inheritance

```
Global              (FOSS + commercial)
  └─ Domain         (commercial)
       └─ Subdomain (commercial)
            └─ Endpoint                  (FOSS + commercial)
                 └─ Method               (FOSS + commercial)
```

Each scope is a config block that can declare its own `RateLimits` map and either `inherit: true` (default — also use parent's limits) or `inherit: false` (this scope's limits replace the parent's entirely for the matching subject). Resolution is nearest-defined-wins per `(subject, scope-key)`: a `Method: GET` block override on `/api/users` shadows the same subject's limit at `Endpoint: /api/users`, which itself shadows the `Subdomain: api.example.com` block, and so on.

YAML form:

```yaml
BotDetection:
  RateLimits:                                 # global scope
    Inherit: false                            # root: nothing to inherit
    Limits:
      scraper-class: { ... as above ... }
      ru-region:     { ... }

  Domains:                                    # commercial-only block
    example.com:
      Inherit: true
      Limits:
        # Tighter scraper cap for this domain than the global default
        scraper-class:
          RequestRate: { RequestsPerMinute: 30 }
      Subdomains:
        api.example.com:
          Inherit: true
          Limits:
            # api subdomain caps AI bots harder, and adds a per-fingerprint cap
            ai-class:
              Subjects: [{ Type: BotType, Value: AI }]
              RequestRate: { RequestsPerMinute: 5 }
              OverLimitAction: block-soft
            fingerprint-burst:
              Subjects: [{ Type: Fingerprint }]
              RequestRate: { RequestsPerMinute: 120, BurstSize: 20 }
              OverLimitAction: throttle-status

          Endpoints:                          # FOSS + commercial
            "/api/search":
              Inherit: true
              Methods:                        # FOSS + commercial
                GET:
                  Inherit: true
                  Limits:
                    # /api/search GET specifically caps AI to 1 req/min
                    ai-class:
                      RequestRate: { RequestsPerMinute: 1 }
                      OverLimitAction: block-soft
```

FOSS supports `Endpoint` + `Method` scopes only. The `Domains` / `Subdomains` blocks are licensed surfaces — operators on FOSS get a config-load error if they declare them (gated through the existing `IStyloBotLicenseGate` / `ILicenseManager.RequireFeatureAsync` path used for other commercial config).

A request resolves its applicable limit set by walking *up* the tree from method to global, collecting limits-by-subject, and applying `Inherit: false` to truncate the walk. A limit defined at multiple levels for the same subject collapses to the nearest one ("Method overrides Endpoint overrides Subdomain ..."). Composite subjects (BotType AND Country) are keyed by the full predicate set, so two scopes with non-identical predicates produce distinct buckets.

### 4. Customer-pinned fingerprints

Operators label actors via an existing surface (the `CustomBotName` field already on `DashboardTopBotEntry`, written through the signature-labels API). A label becomes a `PinnedLabel` subject in rate-limit rules. Workflow:

1. Operator sees a row on the dashboard, recognises it as their "partner-aggregator-feed", clicks Label → "partner-feed".
2. Label is persisted in `dashboard_signature_labels` (existing table).
3. Rate-limit rules referencing `PinnedLabel: partner-feed` immediately apply (the next request the matcher sees with that label gets the bucket).
4. The label is per-customer (per-domain in commercial), so two customers can label different actors `partner-feed` without collision.

This gives operators a stable name to attach policy to — separate from the volatile fingerprint hash which rotates as the actor's signals shift.

### 5. Action dispatch

`Action: rate-limit-<name>` in the detection-policy grammar resolves through the same `IActionPolicyRegistry` as today. The named action wraps a call to:

```csharp
var subjects = RateLimitSubjectResolver.Resolve(context, detection);
foreach (var rule in matchingRules)
{
    if (!_rateLimiter.TryAcquire(rule.Subjects)) {
        return await _actionRegistry.Execute(rule.OverLimitAction, context);
    }
}

// Wrap response body for data-rate enforcement
context.Response.Body = _dataRateLimiter.WrapResponseStream(
    rule.Subjects, context.Response.Body, rule.DataRate.BytesPerSecond);
```

The data-rate wrap only happens once the request passes the request-rate gate. Over-limit on the request-rate gate short-circuits to `OverLimitAction` before any response body work.

### 6. Adaptive scaling

The policy-grammar plan describes a `BotMultiplier` that scales `RequestsPerMinute` down when the origin degrades. Same multiplier applies here, to both `RequestRate.RequestsPerMinute` and `DataRate.BytesPerSecond` — humans never traverse rate limits so the scaling is invisible to them. The multiplier resolves at the `IRateLimiter.TryAcquire` call site (effective rate = configured rate × multiplier).

### 7. Persistence + cluster

| Concern | FOSS | Commercial |
|---|---|---|
| Bucket state | in-memory `ConcurrentDictionary` | Redis (single-key per subject) via `IRateLimitStateStore` |
| Custom labels | SQLite `dashboard_signature_labels` | PostgreSQL `dashboard_signature_labels` |
| Config | appsettings.json + on-disk YAML | same + commercial control-plane push |

In commercial, multi-gateway clusters share the same Redis buckets so a Scraper hitting 6 gateways at 10 req/min/each doesn't get 60 req/min effective. The interface boundary is `IRateLimitStateStore`; FOSS ships a `MemoryRateLimitStateStore`, commercial registers `RedisRateLimitStateStore`.

## Dashboard surface

- **Per-bucket card** on `/dashboard/policies`: subject expression, current tokens / capacity, refill rate, data-rate spend over last minute, hits in the rule that owns it.
- **Per-fingerprint chip** on the signature detail page: which rate-limit rules are currently consuming this signature's tokens, current bucket levels.
- **Per-customer label CRUD** on the visitor row: hover → "Label this actor" → text input → saves a `PinnedLabel` immediately usable in any rule.

Live updates flow through the existing SignalR beacon constrainer (10s cap), so a busy gateway doesn't flood the dashboard with bucket-state pushes.

## Out of scope

- Geo region maps (which countries are in "EU") — config-driven, not code; operators define their own.
- Algorithmic auto-tuning of caps based on traffic shape — out of v1.
- Per-tenant rate-limit isolation for multi-tenant commercial — separate spec.
- Cluster failover / Redis partition handling — relies on the existing commercial cluster spec.

## Open questions

- **Bucket key cardinality.** `Fingerprint` subjects produce one bucket per actor; at production rates (10k+ active fingerprints per hour) the in-memory map needs LRU eviction. **Tentative:** evict buckets idle for > 10x their refill interval.
- **Data-rate granularity.** Refill every 100 ms? Every 1 s? Smaller intervals are smoother but cost more CPU. **Tentative:** 250 ms refill window.
- **OverLimitAction default.** If a rule omits `OverLimitAction`, fall back to `throttle-status` (HTTP 429 + Retry-After) or to `logonly` (count the over-limit, do nothing)? **Tentative:** `throttle-status` — silent observe defeats the point of declaring a cap.
- **Subject precedence on composite OR.** A rule with multiple `Subjects` blocks today reads as AND. Do we also need an explicit OR? **Tentative:** no — operators write a second rule for the OR case. Composing AND/OR in a single rule is the slippery slope toward a tiny expression language.
- **Inheritance default direction.** `Inherit: true` (default) means "use parent's limits in addition to mine"; `Inherit: false` means "I replace the parent for any subject I redefine". Is the additive-default right, or should scope-defined-wins be the default with `Inherit: parent` opt-in? **Tentative:** additive-default — fewer surprises for operators who write a tighter cap at a leaf scope and expect the parent's other limits to still apply.

## Cross-spec dependencies

- **Detection Policy Grammar** (`2026-05-25-detection-policy-grammar-design.md`): provides the `Action: rate-limit-<name>` integration point.
- **Policy Grammar Core Experience** (`2026-05-24-policy-grammar-core-experience.md`): provides the `RateLimitActionPolicy` registration, the `PolicyIntent` enum, the adaptive-scaling multiplier.

Implementation order: this spec's primitives first, then the policy-grammar selectors that point at them.
