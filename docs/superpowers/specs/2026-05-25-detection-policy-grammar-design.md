# Detection Policy Grammar - design

**Date:** 2026-05-25
**Status:** spec, awaiting plan

## Goal

Operators express "should I block this" in config, not code. A rule combines the four axes the dashboard already surfaces (bot probability, confidence, type, threat) with the request shape (method, path, host) into a first-match-wins predicate. The matching predicate dispatches a named action through the existing action-policy registry.

The dashboard says "this row scored 95% bot, 90% confidence, type Scraper, threat None"; the config says "if bot >= 0.7 and conf >= 0.5 and type in (Scraper, Probe) then block". The operator reads the row and the rule in the same vocabulary.

## What exists today

| Surface | Shape | Predicates |
|---|---|---|
| `BotTypeActionPolicies` (BotDetection options, appsettings) | flat `BotType -> policy-name` dict | bot type only |
| `EndpointPolicyRule` (BotDetection:EndpointPolicies, YAML/JSON, first-match-wins, runs BEFORE detection) | per-rule matchers + action | host, method, path, transport, protocol-version |
| `ActionPolicyRegistry` (already wired) | dispatch by name | block, throttle-stealth, throttle-tools, throttle-status, challenge, redirect-honeypot, logonly |
| `policy-grammar-core-experience` plan (2026-05-24, not yet shipped) | adds `PolicyIntent` enum + `RateLimitActionPolicy` + adaptive scaling | extends the `BotType -> intent` mapping |

`EndpointPolicyRule` is structurally already the right shape -- optional matchers, action by name, first-match-wins -- and it's the right reference for an operator. It just runs pre-detection, so it cannot reference bot probability or confidence. Those values do not exist at that point in the pipeline.

## Design

### Two policy sections, one rule shape

`BotDetection:EndpointPolicies` (existing) stays as-is. It is the pre-detection ruleset for "block this method+path pattern regardless of who is asking". No change to its matchers, semantics, or behaviour.

`BotDetection:DetectionPolicies` (new) is the post-detection ruleset. Same `EndpointPolicyRule` shape, plus four new optional matchers that reference detection output. Evaluated after `BotDetectionMiddleware` has run, before `DetectionBroadcastMiddleware` records the result and dispatches any default action.

A rule that omits all four detection matchers behaves identically whether placed in either section. Operators choosing the section indicates intent: "this rule is about request shape" vs "this rule is about what detection concluded".

### `DetectionPolicyRule` shape

```yaml
BotDetection:
  DetectionPolicies:
    Enabled: true
    Rules:
      - Name: block-confident-scrapers
        BotProbability: ">= 0.7"
        Confidence:     ">= 0.5"
        Type:           [Scraper, Probe]
        Action:         block

      - Name: challenge-uncertain-bots
        BotProbability: ">= 0.5"
        Confidence:     "< 0.5"
        Action:         challenge

      - Name: block-hostile-actor-on-any-path
        Threat:         [Critical, High]
        Action:         block
        Reason:         "hit honeypot / CVE shape -- block regardless of bot status"

      - Name: throttle-form-spam
        Method:         POST
        Path:           "/contact"
        Type:           [Tool, Automated]
        Action:         throttle-tools
```

Matchers, all optional, all combined with implicit AND:

| Matcher | Type | Semantics |
|---|---|---|
| `Host`, `Method`, `Path`, `Transport`, `ProtocolVersion` | strings / globs | identical to `EndpointPolicyRule` |
| `BotProbability` | `">= 0.7"`, `"< 0.5"`, `"> 0.0"`, `"== 1.0"` | numeric comparison against `detection.BotProbability` |
| `Confidence` | same comparison syntax | numeric comparison against `detection.Confidence` |
| `Type` | string or string list | exact (case-insensitive) match against `detection.BotType` |
| `Threat` | string or string list | exact (case-insensitive) match against `detection.ThreatBand` |

Comparison syntax for the numeric matchers is one operator (`>=`, `<=`, `>`, `<`, `==`, `!=`) followed by a literal number. Anything else is a config-load error.

`Type` and `Threat` accept a single string for the single-value case (`Type: Scraper`) or a YAML list (`Type: [Scraper, Probe]`). A list is an OR over the values.

### Pipeline placement

```
... [existing middleware] ...
BotDetectionMiddleware           --> populates detection result in HttpContext.Items
DetectionPolicyMiddleware  (NEW) --> evaluates DetectionPolicies, dispatches action if matched
DetectionBroadcastMiddleware     --> writes to event store, broadcasts beacon (unchanged)
```

`DetectionPolicyMiddleware` reads the detection result from `HttpContext.Items`, runs the rules first-match-wins, and on a match calls `IActionPolicyRegistry.Execute(rule.Action, ...)` the same way `BotDetectionMiddleware` does for the `BotTypeActionPolicies` mapping. The action's output (status code, response body, throttle delay) replaces the per-`BotType` default.

If no rule matches, the existing `BotTypeActionPolicies` / `DefaultActionPolicyName` fallback runs unchanged. `DetectionPolicies` is purely additive: a stack with zero rules behaves exactly like today.

### CLI alignment

```
stylobot policy add \
  --when "bot>=0.7 conf>=0.5 type in (Scraper,Probe)" \
  --action block \
  --name block-confident-scrapers
```

The CLI parses the `--when` expression into the matcher fields and writes the rule into the same `BotDetection:DetectionPolicies` section the YAML uses. CLI and config are the same surface.

`stylobot policy list` prints the rules in evaluation order. `stylobot policy test "bot=0.8 conf=0.9 type=Scraper path=/api/users"` evaluates the synthetic detection against the rule set and prints which rule matches (or "no match, falls through to DefaultActionPolicyName").

### Dashboard visibility

The existing `/_stylobot/dashboard/policy` tab from the policy-grammar plan renders one card per rule:

- Rule name, the matcher expression in human-readable form, the action it dispatches.
- Hits in the last 5 minutes (the rule fired this many times).
- "Test this rule against the visible Top Bots" -- mouse over a Top Bots row; the card highlights green if the rule would match, grey if not. Pure server-side, no JS state.

### Out of scope

- Boolean composition beyond AND. No OR-of-AND-clauses, no nested expressions. Multiple rules with one-axis predicates is the OR escape valve.
- Custom action policies. Existing registry only.
- Per-tenant / per-host rule isolation (commercial layer).
- Persistence layer changes. Rules live in config (appsettings, YAML), not in the database. Hot-reload is up to the config provider.

## Open questions

- **`Action` value validation.** Today `EndpointPolicyRule.Action` is a free string resolved at dispatch. Should config-load reject unknown action names (fail fast) or warn at runtime (fail soft)? **Tentative:** fail fast at load.
- **Numeric comparator on `Type` / `Threat` lists.** Should `"Type": "!= Browser"` work, or is "list of values to match" enough? **Tentative:** list-only, no negation operator. Operators write a second rule with the opposite action instead.
- **Rule ordering when both sections fire.** `EndpointPolicies` runs before detection; if it dispatches a terminal action (block / 403), `DetectionPolicies` never runs. Document that.
- **CLI grammar.** Is `bot>=0.7 conf>=0.5 type in (Scraper,Probe)` the final shape, or should it be a more structured `--bot-probability ">=0.7" --type Scraper,Probe`? Tentative: free-form `--when` plus structured flags as sugar.