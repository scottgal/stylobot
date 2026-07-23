# stylobot- context (updated 2026-07-23 ~12:15, post-restart resume)

## Post-restart resume note (read first)
Fleet came back with only 2 root agents alive: `overview-` (stylobot-commercial) and `stylobot-` (me,
this repo). foss-/bench-/deploy-/dash-/mae-/aspnet- are NOT currently spawned — respawn on demand, not
proactively. Found dash-'s item-2/3 WIP (signature-detail Detection Signals → shared `_DetectionReasons`
partial) sitting UNCOMMITTED in this shared checkout, contradicting the "no uncommitted source work" line
below (stale). Verified build clean + 4/4 targeted tests + 988/989 broader UI/Dashboard suite (1
pre-existing unrelated `_TrafficPanels.cshtml` hardcoded-mount failure, confirmed pre-existing via
`git stash` baseline on 2853e929). Committed `15c8f63c` and pushed FF to both `foss/dashboard-collapse`
and `main`. Item 2 (verdict-merge + slot host + signed signals) is now DONE; OTEL empty-collapse part of
item 2 not yet confirmed done — check with dash- if respawned. Many other worktrees exist
(`.worktrees/feat-grpc-caddy`, `feat-node-sdk-liquid`, `reaction-packs`, `.claude/worktrees/atom-followups`,
`session-band`, etc.) not covered by this checkpoint — untouched, not investigated this session, do not
assume stale without checking `who_touched`/branch history first.

---

# stylobot- context (checkpoint 2026-07-23 ~12:02, pre-styloagent-restart)

**Identity:** FOSS architect/overview agent for StyloBot FOSS repo `/Users/scottgalloway/RiderProjects/stylobot`.
Hold spec/architecture/fleet; manage FOSS specialists (foss-, bench-, deploy-). Coordinate over bus per
`.styloagent/PROTOCOL.md`. Operator style: no A/B/C, no em-dashes, act don't grind, verify-before-checkin.

## Branch/release state (CRITICAL)
- **main == foss/dashboard-collapse == `2853e929` (allbot-v8.2.5)`.** I keep both in LOCKSTEP: every FOSS
  commit pushes to both `origin/foss/dashboard-collapse` AND `origin/main` (FF). When foss- commits to
  main alone, I re-sync foss/dashboard-collapse up to it. `dotnet` = `/usr/local/share/dotnet/dotnet`.
- **8.2.x line shipped THIS session (all tagged, tested):** 8.2.0 Dashboard-V2 release (c360cfd3) →
  8.2.1 /api/v1/domain-stats (cb9375a2) → 8.2.2 /summary window fix (afeb7c6c) → 8.2.3 domain-stats
  row-level internal-exclusion (9f448046) → 8.2.4 ISignaturePolicyActionSlot seam (4ca63050) →
  8.2.5 SNI-validated domain attribution keystone (2853e929).
- **NEVER stage these floating working-tree files** (coordination/artifacts, not my work):
  `wwwroot/vendor/css/tailwind.min.css` (M), `.styloagent/model-policy.yaml` (??), this saved-context.
  I have NO uncommitted source work — everything is committed to 8.2.5.

## IN-FLIGHT / AWAITING — exact next steps

### 1. SNI keystone (8.2.5 / 2853e929) — awaiting deploy on prod, then verify
- BUILT+TESTED: DomainNormalizer.Resolve prefers gateway-validated SNI over Host header; Host fallback
  only on non-gateway topologies; evaluated-not-served → "unknown" + `HttpContextItemKeys.TlsSniNotServed`
  flag. Gateway ProxyProtocolKestrelExtensions cert-selector captures via `SelectAndCaptureSni`
  (`TlsConnectionKeys.ValidatedSni`/`SniEvaluated`). 26/26 RequestScope/DomainNormalizer tests.
- overview- CONFIRMED prod uses exactly the wired path (PROXY-protocol multi-cert selector) — NO other
  TLS-path wiring needed. Prod baseline (deploy-): 116,355 detections/24h ALL domain='unknown'.
- **NEXT:** overview- must bump the next gateway cut FOSS ref afeb7c6c→2853e929 (+ fold log-sink fix
  d88c722d) → deploy- runtime-verifies: do new detections record real domain (stylo.bot/stylobot.net)
  vs 'unknown'? I asked overview- (sequencing). deploy- is holding for the cut.
- **IF still 'unknown' after deploy** (PP-then-TLS connCtx.Items bridge failed): ship the FALLBACK —
  capture SNI per-request via `ITlsHandshakeFeature.HostName` + validate against served certs (sidesteps
  connection-items). Already scoped, not built.
- **Follow-ups (non-blocking, filed):** surface `tls.sni_not_served` as a detector atom/signal (flag is
  stashed, ready); forwarded-domain-header fallback (ITransportHeaderTrust-gated) for deprecated
  CF/Caddy-fronted stylobot.net.
- **When verified real:** ping mae- (Domains panel/TrafficFilter) + aspnet- (pack) — their managed rows
  populate (they read ILicensedDomainStore, commercial; FOSS served set maps to their licensed set).

### 2. Signature-detail redesign (mae- leads; FOSS seams mine)
- Item 1 slot seam DONE: `ISignaturePolicyActionSlot` (8.2.4). mae- builds commercial policy block against it.
- Items 2+3+4 = **dash-** (overview- assigned): _SignatureDetail.cshtml one layout pass = verdict-merge +
  the slot VIEW HOST (@inject/@Render) + signed Detection-Signals binding + OTEL empty-collapse. I handed
  dash- the recipe: item 3 signed data ALREADY EXISTS — bind `Model.DetectorContributions`
  (`SignatureDetectorEntry.ConfidenceDelta` signed = ↑/↓, `Contribution` = weight), fallback TopReasons.
  No FOSS plumbing needed.
- **Item 5 = ME, QUEUED:** investigate what `Mostlylucid.BotDetection.StyloExtract` transforms (in/out
  contract) + report to mae- so she wires ?markdown on the site content pages (commercial). mae-: no rush.

### 3. Detection false-positives → foss- (its Signal-Assay/archetype lane; on P0 idle-wait)
- **Auth-flow archetype:** recognize OIDC/OAuth (server-to-server backchannel + browser redirect) so legit
  auth isn't bot-throttled. Centroid/archetype (parallels foss-'s RegistryClient: protocol corroboration →
  negative-delta, no bypass). Concrete Keycloak endpoints given: /.well-known/openid-configuration,
  /protocol/openid-connect/token, /ext/par/request, /certs (generalize the family). Immediate login
  unblock = mae-/deploy- in-cluster backchannel routing (topology). foss- designs → gates with overview-.
- **Chrome IP Protection FP:** real Chrome behind Google Privacy Proxy scored malicious on IP tier. Fix =
  Signal-Assay environmental adaptation: known-privacy-proxy IP + genuine browser signals → IP tier
  NEUTRAL, behavioral wins. Google publishes IP-Protection ranges (seed the classification, verdict stays
  behavioral). No IP-allow-list bypass. foss- designs → gates. May out-prioritize auth-flow (LIVE FP).

### 4. Domain-stats ↔ Traffic reconciliation — DONE
- 8.2.1 domain-stats + 8.2.2 /summary-window + 8.2.3 internal-row-exclusion. Stores aligned (FOSS Sqlite ==
  commercial Postgres 531a4f1c). mae- re-verifies pool≈counter on staging post gateway cut. No action.

## FLEET (my children)
- **foss-** (agent/foss-runtime worktree): P0 gateway-crash-under-concurrency = fd exhaustion at ulimit
  1024 (EMFILE); spine fix `a91b4bea` (self-raise RLIMIT_NOFILE) HELD, not merged, pending Pi verify.
  P2 (AOT CountryCode) merged (af0a12a0). Now designing auth-flow + Chrome-IPP. Csproj Api CS0012 fix
  done (84145471).
- **bench-** (.worktrees/bench-aot): Half-A local NativeAOT micro-benchmarks DONE (doc
  docs/aot-benchmarks-2026-07.md). Half-B (Pi load/plateau/stress + linux-arm64 Pi micros) BLOCKED on the
  Pi being up + .39 SSH grant + .15 free of deploy-3 verify. Pi SUT binary = released v8.1.7 (operator
  choice), perf-equivalent to main for hot paths.
- **deploy-**: holding for the gateway cut to verify SNI keystone.

## OPERATOR DIRECT ASKS — still open
- **Hidden-links feature (NOT built, awaiting operator design approval):** per-role hidden nav links so
  sensitive commercial paths (purchase/membership) aren't discoverable on the stylo.bot site, still
  fully detection-enabled. DECIDED with operator: VISIBILITY-ONLY (paths stay reachable) + config
  path-patterns + privileged-viewer bypass (one tier). DESIGN shaped: FOSS `INavVisibilityPolicy` seam
  (config `Dashboard:HiddenPaths` glob patterns + host-supplied `IsPrivileged`) that both FOSS dashboard
  nav (_SidebarV2.cshtml) + commercial site nav filter through; commercial configs its purchase/membership
  patterns. Detection UNTOUCHED (pure render-time nav filter, no routing/skip). I presented this design;
  operator hadn't given final go before this thread got busy. NEXT: confirm operator wants it, then
  spec (docs/superpowers/specs/) → route (FOSS seam = me/dash-; commercial config = overview-).
- **.39 SSH grant + .15 freed** → unblocks bench- Half-B + the foss- P0 Pi verify (a91b4bea merge).

## Other DONE this session
- npm security deps (body-parser/grpc-js/fast-uri/protobufjs) — main clean, NuGet clean.
- Console build-break (missing Api.Endpoints using) — af84fafa.
- Api CS0012 direct-refs (routed to foss-, 84145471).
- htmx-1→htmx-2 json-enc fix on COMMERCIAL website: dash/deploy-3 @ 26620bb5 (htmx-ext-json-enc, dist
  gitignored so source-only). Optional FOSS vendored htmx 2.0.4→2.0.8 bump still on my list, non-urgent.
- CA-analyzer "137 errors" scare: it's committed CA WARNINGS (274) under a strict build config, NOT
  uncommitted WIP; Directory.Build.props pins CodeAnalysisTreatWarningsAsErrors=false so tags are clean.

## Resume first move after restart
Check inbox + fleet_status. Priority order: (1) SNI keystone deploy/verify result from deploy-;
(2) any foss- archetype design gates to relay to overview-; (3) operator's hidden-links go + .39 grant.
Prod is live+healthy (gw a725cbd7 / web b26f25c5), nothing mid-mutation, no uncommitted source.
