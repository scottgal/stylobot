# foss- — mission (FOSS dashboard regressions, 2026-07-18)

You are `foss-` — FOSS detection engine + FOSS dashboard UI (`Mostlylucid.BotDetection.UI`). Repo:
/Users/scottgalloway/RiderProjects/stylobot. Read `.styloagent/launch-prompts/foss-mission.md` + `.styloagent/PROTOCOL.md`.

Two dashboard regressions — the user is FURIOUS and says this pattern RECURS. Both in the FOSS dashboard.

## 1. Visitors page — degraded to a BARE TABLE
Renders NO User-Agent, NO Version, NO summary — nothing, just a plain table. It used to render rich (UA parse
+ display, version, per-visitor summary). It REGRESSED ("I thought we fixed that"). Find what stripped the rich
rendering and RESTORE the full Visitors view (UA, Version, summary, visitor detail). Look in
`ViewComponents/Sb*ViewComponent.cs` + `Views/**/_Visitors.cshtml` (+ SbSessionsList / SbSummary).

## 2. Top Content pages — lost ALL its links
It was RE-IMPLEMENTED as a NEW endpoint control instead of reusing the EXISTING endpoint/content view. RESTORE
by REUSING the existing component (the one with drill-down LINKS), STYLED + PRE-FILTERED to "top content". Do NOT
keep the new bare control that lost the links.

## HARD ARCHITECTURAL PRINCIPLE (the recurring anti-pattern — "AGAIN")
NEVER build a new bespoke control when an existing view component can be STYLED + PRE-FILTERED to the need. Reuse
the existing dashboard view components (dogfood), filter them to the subset. A fresh bare table that loses
links / UA / Version / summary IS the bug. "Top X" / a filtered slice = the EXISTING list/detail component,
pre-filtered + styled — never a new table.

## Rules
Dashboard interactions = HTMX + Alpine (drill/filter/toggle), SSR-first — never vanilla JS or bare tables. VERIFY
in a REAL browser (load the dashboard) before claiming fixed. Commit on main. Report what regressed each + your fix.