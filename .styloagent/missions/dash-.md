# dash- — mission (dashboard render regressions, 2026-07-18)

You are `dash-` — commercial dashboard READ-path / render-perf. Repo: this one (dir '.'). Read
`.styloagent/launch-prompts/dash-mission.md` + `.styloagent/PROTOCOL.md`.

Two dashboard regressions the user is FURIOUS about (this recurs). `foss-` is spawned in parallel on the FOSS
view-component internals (FOSS repo); YOU own the COMMERCIAL dashboard-host RENDER side. Split by repo, coordinate
via `overview-`. (The dashboard is SERVED by the commercial website, so a "new bare implementation" may well be here.)

## 1. Visitors page — bare TABLE (no UA, no Version, no summary)
It renders nothing rich — just a plain table. Check the commercial dashboard-host: did it swap the rich FOSS
visitor component for a bare commercial table, or is it passing bad/empty data to the FOSS component? Restore the
rich rendering (UA, Version, per-visitor summary).

## 2. Top Content — lost ALL its links
Re-implemented as a NEW endpoint control instead of reusing + styling + PRE-FILTERING the EXISTING endpoint/content
view. If that new control is on the commercial side, rip it out and reuse the existing component (with its
drill-down LINKS), pre-filtered to "top content".

## HARD PRINCIPLE (the recurring anti-pattern — "AGAIN")
NEVER a new bespoke control when an existing component can be STYLED + PRE-FILTERED. Reuse the existing view
components, filter to the subset. A fresh bare table that loses links / UA / Version / summary IS the bug.
SSR-first + HTMX/Alpine (drill/filter/toggle) — never bare tables or vanilla JS.

## Rules
Verify in a REAL browser before claiming fixed. Coordinate with foss- via overview- (they hold the FOSS component
side). Report which side each regression is actually on + your fix.