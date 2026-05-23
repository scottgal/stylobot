# Endpoint-policy dashboard surface (deferred)

> Originally captured as agent memory after the `feat/endpoint-policies` PR (commit `205911b` on `main`). Materialised here so the commercial integration agent and any future contributor can pick it up without re-deriving the context.

## Status

Detection-side functionality is complete and verified end-to-end on Demo. The chip + per-endpoint list is **UI polish that doesn't change protection behaviour**, so it shipped separately from the core to let the commercial agent integrate and test the policy layer first.

## What's already wired (data is on the request)

- `HttpContext.Items[EndpointPolicyMiddleware.ItemKeyMatch]` carries the matched `EndpointPolicyMatch` record (rule + action + status code + reason) on any request whose rule fired.
- `IEndpointPolicyResolver.Rules` exposes the full compiled rule list, so a "rules that could match this endpoint" view needs no new middleware.

## What to build when picked up

- New section on `Views/StyloBot/Dashboard/_EndpointDetail.cshtml` listing matched rules per `(method, path)`. Each row: method, transport requirement, action, status code, reason, hit count over window.
- New `IDashboardEventStore.GetEndpointPolicyHitsAsync(method, path, ...)` aggregating from the `detections` table. The broadcaster needs to start writing `action = "endpoint-policy:<name>"` so hits are recordable.
- Sidebar chip on the Honeypot tab showing how many policy hits short-circuited before honeypot in the window -- helps operators see overlap.
- "Edit" buttons gated on `IsCommercial(context)`: FOSS view-only with a link to the YAML; commercial gets in-place editing.
- Per-rule hit counter stored in-memory (FOSS); commercial extends to persistent storage + analytics.

## Do not change

- **Rule evaluation order.** First-match-wins is part of the contract.
- **The status-override fast path** in `EndpointPolicyMiddleware` -- it bypasses the action-policy registry deliberately so `block` + `StatusCode` lands directly without `BlockActionPolicy` hardcoding 403. If commercial introduces a `BlockWithStatusActionPolicy`, that can replace the bypass; until then, keep it.
- **Honeypot rate-limit key resolution** (`sig:` → `ip:` → `"anon"`). Commercial may add an `identity:` keying tier via the metastable layer -- documented as an upgrade path, not a breaking change.
