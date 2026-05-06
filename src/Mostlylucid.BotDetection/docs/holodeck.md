# Holodeck: Honeypot Response System

Instead of blocking detected bots with a 403, the holodeck intercepts their requests and serves realistic fake responses. This wastes bot resources, studies scraping behavior, and embeds HMAC canaries to track fingerprint rotation across sessions.

---

## Quick Start

```bash
dotnet add package Mostlylucid.BotDetection.ApiHolodeck
```

```csharp
// Program.cs
builder.Services.AddBotDetection();
builder.Services.AddApiHolodeck(options =>
{
    options.MockApiBaseUrl = "http://localhost:5116/api/mock";
    options.Mode = HolodeckMode.RealisticButUseless;
    options.MaxConcurrentEngagements = 10;
    options.EnableBeaconTracking = true;
});

app.UseRouting();
app.UseStyloBot(); // registers HoneypotPathTagger middleware in the correct order
```

Wire it as an action policy in appsettings:

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "holodeck": {
        "Type": "Holodeck",
        "MockApiBaseUrl": "http://localhost:5116/api/mock",
        "Mode": "RealisticButUseless",
        "MaxStudyRequests": 50
      }
    },
    "DetectionPolicies": {
      "default": {
        "Transitions": [
          { "WhenRiskExceeds": 0.6, "ActionPolicyName": "holodeck" },
          { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block" }
        ]
      }
    }
  }
}
```

---

## Architecture

The holodeck is three cooperating layers. Each has a distinct responsibility:

```
Request arrives
     │
     ▼
HoneypotPathTagger          (middleware, pre-detection)
  tags HttpContext.Items["Holodeck.IsHoneypotPath"]
     │
     ▼
BotDetectionMiddleware      (runs all 49 detectors)
  HoneypotLinkContributor   (priority 5, strong bot signal)
  BeaconContributor         (priority 2, canary scan)
     │
     ▼
HolodeckActionPolicy        (or SimulationPackResponder)
  HolodeckCoordinator       (slot management)
  SimulationPackResponder   (serves response template or LLM content)
  BeaconStore.StoreAsync    (persists canary mapping)
```

### Layer 1: HoneypotPathTagger

`HoneypotPathTagger` runs before detection. It tags honeypot paths on `HttpContext.Items` before the detection pipeline fires.

This is necessary because `FastPathReputation` can issue an early exit for known-bad fingerprints, bypassing later contributors. The tag on `HttpContext.Items` is available regardless of whether the detection pipeline runs to completion.

```csharp
// Items written by HoneypotPathTagger:
context.Items["Holodeck.IsHoneypotPath"] = true;
context.Items["Holodeck.MatchedPath"] = "/wp-login.php";
```

Matching is exact or prefix. The path is not normalized at this layer (normalization happens in `HoneypotLinkContributor`).

### Layer 2: Contributors

**`HoneypotLinkContributor`** (priority 5): Matches the request path against the built-in list of scanner paths (WordPress probes, config files, `.git`, database admin panels, cloud metadata endpoints, etc.) plus any paths configured in `HolodeckOptions.HoneypotPaths`. Normalizes the path (double URL-decode, null-byte removal, `.`/`..` resolution) before matching. An exact or prefix match returns `ConfidenceDelta = 0.95` with `Weight = 2.0` and triggers early exit.

**`BeaconContributor`** (priority 2): Scans all incoming requests for canary values from previous holodeck responses. Checks query string values, path segments, cookie values, and Referer query parameters. On a match, writes signals linking the current request to the original fingerprint.

### Layer 3: HolodeckActionPolicy / SimulationPackResponder

**`HolodeckActionPolicy`** proxies the request to a MockLLMApi sidecar. It uses `HolodeckCoordinator` to gate concurrent engagements, maintains a per-fingerprint request count for the study cutoff, and forwards shape/mode hints via custom headers.

**`SimulationPackResponder`** is the simulation-pack-native responder. It matches the path against registered simulation packs, selects the best `PackResponseTemplate`, applies realistic timing delays, embeds the HMAC canary via placeholder replacement (`{{nonce}}`, `{{api_key}}`, `{{token}}`), and optionally delegates to `IHolodeckResponder` for LLM generation.

Both policies add `X-StyloBot-Pack` and `X-StyloBot-Honeypot: true` response headers.

---

## Registration

### FOSS (static templates only)

```csharp
builder.Services.AddBotDetection();
builder.Services.AddApiHolodeck();
```

`AddApiHolodeck()` registers:
- `HolodeckActionPolicy` as `IActionPolicy`
- `HoneypotLinkContributor` as `IContributingDetector`
- `BeaconContributor` as `IContributingDetector`
- `HolodeckCoordinator` (singleton)
- `BeaconCanaryGenerator` (singleton, keyed from `BotDetectionOptions.SignatureHashKey`)
- `BeaconStore` (singleton, SQLite at `{ContentRootPath}/beacons.db`)
- `HoneypotReporter` (background service, disabled unless `ReportToProjectHoneypot = true`)
- `ShapeBuilder` for API type detection

### With LLM dynamic responses

```csharp
builder.Services.AddBotDetection();
builder.Services.AddApiHolodeck();
builder.Services.AddLlmHolodeck(); // registers IHolodeckResponder
```

`SimulationPackResponder` resolves `IHolodeckResponder` via optional constructor injection. If it is null or `IsAvailable` returns false, static templates are served instead. There is no hard dependency on LLM being configured.

---

## Configuration

All options bind from `BotDetection:Holodeck`:

```json
{
  "BotDetection": {
    "Holodeck": {
      "MockApiBaseUrl": "http://localhost:5116/api/mock",
      "Mode": "RealisticButUseless",
      "ContextSource": "Fingerprint",
      "MaxStudyRequests": 50,
      "MockApiTimeoutMs": 5000,

      "EnableHoneypotLinkDetection": true,
      "HoneypotPaths": [
        "/admin-secret",
        "/wp-login.php",
        "/wp-admin",
        "/.env",
        "/xmlrpc.php",
        "/phpmyadmin",
        "/.git/config",
        "/config.php",
        "/backup.sql",
        "/debug.php"
      ],

      "MaxConcurrentEngagements": 10,
      "MaxEngagementsPerFingerprint": 1,
      "EngagementTimeoutMs": 5000,

      "EnableBeaconTracking": true,
      "BeaconTtlHours": 24,
      "BeaconCanaryLength": 8,

      "ReportToProjectHoneypot": false,
      "ProjectHoneypotAccessKey": "",
      "MinRiskToReport": 0.85,
      "MaxReportsPerHour": 100
    }
  }
}
```

### Holodeck modes

| Mode | Behavior |
|------|----------|
| `Realistic` | Generate believable fake data |
| `RealisticButUseless` | Fake data with wrong schemas and demo values (default) |
| `Chaos` | Random errors, timeouts, inconsistencies (30% error rate) |
| `StrictSchema` | OpenAPI-schema-based structured fakes |
| `Adversarial` | Randomly mixes the above tactics |

### Context source

Determines how bots are keyed for consistent fake world state:

| Source | Description |
|--------|-------------|
| `Fingerprint` | Primary signature (most accurate, default) |
| `Ip` | Remote IP only |
| `Session` | ASP.NET session ID |
| `Combined` | IP + fingerprint |

---

## Engagement Slot Management

`HolodeckCoordinator` prevents resource exhaustion by limiting concurrent holodeck engagements:

- One active engagement per fingerprint at a time (`MaxEngagementsPerFingerprint`, default 1).
- Global cap across all fingerprints (`MaxConcurrentEngagements`, default 10).
- When either limit is hit, `TryEngage` returns false and the request falls through to a normal 403 block.
- Slots are released via `IDisposable` when the response completes, so capacity recovers immediately after each response.

```csharp
if (!_coordinator.TryEngage(contextKey, out var slot))
{
    // capacity full or fingerprint already engaged: fall through to 403
    return new ActionResult { Continue = true };
}

using (slot!)
{
    // serve fake response
}
// slot released here
```

After `MaxStudyRequests` for a given fingerprint, the holodeck hard-blocks with HTTP 403 regardless of slot availability. Set `MaxStudyRequests = 0` to disable the cutoff.

---

## Simulation Packs

A simulation pack defines a fake product installation (e.g., WordPress 5.9) that the holodeck simulates. Each pack specifies:

- Honeypot paths (with per-path confidence and weight)
- CVE modules (probe paths per CVE, with optional per-CVE response templates)
- Response templates (path glob patterns, status codes, bodies with timing profiles)
- `PromptPersonality`: system-prompt text giving the LLM domain vocabulary for this framework

```csharp
// Core types (Mostlylucid.BotDetection/SimulationPacks/)
public sealed record SimulationPack
{
    public required string Id { get; init; }
    public required string Framework { get; init; }   // "wordpress", "drupal", etc.
    public required string Version { get; init; }
    public string? PromptPersonality { get; init; }   // fed to IHolodeckResponder
    public List<PackHoneypotPath> HoneypotPaths { get; init; }
    public List<PackResponseTemplate> ResponseTemplates { get; init; }
    public List<PackCveModule> CveModules { get; init; }
    public PackTimingProfile TimingProfile { get; init; }
}

public sealed record PackResponseTemplate
{
    public required string PathPattern { get; init; } // glob, e.g. "/wp-login.php"
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "text/html";
    public required string Body { get; init; }        // static body or LLM prompt
    public bool Dynamic { get; init; }                // true: delegate to IHolodeckResponder
    public PackResponseHints? ResponseHints { get; init; }
    public int MinDelayMs { get; init; }
    public int MaxDelayMs { get; init; } = 100;
}
```

The FOSS product ships a WordPress simulation pack. Additional packs (Drupal, Magento, Laravel) are commercial.

---

## LLM Integration

`IHolodeckResponder` is the interface for dynamic response generation:

```csharp
public interface IHolodeckResponder
{
    Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default);

    bool IsAvailable { get; }
}
```

`HolodeckRequestContext` provides the request method, path, query string, content type, fingerprint, and pack metadata (`PackId`, `PackFramework`, `PackVersion`, `PackPersonality`). The LLM receives the template's `Body` as its prompt, the `ResponseHints` for format/schema guidance, and the pack's `PromptPersonality` for framework vocabulary.

`PackResponseHints` carries:
- `EndpointDescription`: what this endpoint represents (e.g., "WordPress REST API user list")
- `ResponseFormat`: `json`, `xml`, `html`, `plaintext`, `php`
- `BodySchema`: sample structure or layout description
- `ExploitFlow`: multi-step exploit sequence for maintaining session coherence
- `ProductContext`: framework version, plugins, PHP version, etc.
- `ErrorTemplate`: what to return for malformed exploit payloads

When `IHolodeckResponder.IsAvailable` is false, `SimulationPackResponder` falls back to the static `Body` from the template. The canary is still embedded via placeholder replacement in the static path.

---

## Beacon and Canary Tracking

The beacon system detects fingerprint rotation by confirmed bots.

### How it works

1. When `SimulationPackResponder` serves a fake response, it computes an HMAC canary:
   ```csharp
   // BeaconCanaryGenerator (HMAC-SHA256 keyed from BotDetectionOptions.SignatureHashKey)
   string canary = _canaryGenerator.Generate(fingerprint, path);
   // e.g., "a3f7b2c1"  (8 hex chars by default)
   ```

2. The canary is embedded in static responses via placeholder replacement:
   - `{{nonce}}`, `{{api_key}}`, `{{token}}` are all replaced with the same canary value.

3. The mapping `canary -> fingerprint` is stored in SQLite (`beacons.db`), indexed by expiry:
   ```csharp
   await _beaconStore.StoreAsync(canary, fingerprint, path, packId, TimeSpan.FromHours(24));
   ```

4. On all subsequent requests, `BeaconContributor` (priority 2) scans query string values, path segments, cookie values, and Referer query parameters for any 8-character value that matches a stored canary.

5. On a match, it writes signals to the blackboard:
   - `beacon.matched` = `true`
   - `beacon.original_fingerprint` = original fingerprint string
   - `beacon.canary` = matched canary value
   - `beacon.path` = path the canary was served on
   - `beacon.age_seconds` = seconds since the canary was issued
   - `beacon.pack_id` = simulation pack ID (if present)

Entity resolution uses `beacon.original_fingerprint` to link the rotated fingerprint back to the original, connecting behavioral chains across what would otherwise appear to be separate visitors.

### Key files

| File | Role |
|------|------|
| `SimulationPacks/ICanaryGenerator.cs` | Interface: `Generate(fingerprint, path) -> string` |
| `SimulationPacks/IBeaconStore.cs` | Interface: `StoreAsync(canary, fingerprint, path, packId, ttl)` |
| `ApiHolodeck/Services/BeaconCanaryGenerator.cs` | HMAC-SHA256 implementation, deterministic per fingerprint+path |
| `ApiHolodeck/Services/BeaconStore.cs` | SQLite implementation with `LookupAsync` and `BatchLookupAsync` |
| `ApiHolodeck/Contributors/BeaconContributor.cs` | Detector that scans requests and writes beacon signals |

---

## Signals Emitted

### BeaconContributor

| Signal | Type | Meaning |
|--------|------|---------|
| `beacon.matched` | `bool` | A stored canary was found in this request |
| `beacon.original_fingerprint` | `string` | Fingerprint that received the canary response |
| `beacon.canary` | `string` | The matched canary value |
| `beacon.path` | `string` | Path the canary was originally served on |
| `beacon.age_seconds` | `double` | Age of the canary at match time |
| `beacon.pack_id` | `string` | Simulation pack that generated the canary |

### HoneypotLinkContributor

Writes signals on the contribution's `Signals` dictionary (not top-level blackboard signals):

| Signal | Meaning |
|--------|---------|
| `HoneypotTriggered` | Path matched the honeypot list |
| `HoneypotPath` | Normalized path that matched |
| `HoneypotMatchType` | `exact` or `prefix` |
| `SuspiciousExtension` | Extension that triggered detection (e.g., `.sql`) |
| `FollowedHoneypotLink` | Request came via Referer from a honeypot page |

---

## Testing

Response headers on holodeck-served responses:

| Header | Value | Source |
|--------|-------|--------|
| `X-StyloBot-Pack` | Pack ID, e.g., `wordpress-foss` | `SimulationPackResponder` |
| `X-StyloBot-Honeypot` | `true` | `SimulationPackResponder` |
| `X-Holodeck` | `true` | `HolodeckActionPolicy` |
| `X-Holodeck-Context` | Fingerprint context key | `HolodeckActionPolicy` |
| `X-Holodeck-Memory` | Consistent memory name for this bot | `HolodeckActionPolicy` |
| `X-Holodeck-Shape` | Detected API shape (e.g., `rest-json`) | `HolodeckActionPolicy` |

To verify canary embedding, inspect the static response body for the canary value. The same fingerprint hitting the same path always produces the same canary (deterministic HMAC), so you can reproduce it:

```csharp
var generator = new BeaconCanaryGenerator(secret: "your-signature-key", canaryLength: 8);
var canary = generator.Generate(fingerprint: "abc123", path: "/wp-login.php");
// canary = "a3f7b2c1" (repeatable)
```

To verify rotation detection, use `BeaconStore.LookupAsync(canary)` directly in tests or call the existing `BatchLookupAsync` path through `BeaconContributor`.

---

## Threat Intelligence Reporting

`HoneypotReporter` is a background service that queues high-confidence detections for external submission. It is disabled by default:

```json
{
  "BotDetection": {
    "Holodeck": {
      "ReportToProjectHoneypot": true,
      "ProjectHoneypotAccessKey": "your-key",
      "MinRiskToReport": 0.85,
      "MaxReportsPerHour": 100,
      "ReportVisitorTypes": ["Harvester", "CommentSpammer", "Suspicious"]
    }
  }
}
```

The reporter subscribes to `ILearningEventBus` for `HighConfidenceDetection` and `FullDetection` events, filters by `MinRiskToReport`, skips local IPs (RFC 1918 + loopback + link-local), classifies the visitor type from detection metadata, and processes reports in batches of 10 per minute.

Note: Project Honeypot does not expose a direct submission HTTP API. The reporter logs structured data suitable for integration with AbuseIPDB or a custom threat intelligence webhook. Extend `SubmitReportAsync` in `HoneypotReporter` for your target platform.
