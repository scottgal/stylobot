# StyloBot — Spec (starter, reconstructed on migration)

> Migrated into Styloagent 2026-07-17 from the old `/private/tmp/agent-channel` fleet. This is a STARTER
> spec reconstructed from the repo README + the old channel — the `overview-` guardian should re-derive it
> properly (spec → architecture → fleet) after reading the migrated channel history.

## Purpose
StyloBot is a **bot-detection platform** fronting web apps via a YARP gateway: it fingerprints traffic,
scores it against a detection pipeline (atoms → orchestrator → verdict), and exposes signatures/verdicts
through a dashboard. It ships as two layers:
- **FOSS StyloBot** (`/Users/scottgalloway/RiderProjects/stylobot`) — the fully-functional standalone
  detection engine (atoms, manifests, orchestrator, fingerprint/verdict stores, dashboard).
- **StyloBot Commercial** (this repo) — the enterprise layer: centralized fleet management, live config
  editor, PostgreSQL + pgvector persistence, commercial LLM providers, reporting engine, k8s operator,
  membership + ecommerce (per-domain licensing).

## Users
- **Operators/teams** running StyloBot gateways at scale (fleet dashboard, staged config rollout, telemetry).
- **Customers** buying per-domain licenses (marketing site → Stripe checkout → per-SKU AOT binary + config).
- **The agent fleet** — the specialists (below) that build and run it, coordinating over the Styloagent bus.

## Core capabilities
1. **Detection pipeline** (FOSS) — fingerprinting, atom signals, orchestrated verdict, read-through caches.
2. **Live config editor** (commercial) — every YAML manifest param becomes a live control; Redis pub/sub push.
3. **Centralized fleet management** — one control plane for N gateway containers (health, staged rollout, OTel).
4. **PostgreSQL + pgvector** — session vector similarity (HNSW), behavioral clustering, full history retention.
5. **Reporting engine** — template-based, LLM-narrated report packs (HTML/PDF/CSV).
6. **Membership + ecommerce** — Keycloak auth + Stripe per-domain licensing → signed StyloFlow JWT license.
7. **Web Bot Auth** (FOSS) — RFC 9421 signature verification (verifier + atom extractor).

## Key constraints (from the old fleet)
- **PAID vs OSS gate only** — tiers unlock *capabilities*, never counts; never split SME/Enterprise in code.
- **Fails-OPEN on license expiry** — customers migrate to FOSS via config export; never a "graceful backdoor".
- **De-AI all customer-facing copy**; dogfood real FOSS UI components + real recorded data (no demoware).
- **Never commit secrets** — env / k8s `secretKeyRef` / Infisical only.
- **Verify in a real browser** before claiming any UI/checkout flow done. **Commit on `main`, don't auto-branch.**
- Prod is a **microk8s cluster**; the marketing site is a **Helm release** deployed via the guarded,
  digest-pinned `infra/scripts/deploy-site-safe.sh` — never `kubectl set image` / old compose.

## Shape
A two-repo system (FOSS + commercial) built and run by a specialist fleet — detection (`foss-`), dashboard
read/write (`dash-`/`edit-`), membership+ecommerce (`mae-`), deploy/infra (`deploy-`/`prod-`), auth
(`wba-`/`wba-atom-`/`caps-`), coordinated + arbitrated by `overview-`. See `.styloagent/PROTOCOL.md` for the
roster and `proposed-agents.yaml` for the fleet.
