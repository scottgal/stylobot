# In-Dashboard Configuration Editor (FOSS)

**Status:** Design, not yet built
**Scope:** FOSS-tier Monaco-powered YAML + JSON editor in the StyloBot dashboard. Ships as a new "Configuration" tab. Every paid tier gets this too - they also get form-based editors on top for policy authoring, per-user targeting, and shadow-mode preview. FOSS users get the full raw-text experience and an upsell rail showing what the form UI looks like.

## Why it matters

OSS today: "edit appsettings.json or the detector YAML file, restart the app." Feels like a framework, not a product. A single in-dashboard editor that does:

1. Syntax highlighting + JSON Schema validation for YAML detector manifests
2. JSON Schema validation for `BotDetection:*` sections in appsettings.json
3. Autocomplete on every detector field, weight, parameter
4. Hot-reload on save - dashboard shows "config reloaded, UserAgent weights updated" toast within 500ms

...turns OSS from "framework with decent docs" into "product where you edit your config and see it live."

## Stack choice - Monaco vs alternatives

| Editor | Pros | Cons |
|---|---|---|
| **Monaco** (VS Code's editor) | Schema-aware autocomplete, real IntelliSense, squiggly-line linting, familiar to every developer. Ships YAML + JSON support out of box. | ~1MB gzipped (big for a dashboard page). Can lazy-load on the Configuration tab only. |
| CodeMirror 6 | Smaller (~200KB). Modular. | Schema validation requires writing a custom linter. IntelliSense is OK but not as rich. |
| Ace | Works. | Dated feel. Less active. |
| Highlight.js | Lightweight, syntax coloring only. | **View-only** - not an editor. Can't save. |

**Decision: Monaco**, lazy-loaded. Dashboard stays snappy because we only pull the editor bundle when the user clicks the Configuration tab. Every developer alive recognizes the VS Code look-and-feel; that's a trust signal for "this is a real product." JSON Schema for detector manifests gives us autocomplete on every field, inline docs, type checking - essentially a free IDE for config.

## Layout

```
 /_stylobot  →  [ Overview | Sessions | Users | Visitors | Detectors | Clusters | Configuration* | … ]
                                                                      ─────────────────
*new tab                                                              |                |
                                                                      ▼                │
┌────────────────────────────────────────────────────────────────────────────┐         │
│ Configuration                                                               │         │
│ ┌──────────────────────────────┐ ┌────────────────────────────────────────┐ │         │
│ │ Files                        │ │ ┌────────────────────────────────────┐ │ │         │
│ │                              │ │ │ [Monaco editor, YAML/JSON]        │ │ │         │
│ │ ▼ Detector manifests         │ │ │                                    │ │ │         │
│ │   ● useragent.detector.yaml  │ │ │ name: UserAgentContributor         │ │ │         │
│ │   ○ header.detector.yaml     │ │ │ priority: 10                       │ │ │         │
│ │   ○ ip.detector.yaml         │ │ │ defaults:                          │ │ │         │
│ │   ○ (27 total - search)      │ │ │   weights:                         │ │ │         │
│ │                              │ │ │     base: 1.0                      │ │ │         │
│ │ ▼ App config                 │ │ │     bot_signal: 1.5   ← autocomp.  │ │ │         │
│ │   ○ appsettings.json         │ │ │     human_signal: 1.3              │ │ │         │
│ │   ○ appsettings.Develop…json │ │ │                                    │ │ │         │
│ │                              │ │ │  (red squiggle: "missing required  │ │ │         │
│ │ ▼ Custom overrides           │ │ │   field 'confidence.bot_detected'")│ │ │         │
│ │   ● my-overrides.yaml        │ │ │                                    │ │ │         │
│ │   [+ New file]               │ │ └────────────────────────────────────┘ │ │         │
│ │                              │ │                                          │ │         │
│ └──────────────────────────────┘ │ [Diff vs saved] [Revert] [Save & reload]│ │         │
│                                  │                                          │ │         │
│                                  │ ┌────────────────────────────────────┐ │ │         │
│                                  │ │ Want per-user policy editing       │ │ │         │
│                                  │ │ without YAML? Form-based editor    │ │ │         │
│                                  │ │ ships in Startup+:                 │ │ │         │
│                                  │ │ [screenshot of form]               │ │ │         │
│                                  │ │ [Start 30-day trial]               │ │ │         │
│                                  │ └────────────────────────────────────┘ │ │         │
│                                  └────────────────────────────────────────┘ │         │
└────────────────────────────────────────────────────────────────────────────┘         │
                                                                                        │
Save → file write → FileSystemWatcher → ConfigurationWatcher → detectors refresh ───────┘
                                                                Within 500ms.
```

## File discovery

The editor sidebar lists three classes of files:

1. **Detector manifests** - the 49 YAML files bundled in `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/`. These are bundled as embedded resources in the assembly. For editing, we export them on first dashboard load to a writable directory (`{ContentRoot}/stylobot-config/detectors/`) so the customer's copies can be edited without touching the shipped defaults. Loading precedence becomes: local overrides → embedded defaults.

2. **App config** - `appsettings.json` and `appsettings.{Environment}.json` in the app's content root. Editor scopes to the `BotDetection:*` subtree - non-StyloBot sections are greyed and read-only.

3. **Custom override files** - any `*.yaml` / `*.json` file the operator creates under `{ContentRoot}/stylobot-config/`. Useful for per-customer / per-deployment layering without touching detector defaults.

## Hot-reload wiring

`Mostlylucid.BotDetection/Orchestration/Manifests/ConfigurationWatcher.cs` already exists - it currently subscribes to commercial `IConfigurationOverrideSource` changes and invalidates the `IDetectorConfigProvider` cache. Extensions needed for FOSS hot-reload:

1. Add `FileSystemConfigurationOverrideSource` in FOSS. Watches `{ContentRoot}/stylobot-config/**/*.yaml` and `{ContentRoot}/stylobot-config/**/*.json`. Emits a `ConfigurationChangeNotification` for each changed file.
2. The editor's Save action (POST `/_stylobot/api/config/save`) writes the file → OS file event → `FileSystemConfigurationOverrideSource` → `ConfigurationWatcher` → cache invalidation → next request picks up the new config.
3. SignalR beacon broadcasts `BroadcastInvalidation("config")` so the dashboard updates any config-derived view (Detector tab's weight sliders, overrides list, etc.) without the operator having to refresh.

## JSON Schema for detector manifests

Autocomplete + validation both come from a single schema. Generate it from the `DetectorManifest` C# model at build time using `NJsonSchema` or hand-authored (~200 lines). Ship as `Schema/detector-manifest.schema.json`, serve via `GET /_stylobot/api/config/schema/detector-manifest`.

Monaco config:

```typescript
monaco.languages.yaml.yamlDefaults.setDiagnosticsOptions({
  validate: true,
  hover: true,
  completion: true,
  schemas: [{
    uri: "/_stylobot/api/config/schema/detector-manifest",
    fileMatch: ["*.detector.yaml"],
  }]
});
```

Done. Autocomplete on every `weights.*`, `confidence.*`, `parameters.*` field. Inline docs on hover. Red squiggles on typos or out-of-range values.

## Save endpoint contract

```
POST /_stylobot/api/config/save
Content-Type: application/json

{
  "path": "detectors/useragent.detector.yaml",
  "content": "name: UserAgentContributor\n...",
  "format": "yaml"     // or "json"
}
```

Response:
```
200 OK
{
  "saved": true,
  "appliedAt": "2026-04-16T14:00:00Z",
  "validationWarnings": []
}
```

Validation warnings ARE NOT BLOCKING - if the YAML parses but fails schema, we still save (customer's choice), but the response includes the warnings so the editor can render a non-fatal banner. This is consistent with the rest of FOSS: we trust the operator, we don't paternalize.

Security:
- Path traversal: reject any `../` in `path`. Restrict writes to `{ContentRoot}/stylobot-config/**`.
- File size: cap at 1 MB per save. Larger config files are a smell.
- Rate limit: same dashboard auth already gates this endpoint. Add 10-req/min per IP on top.
- CSRF: `X-SB-Dashboard-Token` header required on all write endpoints (same token the rest of the dashboard emits).

## Upsell rail

In OSS, a persistent panel in the editor sidebar:

> **Editing YAML? The full story is nicer in paid tiers:**
> - Per-endpoint config via form UI, not YAML
> - Policy templates (strict-login, api-gateway, …)
> - Shadow-mode / dry-run: see how a policy change would have affected the last hour of traffic BEFORE you save it
> - Per-user / per-API-key / per-group targeting
> - Config broadcast across multi-gateway fleets in <1 second
>
> [Screenshot of form UI]
> [Start 30-day SME trial - no credit card]

Dismissable but not hidden - reappears after 7 days. Operators know there's more; they're not nagged.

## Paid tiers inherit the editor + get more

In Startup/SME/Enterprise, the Monaco editor is still there (operators who prefer YAML keep using it). On top, a "Policy Editor" sub-tab provides the form-based authoring. The Monaco editor is the escape hatch - the form covers 95% of cases cleanly and the remaining 5% drops to raw config.

Commercial tiers' Save endpoint writes to Postgres (not filesystem) and broadcasts via Redis pub/sub; same UX from the editor's perspective, different transport.

## Accessibility

Monaco is keyboard-driven out of the box. We override the default dark theme to match DaisyUI's theme tokens so it respects the operator's light/dark preference. Screen reader support is inherited from Monaco.

## Rollout

**Phase 1 (FOSS):**
- `FileSystemConfigurationOverrideSource` + file watcher integration
- Save endpoint with schema validation + traversal protection
- Monaco lazy-loaded on Configuration tab
- JSON Schema for detector manifests
- Upsell rail in OSS

**Phase 2 (Commercial):**
- Same editor, writes go to Postgres instead of filesystem
- Redis pub/sub broadcast to all gateways
- Form-based Policy Editor on a sibling sub-tab

**Phase 3 (Commercial SME+):**
- Shadow-mode / dry-run: recent-traffic simulator
- Policy templates library
- Conditional rule builder (visual, not YAML)

**Phase 4 (Enterprise):**
- Staged rollout: save in "drafted" state, deploy to gateway group A first, promote on metric match
- Signed change log with operator identity
- SCIM-sourced user selection for per-user policies