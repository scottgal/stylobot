# FOSS dashboard-collapse extension-point plan

**Author:** stylobot- (FOSS architect) · **Status:** CO-SIGNED by foss-; implementation cleared
**Design input:** overview-'s relayed catalog (dash-'s approved classified inventory), 2026-07-21.

## Principle (from the operator ruling)
One dashboard = the FOSS `Mostlylucid.BotDetection.UI` RCL / `Stylobot.Ui` host. Adopt each commercial
IMPROVEMENT INTO FOSS, config-selectable where both variants are worth keeping. Never a commercial shadow
copy. Delete the duplicates. Novel marketing chrome stays a thin add-on. Config lives in YAML/options, not
hardcoded.

## A. Live-feed fix — HIGHEST PRIORITY (the frozen-dashboard root cause)
Problem: the marketing site appeared frozen because its live-source configuration was absent; the local
dogfood host was incorrectly assumed to need the remote relay.
Validated design: `SignalRBeaconRelay` remains in the `Stylobot.Ui` remote-mode host and is enabled only when
`StyloBot:Source:Live:Type = signalr`. The marketing site is local dogfood: its materializer already broadcasts
invalidation beacons directly from the `ScheduleCoordinator` tick, so `AddStyloBotDashboard(...)` does not
register a relay. The materializer tracks warmed page keys, bumps the change cursor, and queues dirty-kind
beacons through `SignalRBroadcastConstrainer` for HTMX OOB refreshes. **NO BackgroundService, NO timer** for
the local path (operator hard rule).
→ foss- validated the registration seam, remote-only predicate, and dirty-kind availability on the tick.

## B. Extension points for the shadow-override deltas (kill the copies)
For each: seam + FOSS home + what commercial deletes.

- **#1 multi-domain filter widget** (`Traffic/_Body`): FOSS keeps the data plumbing it already has
  (`TrafficFilters.Domains`, SQL `IN`, remote passthrough) and adds a **named render slot** in `_Body` (slot
  host). Per dash-'s inventory the filter is **license-gated** and multi-domain is a fleet/multi-tenant
  capability (meaningless in single-domain FOSS standalone), so this is the #6/#7 pattern, not a FOSS toggle:
  FOSS ships the slot host (empty by default), commercial fills it license-gated. FOSS single-domain operation
  is unchanged (no FOSS capability removed). Delete the commercial `_Body` override.
- **#2 `_TrafficPanels`** (adopt improvements into FOSS):
  - Country widget: **config-selectable `bar|map` in FOSS** (proposed `Dashboard:CountryWidget:Style`). Keep
    both; FOSS already has jsVectorMap so `map` is a config flip. **Proposed default: `bar`** (data-first, no
    map asset on the default path). This is an aesthetic call overview- may want to own; I propose bar and
    defer the final default to the operator.
  - "By source (excl. internal)": becomes a FOSS option (proposed `Dashboard:SourceBreakdown:ExcludeInternal`).
  - Pack-contribution: add a dedicated FOSS render slot at the foot of `_TrafficPanels` ("pack contribution
    slot") so packs + commercial inject without forking the view.
  - Delete the commercial `_TrafficPanels` override.
- **#3 `_YourDetection` radar vs triangle**: **config-selectable in FOSS** (proposed
  `Dashboard:DetectionShape:Viz = radar|triangle`), both available. **Proposed default: `radar`** (the
  established information-rich FOSS behavioral-shape viz); triangle is the simpler variant, opt-in. overview-
  offered the operator's call on this default; I propose radar and defer the final call.
- **#4 `_VisitorsSection`**: FOSS visitors section reads + forwards the URL filters natively. foss- already
  fixed FOSS Visitors filter-forwarding in `ec6907af`; if that covers it, #4 is **delete the shim only**.
  foss- confirm.
- **#5 `_SiteEndpointDetail`**: FOSS `SiteController` provides the single-endpoint detail composition; delete
  the commercial shim.
- **#6 location tree / #7 site health**: FOSS provides the **SLOT HOST** (a VC slot / named render section);
  commercial fills it license-gated. FOSS ships the slot (empty or a FOSS-default fill); commercial injects
  via the slot, not a view override.
- **#8 config editor**: OUT OF SCOPE (edit- lane). Leave as-is.

## C. Commercial-side config — MUST accompany the copy deletions (deploy-/config lane)
The FOSS code seams are necessary but NOT sufficient. When dash- deletes the commercial copies, the website
config must set these or the site regresses (dash- flags, 2026-07-21):
1. **Live feed (CRITICAL):** foss-'s conditional relay registration only fires when a live source is
   configured. The website today runs `AddStyloBotDashboard` (Program.cs:255) + `AddStyloBotDashboardRemote`
   (:157, REST-only) and sets NO live source — that is why it's frozen. `deploy-`/config must set
   `StyloBot:Source:Live:Type=signalr` + the gateway hub URL on the website, or it stays frozen AFTER the code
   fix. This is in the sequence, not a post-deploy discovery.
2. **Country widget:** commercial deployment config sets
`BotDetection:Dashboard:Materializer:CountryWidgetStyle=map` (operator-
   confirmed want — dash- verified via the `_TrafficPanels` "rebuilt per the operator's call" comment). FOSS
   out-of-box default stays `bar`.
3. **Detection shape:** commercial stays on the FOSS default `radar`. The 3-axis triangle is agent-introduced
   with NO operator request (dash- confirmed), so it is NOT selected commercial-side; it remains available via
   `Dashboard:DetectionShape:Viz=triangle` only if later confirmed wanted. Config-selectable, so no capability
   is lost by dropping it as the commercial default.

## Seam conventions
Config keys above are proposals; foss- aligns them to the real `DashboardPageManifest` / ViewComponent /
options conventions in the RCL. Slots use the existing VC/named-section pattern the dashboard already uses,
not a new mechanism.

## Sequence (de-gated by operator GO — no overview- ack between steps)
co-sign (stylobot- + foss-, done) → foss- implements FOSS side (materializer/live-feed validation FIRST, then
extension points) → dash- deletes commercial copies (3 overrides + 2 shims + Overview.cshtml + DashboardController)
wires FOSS slots + reverts `33be9b8d` → **deploy-/config sets the website live
source (`StyloBot:Source:Live:Type=signalr` + gateway hub URL) and
`BotDetection:Dashboard:Materializer:CountryWidgetStyle=map`
(section C) — without this the site stays frozen / loses the map** → deploy- redeploy → overview-
browser-verifies live updates.

## Validation record (foss- co-sign)
1. Live-feed seam: relay remains remote-only; local materializer broadcasts from the scheduler tick.
2. #4: `ec6907af` forwards all five Visitors URL filters, making the commercial shim delete-only.
3. All catalog deltas and proposed slots are feasible against the real RCL.
4. Defaults agreed: country widget `bar`; detection shape `radar`.
