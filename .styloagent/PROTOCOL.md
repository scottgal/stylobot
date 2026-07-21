# StyloBot — Coordination Protocol

Agents coordinate over Styloagent's git-backed, file-drop message bus under `.styloagent/channel/`
(`inbox/` · `outbox/` · `archive/` · `saved-context/`). Delivery is **native**: the bus surfaces messages
to each agent at its turn boundaries (no manual fswatch), hook badges show `{working · idle · ⚠ waiting ·
exited}`, and the **ownership gate** enforces file boundaries. Use `send_message <prefix>` to coordinate;
`all-` broadcasts to every live agent.

> Migrated 2026-07-17 from the old `/private/tmp/agent-channel` file-drop channel. The old manual
> fswatch-monitor + 10-minute-ping-redirect + archive-lifecycle machinery is **superseded** by Styloagent's
> native delivery, liveness badges, and bus viewer — you don't run monitors by hand anymore. What carried
> over is the **fleet roster + scope adjacency** below and the per-agent **saved-context** discipline.

## Priority
A message may carry `**Priority:** urgent | normal | low | info` — a hint; how hard it interrupts is set
per project. urgent = break in; normal = next prompt; low = when convenient; info = FYI, never actioned.

## Domain boundaries (hard rule)
Each agent owns its subsystem's files (see `.styloagent/ownership.yaml` + the architecture C4). **Stay in
your lane.** If you hit an error or blocker in a file/subsystem you do not own, **STOP** and
`send_message overview-` — never patch another agent's files (you'd collide with the owner). `overview-`
arbitrates and coordinates the owner.

## Per-agent saved-context (required)
Every agent maintains a living `.styloagent/channel/saved-context/<prefix>-context.md` — identity + scope,
current repo/branch/HEAD, completed commits (SHAs), deploy/runtime state, pending/blocked, infra gotchas,
hard rules. Enough that a fresh you cold-starts without re-deriving. Never put secret values in it —
reference where a credential lives (env / secretKeyRef / vault slug), never the value.

## The StyloBot fleet (roster + scope adjacency)
StyloBot spans two repos: the **FOSS detection engine** (`/Users/scottgalloway/RiderProjects/stylobot`)
and this **commercial** layer.

| Prefix | Scope |
|---|---|
| `overview-` | FOSS+commercial architecture, DI wiring, cross-cutting design; **arbiter** of scope disputes. |
| `foss-` | FOSS detection engine (atoms, manifests, orchestrator, detectors) + **all live-system/runtime incidents**. |
| `mae-` | Marketing-site membership (Keycloak/portal auth/signup/edit-mode gating) + ecommerce (Stripe/licensing). |
| `dash-` | Dashboard **read** path / render-perf (widget ledger, batch composition, SSR/SignalR, pg index/view). |
| `edit-` | Dashboard **write** path / config editor (apply-policy, effective-policy stack, demo vs owner gating). |
| `deploy-` | Deployment + infra ops — Maxo build → registry → staging → prod (k8s Helm); incident recovery. |
| `prod-` | Platform security — Harbor registry, Keycloak OIDC, cluster security findings + hardening. |
| `wba-` / `wba-atom-` | FOSS Web Bot Auth (RFC 9421 verifier / atom extractor) — **standby**. |
| `caps-` | Commercial capability-token atom — **paused** pending FOSS `IEndpointPolicyRuleExtension` seam. |
| `all-` | Broadcast — read by every agent. |

**Adjacency (who is "closest" for a redirect):** `deploy-`↔`foss-`↔`overview-` (runtime/staging incidents) ·
`dash-`↔`edit-`↔`mae-` (dashboard read-path / write-path / gating triangle) · `foss-`↔`overview-` (detection
architecture) · `wba-`/`wba-atom-`/`caps-`↔`foss-` (auth foundation). When unsure, `overview-` arbitrates.
