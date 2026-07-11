# Upgrading StyloBot (FOSS): 7.x to 8.x

This guide covers upgrading the **FOSS** package from the 7.x series to 8.x. FOSS persists to **SQLite**; PostgreSQL is a new **commercial** option introduced at 8.x and is not part of a FOSS upgrade (see [PostgreSQL](#postgresql-new-at-8x-commercial) below).

## TL;DR

- Bump the package version `7.*` to `8.*`. The package ID (`mostlylucid.botdetection`) and the `Mostlylucid.BotDetection.*` namespaces are **unchanged**: no find-and-replace, no `using` rewrites.
- Your existing SQLite databases **migrate in place** on first 8.x startup. Back them up first (routine precaution), then just start the app.
- One required config check: **`DatabasePath` can no longer be null.** Set it explicitly, or call `AddBotDetectionInMemory()` for ephemeral runs.
- Behavioural changes to be aware of (no action needed): the single canonical classifier, Signal Assay proxy calibration, `BotType.Internal`, and the Dashboard V2 information architecture (old URLs redirect).
- Only if you wrote **custom detectors**: `IContributingDetector` became `IDetectorAtom`. See [Custom detectors](#custom-detectors-only-if-you-extended-detection).

## What is NOT changing

- **Package ID** `mostlylucid.botdetection` and all `Mostlylucid.BotDetection.*` namespaces.
- **Entry-point API**: `AddBotDetection()`, `AddStyloBot()`, `UseBotDetection()`, `UseStyloBot()`, `AddBotDetectionInMemory()` keep the same shapes.
- **SQLite** remains the FOSS store. No dependency added.
- **Signal keys** (e.g. `signature.primary`, `request.ip.is_datacenter`) and the **YAML manifest format** for detectors and policies.
- **Configuration section root** stays `BotDetection:`.

## 1. Update the package reference

```xml
<!-- Before -->
<PackageReference Include="mostlylucid.botdetection" Version="7.*" />

<!-- After -->
<PackageReference Include="mostlylucid.botdetection" Version="8.*" />
```

Repeat for any companion packages you reference (`Mostlylucid.BotDetection.UI`, `Mostlylucid.BotDetection.Api`, the LLM providers, etc.), moving each to `8.*`. The IDs and namespaces do not change, so nothing else in your code needs editing for the version bump.

## 2. SQLite databases migrate automatically

8.x adds tables and columns to the identity and detection stores, but the migration is **automatic and in place**. On first startup, `IdentitySchema.MigrateExistingTablesAsync` and the per-store schema initialisers add what is missing (for example the new `archetype_drift_metrics` and `session_echoes` tables, and identity columns such as `variance_multiplier`). Your 7.x `botdetection.db` keeps its existing data.

- **Back up your `.db` files before upgrading.** This is a routine precaution, not a required step: the migration is additive and does not drop data.
- No manual SQL, no export/import, no fresh database needed.
- The separate `fingerprints.db` (metastable identity layer) is only created when `Identity:Enabled = true`; it is dormant otherwise.

## 3. Required config check: `DatabasePath`

8.x **fails loud** if `DatabasePath` is left `null`. In 7.x a missing path could silently fall back to a default; 8.x makes the intent explicit so a persistence path is never chosen for you by accident.

- **Persistent (normal):** set an explicit path.
  ```json
  { "BotDetection": { "DatabasePath": "botdetection.db" } }
  ```
- **Ephemeral (tests / CI / stateless nodes):** call `AddBotDetectionInMemory()`, which sets `DatabasePath` to empty to signal "no SQLite files" on purpose. Do **not** leave `DatabasePath` null to get in-memory behaviour.

If your 7.x config relied on a null/absent `DatabasePath`, set one of the two above before deploying 8.x.

## 4. Behavioural changes (awareness, usually no action)

These change how detection behaves; none require code changes, but review them if you have downstream integrations.

- **Single canonical classifier.** Every surface now derives `is_bot` from `bot_probability >= Classification.BotFloor` (default `0.70`), never from a separately stored boolean. If you read a persisted `is_bot` flag or computed your own, switch to comparing `bot_probability` against `Classification.BotFloor` so your logic matches the dashboard and API.
- **Signal Assay (deployment-norm calibration).** Transport-fingerprint signals (JA3, HTTP/2 stream priority, TCP `Connection` header) that a proxy or tunnel strips before the origin are no longer scored as bot evidence. Behind Cloudflare / a TLS-terminating proxy this **removes false positives** you may have seen in 7.x. No configuration required; it self-calibrates per deployment.
- **`BotType.Internal`.** LAN and loopback traffic is now classified as `Internal`, listed and filterable on the dashboard, and **never throttled** (risk band clamped to Low). If you had custom allowances for internal IPs, you can likely remove them.
- **Adaptive learning loop.** Off by default (`Trigger.Enabled = false`), so existing deployments see no behavioural change unless you opt in.

## 5. Dashboard: V2 information architecture

The dashboard was reorganised into the V2 IA (**Traffic / Visitors / Site / Policies / Configuration**). The legacy surfaces (Overview, Activity, Sessions, Threats, Insights, Investigate) were removed and now **301-redirect** to their V2 targets.

- Update any bookmarks, links, or embeds that pointed at the old dashboard routes.
- Charts render through a locally vendored Chart.js primitive (no CDN); nothing to configure.

## 6. Custom detectors (only if you extended detection)

If you wrote your own detector against the 7.x contributor model, the v8 atom refactor renames the base contract. This does **not** affect you if you only consume the built-in detectors.

| 7.x (contributor model) | 8.x (atom model) |
|---|---|
| `Orchestration/ContributingDetectors/{Name}Contributor.cs` | `Orchestration/Atoms/{Name}Atom.cs` |
| `IContributingDetector` | `IDetectorAtom` |
| `ConfiguredContributorBase` / `ContributingDetectorBase` | `DetectorAtomBase` |
| `ContributeAsync(BlackboardState state, CancellationToken)` | `DetectAsync(SignalSink sink, string sessionId, CancellationToken)` |
| `TriggerConditions` | `RequiredSignals` |
| `GetParam<T>(name, default)` | `_configProvider.GetParameter(Name, name, default)` |
| write signals onto the contribution | `sink.Raise(SignalKeys.X, value)` on the passed `SignalSink` |

The YAML manifest (`Orchestration/Manifests/detectors/{name}.detector.yaml`), the `SignalKeys` constants, and the narrative-builder registration are otherwise the same. See `Http3FingerprintAtom` as a reference implementation, and the "Adding a New Detector" section in `CLAUDE.md`.

## PostgreSQL (new at 8.x, commercial)

8.x introduces PostgreSQL as a **commercial** persistence upgrade path. It is **not** part of a FOSS upgrade: FOSS continues to use SQLite with no changes. There is nothing to migrate, and no PostgreSQL dependency is added to the FOSS package. If you later adopt the commercial tier, its own documentation covers the SQLite-to-PostgreSQL path.

## Questions

Open an issue at https://github.com/scottgal/stylobot/issues.
