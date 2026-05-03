---
id: status-strip
title: "System Status"
section: always
commercial: false
---

# System Status

The status strip runs along the top of every dashboard page and gives you an at-a-glance health check for each major component of the StyloBot system. If anything is degraded or offline, it will appear here before you see any effect on the detection data.

## Indicators

- **Pack** -- the detection rule pack currently loaded. Shows the pack version. If it reads "Unknown" or shows a stale version, the gateway may not have received a recent update.

- **Detection** -- whether the detection pipeline is actively processing requests. Green means running; Amber means degraded (some signals unavailable); Red means the pipeline is not processing traffic.

- **Services** -- the health of internal support services (configuration loading, data storage, telemetry). A degraded status here means some dashboard data may be incomplete or delayed.

- **Guardians** -- the number of active gateway instances currently reporting to the system. If this drops unexpectedly, one or more gateways may have gone offline or lost connectivity.

- **LLM** -- the status of the AI escalation layer, used for borderline cases that require deeper analysis. Amber or Red here means borderline traffic may be falling back to rule-based decisions only. This does not mean detection has stopped -- it means the highest-confidence tier is unavailable.

## Colours

- **Green** -- fully operational.
- **Amber** -- degraded; partial functionality, data may be incomplete.
- **Red** -- offline or failing; immediate attention required.

**Tip:** If you see a red indicator and the detection data looks wrong (e.g. zero bots, sudden drop in requests), the status strip is the first place to check before investigating data issues.
