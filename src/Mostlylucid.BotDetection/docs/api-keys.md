# API Key System

StyloBot provides two API key mechanisms for trusted callers: **legacy bypass keys** (simple, complete bypass) and **rich API keys** (fine-grained detection policy overlays). Rich API keys allow per-key control over which detectors run, weight overrides, action policies, path permissions, time windows, and rate limits.

## Quick Start

```json
{
  "BotDetection": {
    "ApiKeys": {
      "SB-CI-PIPELINE": {
        "Name": "CI Pipeline",
        "DisabledDetectors": ["BehavioralWaveform", "Behavioral"],
        "ActionPolicyName": "logonly",
        "RateLimitPerMinute": 200
      }
    }
  }
}
```

Callers pass the key via the `X-SB-Api-Key` header:

```bash
curl -H "X-SB-Api-Key: SB-CI-PIPELINE" https://example.com/api/data
```

> **Convention**: Key values use the `SB-` prefix (e.g., `SB-DASHBOARD-MONITOR`, `SB-K6-BYPASS`) to make them visually distinct from other headers and easy to grep in logs. The header `X-SB-Api-Key` uses the vendor `SB` prefix per [RFC 6648](https://datatracker.ietf.org/doc/html/rfc6648) conventions.

## Gateway API access authorization (`/api/v1/*`)

The same `BotDetection:ApiKeys` surface is **the access-authorization mechanism for the gateway API**. Every `/api/v1/*` read endpoint (`/detect`, `/detections`, `/summary`, `/timeseries`, `/topbots`, `/countries`, `/endpoints`, …) is registered with `.RequireAuthorization(...)` against the `X-SB-Api-Key` scheme, so a caller **must present a valid configured key to read the API**. This is a first-class authorization gate — a proper `RequireAuthenticatedUser()` policy — not a side effect of the detection-bypass overlay.

**Default is fail-closed (locked).** When `BotDetection:ApiKeys` is empty, or a request presents no/invalid key, the API returns **`401 Unauthorized`**. There is no "open when unconfigured" mode. To *use* the gateway API you must configure at least one key.

> The marketing/remote dashboard viewer (`Stylobot.Ui` in `rest` mode, `RemoteDashboardEventStore`) reads `/api/v1/*` with the key from `StyloBot:Source:Pull:ApiKey`. That value **must match a configured `BotDetection:ApiKeys` entry**, or every pull 401s.

**Access authorization is distinct from detection bypass.** Holding a valid key proves *"you may call this endpoint"*. Whether that traffic *also* bypasses detection is a **separate, opt-in** per-key concern controlled by `DisableLearningWrites` (default `false`) and `DisabledDetectors`. A key with `DisableLearningWrites: false` (the default) authorizes API access **without** bypassing detection.

### Minimal: lock the API down with a single fixed key

```json
{
  "BotDetection": {
    "ApiKeys": {
      "SB-API-ACCESS": {
        "Name": "Gateway API access",
        "DisableLearningWrites": false
      }
    }
  }
}
```

The dictionary key (`SB-API-ACCESS`) is the secret; send it as `X-SB-Api-Key`. Because `DisableLearningWrites` is `false`, this key authorizes access but detection still runs normally on that traffic. Store it as a secret/env var — never commit it (see [Environment Variables](#configuration-via-environment-variables) and [Security Considerations](#security-considerations)).

## Two Key Types

### Legacy Bypass Keys

Complete detection bypass. No detectors run, no scoring, no action policies.

```json
{
  "BotDetection": {
    "ApiBypassKeys": ["SB-LEGACY-KEY-1", "SB-LEGACY-KEY-2"]
  }
}
```

- Configured via `BotDetection.ApiBypassKeys` (string array), gated by `BotDetection.EnableLegacyApiBypassKeys` (default `false`)
- Header name: `X-SB-Api-Key` (configurable via `ApiBypassHeaderName`)
- No validation beyond key matching (no expiry, no rate limits)

> **Currently non-functional.** `ApiBypassKeys` is not consulted by any request-handling code path today — configuring it has no runtime effect (startup only warns if it's set without `EnableLegacyApiBypassKeys`). The intended mechanism (an upstream check sets `HttpContext.Items["BotDetection.ApiKeyBypass"] = true`, which `BotEndpointFilter` then honours) is wired on the *consuming* side but nothing sets that flag. See [Current Limitations](#current-limitations) below.

### Rich API Keys

Detection policy overlays with full lifecycle management.

```json
{
  "BotDetection": {
    "ApiKeys": {
      "SB-DASHBOARD-MONITOR": {
        "Name": "Dashboard Monitor",
        "Description": "Used by the dashboard polling service",
        "Enabled": true,
        "DisabledDetectors": ["BehavioralWaveform", "Behavioral"],
        "WeightOverrides": { "UserAgent": 0.5 },
        "ActionPolicyName": "logonly",
        "AllowedPaths": ["/_stylobot/**"],
        "RateLimitPerMinute": 120,
        "Tags": ["monitoring", "dashboard"]
      }
    }
  }
}
```

The dictionary key is the secret value callers send in `X-SB-Api-Key`. The value is an `ApiKeyConfig` object.

## ApiKeyConfig Reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | **required** | Human-readable name for logging/auditing |
| `Description` | string? | null | Optional description |
| `Enabled` | bool | true | Set false to revoke without removing |
| `AllowedPaths` | string[] | [] (all) | Glob patterns for allowed paths |
| `DeniedPaths` | string[] | [] (none) | Glob patterns for denied paths (checked first) |
| `DisabledDetectors` | string[] | [] (none) | Detector names to skip. Use `["*"]` to disable all |
| `WeightOverrides` | dict<string,double> | {} | Per-detector weight multipliers |
| `DetectionPolicyName` | string? | null | Override the path-resolved detection policy |
| `ActionPolicyName` | string? | null | Override the action policy (e.g., `"logonly"`) |
| `ExpiresAt` | DateTimeOffset? | null | Expiration timestamp. Null = never |
| `AllowedTimeWindow` | string? | null | UTC time window, format `"HH:mm-HH:mm"` |
| `RateLimitPerMinute` | int | 0 (unlimited) | Per-key sliding window rate limit |
| `RateLimitPerHour` | int | 0 (unlimited) | Per-key sliding window rate limit |
| `Tags` | string[] | [] | Metadata tags for categorization |
| `BoundIdentity` | string? | null | Default impersonation target signature when the caller doesn't send `X-SB-Impersonate` (requires `AllowImpersonation`) |

## Detection Pipeline Flow

When a request arrives with an `X-SB-Api-Key` header, `ApiKeyContextMiddleware` validates it via
`IApiKeyStore` and, if valid, stores an `ApiKeyContext` in `HttpContext.Items["BotDetection.ApiKeyContext"]`
(readable from your own code via `context.GetApiKeyContext()` / `context.HasApiKey()`). Detection then runs
as normal downstream — see [Current Limitations](#current-limitations) for what the key's fields do and
don't currently change about that run.

`DetectionPolicy.WithApiKeyOverlay(ApiKeyContext)` exists to merge `WeightOverrides`, apply
`DetectionPolicyName`, and set `ExcludedDetectors` from `DisabledDetectors` onto the resolved policy, but it
has no call site today — policy resolution never applies it. `ActionPolicyName` is stored on the context but
nothing reads it to override the action policy either. If your integration needs this overlay behavior now,
call `WithApiKeyOverlay` yourself against the policy your app resolves (e.g. in middleware placed after
`UseApiKeyContext()` and before `UseBotDetection()`).

## Validation Checks

The `InMemoryApiKeyStore` validates keys in this order:

1. **Key exists** - constant-time comparison (prevents timing attacks)
2. **Enabled** - `config.Enabled == true`
3. **Not expired** - `ExpiresAt == null || ExpiresAt > now`
4. **Time window** - current UTC time within `AllowedTimeWindow`
5. **Path allowed** - request path matches `AllowedPaths` globs
6. **Path not denied** - request path does not match `DeniedPaths` globs
7. **Rate limit** - sliding window counters for per-minute and per-hour limits

`IApiKeyStore.ValidateKeyWithReason` computes a rejection reason (`ApiKeyRejection`) for each of the checks
above, but `ApiKeyContextMiddleware` currently discards it rather than exposing it on `HttpContext.Items` --
there is no diagnostics surface for *why* a key was rejected today, only whether `GetApiKeyContext()` is
null.

### Path Matching

- `/**` or `**` - matches all paths
- `/api/**` - matches any path starting with `/api/`
- `/admin/*` - matches single segment under `/admin/`
- Empty `AllowedPaths` - all paths allowed
- `DeniedPaths` are evaluated first (deny takes priority)

## Configuration via Environment Variables

For Docker Compose or container environments, use the `__` separator:

```yaml
# Legacy bypass key
- BotDetection__ApiBypassKeys__0=SB-LEGACY-BYPASS

# Rich API key (key value is part of the config path)
- BotDetection__ApiKeys__SB-CI-PIPELINE__Name=CI Pipeline
- BotDetection__ApiKeys__SB-CI-PIPELINE__DisabledDetectors__0=Behavioral
- BotDetection__ApiKeys__SB-CI-PIPELINE__DisabledDetectors__1=BehavioralWaveform
- BotDetection__ApiKeys__SB-CI-PIPELINE__ActionPolicyName=logonly
- BotDetection__ApiKeys__SB-CI-PIPELINE__RateLimitPerMinute=200
- BotDetection__ApiKeys__SB-CI-PIPELINE__Tags__0=ci
```

Using env var indirection for the key value itself:

```yaml
- BotDetection__ApiKeys__${MY_API_KEY:-SB-DEFAULT}__Name=My Service
```

## HttpContext Access

```csharp
// Check if request has any API key (legacy or rich)
bool hasKey = context.HasApiKey();

// Get the rich API key context (null for legacy keys or no key)
ApiKeyContext? keyCtx = context.GetApiKeyContext();

// Check specific properties
if (keyCtx != null)
{
    string name = keyCtx.KeyName;
    bool allDisabled = keyCtx.DisablesAllDetectors;
    IReadOnlyList<string> disabled = keyCtx.DisabledDetectors;
    IReadOnlyList<string> tags = keyCtx.Tags;
}
```

## Current Limitations

The following `ApiKeyConfig` fields are validated, stored on `ApiKeyContext`, and readable via
`context.GetApiKeyContext()`, but have **no effect on detection today** -- policy resolution never applies
them (see [Detection Pipeline Flow](#detection-pipeline-flow)):

- `DisabledDetectors` (except the special `["*"]` value, which is validated at startup but -- like legacy
  `ApiBypassKeys` -- also isn't consulted by any runtime code path; see the note under
  [Legacy Bypass Keys](#legacy-bypass-keys))
- `WeightOverrides`
- `DetectionPolicyName`
- `ActionPolicyName`

What does work today: key validation itself (`Enabled`, `ExpiresAt`, `AllowedTimeWindow`, `AllowedPaths` /
`DeniedPaths`, rate limiting), `GetApiKeyContext()` / `HasApiKey()` visibility to your own code,
`AllowImpersonation` / `BoundIdentity`, and `DisableLearningWrites`. The patterns below that rely on the
non-functional fields describe the *intended* effect once `WithApiKeyOverlay` is wired into policy
resolution -- until then, treat them as configuration you can safely add now (it'll take effect when the
overlay ships) rather than behavior you can rely on.

## Common Patterns

### Dashboard Monitoring Key

Disables behavioral detectors (dashboard polling is repetitive and would trigger false positives) -- see
[Current Limitations](#current-limitations), `DisabledDetectors` isn't wired into detection yet:

```json
{
  "SB-DASHBOARD-MONITOR": {
    "Name": "Dashboard Monitor",
    "DisabledDetectors": ["BehavioralWaveform", "Behavioral", "AdvancedBehavioral"],
    "AllowedPaths": ["/_stylobot/**"],
    "RateLimitPerMinute": 120,
    "Tags": ["monitoring"]
  }
}
```

### CI/CD Pipeline Key

All detectors enabled but action set to log-only -- see [Current Limitations](#current-limitations),
`ActionPolicyName` isn't wired into policy resolution yet:

```json
{
  "SB-CI-PIPELINE": {
    "Name": "CI Pipeline",
    "ActionPolicyName": "logonly",
    "RateLimitPerMinute": 600,
    "Tags": ["ci", "testing"]
  }
}
```

### Latency Baseline Key

Disables all detectors to measure raw proxy overhead -- see [Current Limitations](#current-limitations),
requires `AllowFullDetectorBypassApiKeys: true` at the top level of `BotDetection` config (startup validation
error otherwise), and note that the actual bypass isn't wired into the request pipeline yet either:

```json
{
  "SB-BASELINE": {
    "Name": "Latency Baseline",
    "DisabledDetectors": ["*"],
    "RateLimitPerMinute": 600,
    "Tags": ["testing", "baseline"]
  }
}
```

### Business Hours Only Key

Restrict key usage to UTC business hours with expiration:

```json
{
  "SB-CONTRACTOR-ACCESS": {
    "Name": "External Contractor",
    "AllowedTimeWindow": "09:00-17:00",
    "ExpiresAt": "2026-06-30T23:59:59Z",
    "AllowedPaths": ["/api/**"],
    "DeniedPaths": ["/api/admin/**"],
    "RateLimitPerMinute": 60
  }
}
```

## k6 Load Testing with API Keys

The demo stack (`docker-compose.demo.yml`) ships with four pre-configured API keys for load testing:

| Key (env var) | Default Value | Purpose |
|---------------|---------------|---------|
| `BOTDETECTION_DASHBOARD_API_KEY` | `SB-DASHBOARD-MONITOR` | Dashboard polling, behavioral detectors disabled |
| `BOTDETECTION_FULL_DETECTION_KEY` | `SB-K6-FULL-DETECTION` | ALL detectors enabled, logonly action |
| `BOTDETECTION_BYPASS_KEY` | `SB-K6-BYPASS` | ALL detectors disabled, latency baseline |
| `BOTDETECTION_NO_BEHAVIORAL_KEY` | `SB-K6-NO-BEHAVIORAL` | Behavioral detectors disabled, fingerprint-only |

The k6 test (`k6-soak.js`) uses these keys via the `X-SB-Api-Key` header across scenarios:

```bash
# Run with default keys
k6 run k6-soak.js

# Override keys via env vars
k6 run -e FULL_DETECTION_KEY=SB-MY-CUSTOM-KEY k6-soak.js

# Verbose mode (logs which detectors fired)
k6 run -e K6_VERBOSE=1 k6-soak.js
```

### k6 Scenarios

| Scenario | API Key | What It Tests |
|----------|---------|---------------|
| `human_browsing` | none | Real browser-like traffic, should NOT be flagged |
| `bot_scraping` | none | Known bot UAs, should be detected |
| `attack_traffic` | none | SQLi/XSS/path probes, should be blocked |
| `credential_stuffing` | none | Login brute force, should trigger AccountTakeover |
| `dashboard_polling` | `SB-DASHBOARD-MONITOR` | Dashboard APIs work with behavioral detectors off |
| `full_detection_test` | `SB-K6-FULL-DETECTION` | Verifies ALL detectors fire, measures detector count |
| `bypass_baseline` | `SB-K6-BYPASS` | Zero detection overhead, measures raw proxy latency |
| `no_behavioral_test` | `SB-K6-NO-BEHAVIORAL` | Confirms behavioral detectors are excluded |

## Security Considerations

### Key Storage

API keys are **bearer tokens** - anyone who knows a key value can send it via `X-SB-Api-Key` to bypass or alter detection. Treat them like passwords:

- **Never commit real key values to source control.** The `SB-*` defaults in `docker-compose.demo.yml` and `.env.example` are insecure placeholders for local development only. They are not used in production.
- **Generate production keys with strong randomness**: `openssl rand -hex 32` or equivalent.
- **Store keys in `.env` (gitignored), Docker Secrets, Vault, or your platform's secrets manager** - never in compose files, appsettings.json checked into git, or environment variable defaults.
- **Scope keys narrowly**: use `AllowedPaths`, `DeniedPaths`, `RateLimitPerMinute`, and `ExpiresAt` to limit blast radius if a key leaks.

### Intended Use

API keys are designed for **your own automation tooling** - CI/CD pipelines, load testing (k6), monitoring dashboards, and dev scripts that need to interact with protected endpoints without triggering false positives. They are not used for production system-to-system communication within the StyloBot infrastructure itself.

### Demo vs Production

| | Demo (`docker-compose.demo.yml`) | Production |
|---|---|---|
| **Key values** | Hardcoded `SB-*` placeholders | Cryptographically random secrets |
| **Storage** | Inline defaults in compose file | `.env` / secrets manager |
| **Network** | Local Docker network only | Internet-facing, TLS required |
| **k6 test keys** | Present (full-detection, bypass, no-behavioral) | Remove or disable after testing |

### Implementation

- **Constant-time comparison**: Key validation uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks
- **Key rotation**: Set `Enabled = false` to revoke, then add a new key. No restart required if using options reload
- **Rate limiting**: Per-key sliding window counters are thread-safe and independent of bot detection rate limiting
- **Path isolation**: `DeniedPaths` are checked before `AllowedPaths` (deny takes priority)
- **No key in logs**: The key value itself is never logged; only `KeyName` appears in diagnostics
- **Vendor-prefixed header**: `X-SB-Api-Key` avoids collision with generic `X-Api-Key` headers from other middleware