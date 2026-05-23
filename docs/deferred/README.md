# Deferred work

Design and follow-up notes for work that has been **scoped, partially built, or fully designed** but not yet shipped. Each file is self-contained: status, what's already wired, what to build when picked up, what NOT to change.

These started life as agent-local memory notes. They're materialised here so:

- A future contributor (human or agent) can pick the work up without re-deriving the context.
- The commercial integration agent can read the same source the FOSS agent did.
- The notes survive any single agent's memory window.

Current entries:

- **[endpoint-policy-dashboard-surface.md](endpoint-policy-dashboard-surface.md)** — UI follow-up to `feat/endpoint-policies`. Detection-side complete; chip + per-endpoint matched-rule list deferred. Data already lands on `HttpContext.Items[EndpointPolicyMiddleware.ItemKeyMatch]`.
- **[per-host-site-profiles.md](per-host-site-profiles.md)** — YAML `host → stack` mappings that modulate the honeypot exempt list only. Orthogonal to simulation packs. ~12 embedded YAMLs + dashboard chip when picked up.
- **[scanner-path-catalog-consolidation.md](scanner-path-catalog-consolidation.md)** — Twelve files duplicate scanner-path knowledge. Plan: tag every entry in `HoneypotPathDefinitions` with a category enum, then route every other consumer through `Classify(...)`. Two commits: catalog first, consumer rewrites second.
