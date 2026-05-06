# Simulation Packs

Simulation packs are named bundles of honeypot paths and fake response templates that simulate a vulnerable software installation. When a bot probes a path covered by a loaded pack, StyloBot serves a realistic fake response instead of a 404, luring the bot into deeper engagement and generating richer behavioral signals. The FOSS product ships with a WordPress pack. Commercial packs cover additional frameworks.

---

## Quick Start

```csharp
// Detection only (packs loaded automatically with AddBotDetection)
builder.Services.AddBotDetection();
app.UseBotDetection();

// Detection + holodeck response serving + beacon tracking
builder.Services.AddBotDetection();
builder.Services.AddApiHolodeck(options =>
{
    options.MaxConcurrentEngagements = 10;
    options.EnableBeaconTracking = true;
});
app.UseRouting();
app.UseStyloBot(); // or app.UseBotDetection()
```

Simulation packs are embedded YAML resources in `Mostlylucid.BotDetection`. They load automatically at startup via `SimulationPackLoader`. No extra registration is required for detection. The `AddApiHolodeck()` call adds the holodeck response layer, beacon tracking, and the `HoneypotPathTagger` middleware.

### Configuration

```json
{
  "BotDetection": {
    "Holodeck": {
      "MaxConcurrentEngagements": 10,
      "MaxEngagementsPerFingerprint": 1,
      "EnableBeaconTracking": true,
      "BeaconCanaryLength": 8,
      "BeaconTtlHours": 24,
      "HoneypotPaths": [
        "/wp-login.php",
        "/wp-admin",
        "/.env",
        "/xmlrpc.php"
      ]
    }
  }
}
```

---

## How Detection Works

Pack loading and path matching are handled by `SimulationPackLoader`, which implements `ISimulationPackRegistry`. At startup, it scans for embedded YAML resources matching the prefix `Mostlylucid.BotDetection.SimulationPacks.Packs.` and deserializes each into a `SimulationPack` record. All honeypot and CVE probe paths across all packs are flattened into a single lookup list for fast per-request matching.

The `CveProbeContributor` (Priority 11, Wave 0) runs on every request and calls `IsHoneypotPath()` against the registry. When a match is found, it writes signals to the blackboard and adds a `DetectionContribution.Bot(...)` with confidence scaled to severity. It also sets `action.trigger_policy` to `"simulation-pack"` for paths with confidence >= 0.7, which routes the request to `SimulationPackResponder`.

Path matching uses `FileSystemName.MatchesSimpleExpression` (case-insensitive glob). CVE probe paths resolve with confidence derived from the module's severity field; honeypot paths use the per-path `confidence` value from the YAML definition.

The `HoneypotPathTagger` middleware runs before detection and sets `HttpContext.Items["Holodeck.IsHoneypotPath"]`. This ensures the tag is available even if early-exit fast-path reputation prevents the contributor from running.

---

## Pack Architecture

### SimulationPack

The top-level record representing one fake installation:

| Field | Type | Purpose |
|-------|------|---------|
| `Id` | `string` | Unique identifier, e.g. `"wordpress-5.9"` |
| `Name` | `string` | Human-readable label |
| `Framework` | `string` | e.g. `"wordpress"`, `"drupal"` |
| `Version` | `string` | Simulated version string |
| `Description` | `string?` | Optional description |
| `PromptPersonality` | `string?` | System prompt additions for LLM generation |
| `HoneypotPaths` | `List<PackHoneypotPath>` | Paths that trigger detection |
| `ResponseTemplates` | `List<PackResponseTemplate>` | Response templates keyed by path pattern |
| `CveModules` | `List<PackCveModule>` | CVE-specific probe modules |
| `TimingProfile` | `PackTimingProfile` | Response delay range for realism |

### PackHoneypotPath

Defines a single matchable path and its detection weight:

```yaml
- path: /wp-config.php*
  confidence: 0.95
  weight: 2.5
  category: wordpress-config
```

- `path`: glob pattern matched with `FileSystemName.MatchesSimpleExpression`
- `confidence`: delta applied to the bot confidence score (0.0-1.0)
- `weight`: multiplier for the detection contribution
- `category`: label for grouping in the dashboard

### PackResponseTemplate

Controls what the responder serves when a path matches:

```yaml
- path_pattern: /wp-login.php
  status_code: 200
  content_type: "text/html; charset=UTF-8"
  min_delay_ms: 200
  max_delay_ms: 600
  headers:
    X-Powered-By: "PHP/7.4.33"
    X-Frame-Options: SAMEORIGIN
  body: |
    <!DOCTYPE html>...
```

Set `dynamic: true` to use LLM generation instead of the static `body` field. See [Template Types](#template-types) below.

### PackCveModule

A CVE-specific module with probe paths and an optional dedicated response template:

```yaml
- cve_id: CVE-2024-6386
  severity: critical
  description: WPML plugin remote code execution via Twig SSTI
  affected_versions: ["5.8", "5.9", "6.0"]
  probe_paths:
    - /wp-admin/admin-ajax.php
  probe_response:
    path_pattern: /wp-admin/admin-ajax.php
    status_code: 200
    content_type: application/json
    dynamic: true
    body: '{"success":false,"data":"0"}'
    response_hints:
      endpoint_description: "WordPress admin-ajax.php handler"
      exploit_flow: "Step 1: POST with action=wpml_action..."
```

The `probe_response` template takes priority over pack-level response templates when both match.

### PackTimingProfile

```yaml
timing_profile:
  min_response_ms: 150
  max_response_ms: 800
  jitter_ms: 100
```

See [Timing Profiles](#timing-profiles) below.

### YAML Top-Level Structure

```yaml
id: <string>
name: <string>
framework: <string>
version: <string>
description: <string>
prompt_personality: <string>

timing_profile:
  min_response_ms: <int>
  max_response_ms: <int>
  jitter_ms: <int>

honeypot_paths:
  - path: <glob>
    confidence: <float>
    weight: <float>
    category: <string>

response_templates:
  - path_pattern: <glob>
    status_code: <int>
    content_type: <string>
    min_delay_ms: <int>
    max_delay_ms: <int>
    dynamic: <bool>
    headers:
      <HeaderName>: <value>
    body: <string>
    response_hints:
      endpoint_description: <string>
      response_format: <string>
      body_schema: <string>
      expected_methods: [<string>]
      exploit_flow: <string>
      product_context:
        <key>: <value>
      error_template: <string>

cve_modules:
  - cve_id: <string>
    severity: critical|high|medium|low
    description: <string>
    affected_versions: [<string>]
    probe_paths:
      - <glob>
    probe_response:
      <PackResponseTemplate fields>
```

---

## The WordPress Pack

The FOSS reference pack. File: `SimulationPacks/Packs/wordpress.yaml`, pack ID `wordpress-5.9`.

Simulates an unpatched WordPress 5.9 installation with PHP/7.4.33 response headers and version-accurate page content.

### Honeypot Paths

| Path | Confidence | Category |
|------|-----------|---------|
| `/wp-config.php*` | 0.95 | wordpress-config |
| `/wp-admin/*` | 0.90 | wordpress-admin |
| `/xmlrpc.php` | 0.90 | wordpress-xmlrpc |
| `/wp-login.php` | 0.85 | wordpress-auth |
| `/wp-json/wp/v2/users` | 0.80 | wordpress-api |
| `/wp-cron.php` | 0.70 | wordpress-cron |
| `/wp-includes/*` | 0.70 | wordpress-core |
| `/wp-content/plugins/*` | 0.75 | wordpress-plugins |
| `/readme.html` | 0.65 | wordpress-fingerprint |
| `/wp-content/uploads/*` | 0.60 | wordpress-uploads |
| `/wp-content/themes/*/style.css` | 0.55 | wordpress-theme-enum |

### Response Templates

The pack serves tailored responses per path:

- `/wp-login.php`: Full HTML login form (200), realistic `loginform` markup, with `X-Powered-By: PHP/7.4.33`
- `/xmlrpc.php`: XML-RPC "POST only" response (200), dynamic-capable with exploit flow hints
- `/wp-json/wp/v2/users`: JSON user list with three fake accounts (admin, editor, subscriber)
- `/wp-admin/*`: 302 redirect to `/wp-login.php?redirect_to=%2Fwp-admin%2F&reauth=1`
- `/readme.html`: HTML page disclosing "Version 5.9"
- `/wp-content/plugins/*`: Fake directory index listing akismet, contact-form-7, wpml-multilingual-cms
- `/wp-config.php*`: Fake PHP config file with plausible-looking credentials and salts

Paths with no matching template receive a framework-appropriate 404 page with `X-Powered-By: PHP/7.4.33`.

### CVE Modules

| CVE ID | Severity | Description |
|--------|---------|-------------|
| CVE-2017-5487 | Medium | WordPress REST API user enumeration |
| CVE-2021-34474 / CVE-2022-21661 | High | WP_Query SQL injection |
| CVE-2023-2982 | Critical | miniOrange Social Login authentication bypass |
| CVE-2023-6553 | Critical | Backup Migration plugin RCE |
| CVE-2023-32243 | Critical | Essential Addons for Elementor privilege escalation |
| CVE-2024-2876 | Critical | Icegram Express SQL injection |
| CVE-2024-6386 | Critical | WPML plugin Twig SSTI remote code execution |
| CVE-2024-27956 | Critical | WP-Automatic SQL injection |

---

## Path Matching

All patterns use `FileSystemName.MatchesSimpleExpression` (case-insensitive). Supported glob syntax:

- `*` matches any sequence of characters within a path segment
- `?` matches any single character
- Trailing `*` matches any suffix: `/wp-config.php*` matches `/wp-config.php.bak`
- Prefix wildcards: `/wp-content/plugins/*` matches any plugin subdirectory

CVE probe path confidence is derived from module severity:

| Severity | Confidence | Weight |
|---------|-----------|--------|
| Critical | 0.95 | 2.5 |
| High | 0.90 | 2.5 |
| Medium | 0.80 | 2.5 |
| Low / unknown | 0.75 | 2.5 |

Honeypot path confidence and weight come directly from the YAML `confidence` and `weight` fields.

`FindResponseTemplate()` checks CVE `probe_response` templates before pack-level templates. The first match wins.

---

## Template Types

### Static Templates

The default. `SimulationPackResponder` writes the `body` field directly to the HTTP response. Canary placeholders are substituted before writing:

- `{{nonce}}` - replaced with the HMAC canary for this fingerprint+path
- `{{token}}` - same canary value
- `{{api_key}}` - same canary value

Example (from wp-config.php template):

```yaml
body: |
  <?php
  define( 'DB_PASSWORD', 's3cur3_p4ssw0rd_2024!' );
  define( 'AUTH_KEY', '{{nonce}}' );
```

When the canary generator is not registered or no fingerprint is available, placeholders are left unreplaced.

### Dynamic Templates

Set `dynamic: true` on the template. The responder calls `IHolodeckResponder.GenerateAsync()` when available, passing the template and `HolodeckRequestContext` (method, path, fingerprint, pack ID, framework, version, `PromptPersonality`). The `response_hints` block provides the LLM with:

- `endpoint_description`: what this endpoint represents
- `response_format`: `json`, `xml`, `html`, `plaintext`, `php`
- `body_schema`: structure or sample of the expected response body
- `expected_methods`: HTTP methods the endpoint accepts
- `exploit_flow`: multi-step abuse sequence to guide sequential request handling
- `product_context`: version-specific context (PHP version, plugin version, etc.)
- `error_template`: response to return when the bot sends invalid payloads

If `IHolodeckResponder` is not registered or `IsAvailable` is false, the static `body` field is used as the fallback. Dynamic templates always have a static fallback.

Register an LLM responder with `AddLlmHolodeck()` (commercial) or implement `IHolodeckResponder` directly.

---

## Timing Profiles

Each pack defines a `timing_profile` that controls response delay to mimic realistic server latency and prevent bots from fingerprinting the honeypot via response time analysis.

```yaml
timing_profile:
  min_response_ms: 150
  max_response_ms: 800
  jitter_ms: 100
```

`SimulationPackResponder` picks a random delay in `[min, max]` before writing the response. If the matched template also specifies `min_delay_ms`/`max_delay_ms`, those values are used as a floor: the delay is `Random.Next(Max(template.min, profile.min), Max(template.max, profile.max))`.

The WordPress pack uses 150-800ms with 100ms jitter, matching typical shared hosting latency.

---

## CVE Modules

A `PackCveModule` targets a specific published vulnerability. It contains:

- `cve_id`: CVE identifier
- `severity`: `critical`, `high`, `medium`, or `low`
- `affected_versions`: list of product versions that carry the vulnerability
- `probe_paths`: glob patterns matching the paths exploit scanners use to probe for the vulnerability
- `probe_response`: optional `PackResponseTemplate` with CVE-specific response content and LLM hints

When a request path matches a CVE module's `probe_paths`:
- Confidence is set by severity (0.75-0.95)
- Weight is 2.5 (higher than most honeypot path entries)
- `CveProbeContributor` classifies the bot as `MaliciousBot` for critical/high, `Scraper` for medium/low
- The `simulation-pack` action policy is triggered for confidence >= 0.7

If the module defines a `probe_response`, that template is served instead of any pack-level template matching the same path.

---

## Signals Emitted

`CveProbeContributor` writes to the blackboard when a path matches:

| Signal Key | Type | Value |
|-----------|------|-------|
| `simulation.pack.match` | `bool` | `true` when any pack path matches |
| `cve.probe.pack_id` | `string` | ID of the matched pack, e.g. `"wordpress-5.9"` |
| `cve.probe.detected` | `bool` | `true` when the match is a CVE module probe path |
| `cve.probe.id` | `string` | CVE identifier, e.g. `"CVE-2024-6386"` |
| `cve.probe.severity` | `string` | `critical`, `high`, `medium`, `low`, or `unknown` |
| `action.trigger_policy` | `string` | `"simulation-pack"` (set when confidence >= 0.7) |
| `action.trigger_reason` | `string` | Human-readable reason, e.g. `"CVE probe: CVE-2024-6386 (critical)"` |

`BeaconContributor` (Priority 2, from `Mostlylucid.BotDetection.ApiHolodeck`) writes when a canary from a previous holodeck response is found in the current request:

| Signal Key | Type | Value |
|-----------|------|-------|
| `beacon.matched` | `bool` | `true` when a canary replay is detected |
| `beacon.original_fingerprint` | `string` | Fingerprint that received the original holodeck response |
| `beacon.canary` | `string` | The matched canary value |
| `beacon.path` | `string` | Path from the original holodeck response |
| `beacon.age_seconds` | `double` | Age of the beacon record at match time |
| `beacon.pack_id` | `string` | Pack ID from the original engagement |

---

## Adding a New Pack

Place a YAML file in `src/Mostlylucid.BotDetection/SimulationPacks/Packs/`. The `*.yaml` glob in the `.csproj` includes it as an embedded resource automatically. No code changes are required.

Minimum viable pack:

```yaml
id: my-framework-1.0
name: MyFramework 1.0 Honeypot
framework: myframework
version: "1.0"

timing_profile:
  min_response_ms: 100
  max_response_ms: 500
  jitter_ms: 50

honeypot_paths:
  - path: /admin/login
    confidence: 0.85
    weight: 2.0
    category: admin-auth

response_templates:
  - path_pattern: /admin/login
    status_code: 200
    content_type: "text/html; charset=UTF-8"
    body: |
      <!DOCTYPE html><html><body><form action="/admin/login" method="post">
      <input name="username"><input type="password" name="password">
      <input type="hidden" name="_token" value="{{token}}">
      <button type="submit">Login</button></form></body></html>
```

For CVE modules, add a `cve_modules` section. Use `dynamic: true` with `response_hints` for paths where multi-step exploit flows benefit from LLM-generated context-aware responses.
