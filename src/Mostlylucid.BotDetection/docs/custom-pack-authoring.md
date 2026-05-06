# Custom Simulation Pack Authoring

Simulation packs are YAML files that describe a fake software installation (a CMS, e-commerce platform, IoT admin panel, etc.) that StyloBot serves to bots probing for known vulnerabilities. When a request matches a honeypot path, StyloBot serves a realistic fake response, records the interaction, and applies detection signals.

This guide covers writing a pack YAML, understanding every field, registering custom packs, and testing them.

---

## How Packs Work

At startup, `SimulationPackLoader` reads all `*.yaml` files embedded under `Mostlylucid.BotDetection.SimulationPacks.Packs.*` and builds a flattened path lookup. When a request arrives, `CveProbeContributor` checks whether the path matches any honeypot or CVE probe path and writes detection signals. `SimulationPackResponder` finds the matching response template and serves it, optionally with an HMAC canary embedded for beacon tracking.

Path matching uses `FileSystemName.MatchesSimpleExpression` (glob-style, case-insensitive). CVE probe responses take priority over pack-level response templates.

---

## Complete YAML Schema

### Top-Level Pack Fields

```yaml
id: wordpress-5.9              # Required. Unique identifier. Used in logs, dashboard, and canary headers.
name: WordPress 5.9 Honeypot   # Required. Human-readable display name.
framework: wordpress           # Required. Framework key used by generic 404 fallback logic.
version: "5.9"                 # Required. Version string. Quoted to avoid YAML float parsing.
description: >                 # Optional. Shown in dashboard pack list.
  Simulates an unpatched WordPress 5.9 installation
  to detect WP-targeting bots.

prompt_personality: >          # Optional. Appended to the LLM system prompt when generating
  You are emulating a PHP 7.4  # dynamic responses for this pack. Use it to give the LLM
  WordPress installation. Use  # product-specific vocabulary, version details, and API style.
  WordPress coding conventions.
```

`framework` affects the fallback 404 body when no template matches. Currently recognised values are `wordpress` and `drupal`; all other values produce a plain Apache-style 404.

---

### Timing Profile

```yaml
timing_profile:
  min_response_ms: 150   # Minimum delay before the response is written. Default: 50.
  max_response_ms: 800   # Maximum delay. Default: 300.
  jitter_ms: 100         # Unused in the current runtime; reserved for future jitter layering.
```

Per-template `min_delay_ms` and `max_delay_ms` override the pack-level profile when set. The responder picks a random value between `max(template.min, profile.min)` and `max(template.max, profile.max)`.

---

### Honeypot Paths

```yaml
honeypot_paths:
  - path: /wp-login.php            # Required. Glob pattern matched against the request path.
    confidence: 0.85               # Detection confidence delta (0.0-1.0). Default: 0.9.
    weight: 1.8                    # Contribution weight multiplier. Default: 2.0.
    category: wordpress-auth       # Optional label for grouping in the dashboard.

  - path: /wp-admin/*              # Glob wildcard matches any path under /wp-admin/.
    confidence: 0.90
    weight: 2.0
    category: wordpress-admin

  - path: /wp-config.php*          # Trailing wildcard catches /wp-config.php.bak, etc.
    confidence: 0.95
    weight: 2.5
    category: wordpress-config
```

`confidence` is the delta added to the bot probability when a path matches. A value of `0.95` on `/wp-config.php` is correct: no legitimate user agent requests a config file. A value of `0.60` is more appropriate for paths like `/wp-content/uploads/*` that could appear in legitimate deep-link traffic.

`weight` multiplies the contribution's influence in the aggregated evidence calculation. CVE probe paths always use a weight of `2.5` regardless of per-path settings.

---

### Response Templates

```yaml
response_templates:
  - path_pattern: /wp-login.php        # Required. Glob matched against the request path.
    status_code: 200                   # HTTP status to return. Default: 200.
    content_type: "text/html; charset=UTF-8"  # Default: text/html.
    min_delay_ms: 200                  # Per-template delay floor.
    max_delay_ms: 600                  # Per-template delay ceiling.
    headers:                           # Optional. Extra response headers.
      X-Powered-By: "PHP/7.4.33"
      X-Frame-Options: SAMEORIGIN
    body: |                            # Required. Static body, or LLM prompt when dynamic: true.
      <!DOCTYPE html>
      <html lang="en-US">
      ...
```

```yaml
  - path_pattern: /xmlrpc.php
    status_code: 200
    content_type: "text/xml; charset=UTF-8"
    dynamic: true                      # When true, body is used as context for LLM generation.
    body: |
      <?xml version="1.0" encoding="UTF-8"?>
      <methodResponse><params><param><value>
        <string>XML-RPC server accepts POST requests only.</string>
      </value></param></params></methodResponse>
    response_hints:                    # Hints for LLM-powered generation (see section below).
      endpoint_description: "WordPress XML-RPC endpoint"
      response_format: xml
      ...
```

The `dynamic` field defaults to `false`. When `true` and an `IHolodeckResponder` is registered (requires `AddLlmHolodeck()`), the LLM generates the response body using `body` as a fallback/seed and `response_hints` as generation context. If the LLM is unavailable, the static `body` is served verbatim with placeholder substitution applied.

---

### Template Placeholders

Three placeholders are substituted in static (non-dynamic) response bodies. Include them wherever the real product would expose a token or key that could identify a bot if replayed.

| Placeholder | Substitution |
|---|---|
| `{{nonce}}` | HMAC canary tied to the visitor fingerprint and path. Use in HTML forms, REST tokens, and nonce fields. |
| `{{token}}` | Same canary value as `{{nonce}}`. Use in Authorization headers and bearer token fields. |
| `{{api_key}}` | Same canary value as `{{nonce}}`. Use in API key fields and configuration values. |

All three placeholders resolve to the same HMAC canary string for a given fingerprint and path. When the bot replays the canary in a future request, `BeaconContributor` detects it and links the rotated fingerprint back to the original via `beacon.original_fingerprint`.

If `ICanaryGenerator` is not registered, substitution is skipped and the literal placeholder text is served (avoid this in production).

---

### LLM Response Hints (`response_hints`)

Use `response_hints` when `dynamic: true` to guide LLM generation toward realistic, version-accurate content. All fields are optional.

```yaml
response_hints:
  endpoint_description: >
    WordPress admin-ajax.php handler that processes AJAX
    actions registered by plugins.
  response_format: json          # json | xml | html | plaintext | php
  body_schema: >
    {"success": true/false, "data": "response payload or error code"}
  expected_methods:              # HTTP methods the endpoint accepts.
    - POST
  exploit_flow: >
    Step 1: POST with action=wpml_action probes for WPML.
    Step 2: POST with Twig SSTI payload in translatable string.
    Step 3: Response containing 49 confirms execution.
  product_context:               # Arbitrary key-value context fed to the LLM.
    php_version: "7.4.33"
    wordpress_version: "5.9"
    xmlrpc_enabled: "true"
  error_template: >
    {"success":false,"data":"-1"}
```

`exploit_flow` helps the LLM maintain realistic multi-step interaction across sequential requests from the same bot session. `product_context` provides version accuracy. `error_template` tells the LLM what to return when the bot sends malformed payloads.

When `IHolodeckResponder` is not available, `response_hints` is ignored and the static `body` is served.

---

### CVE Modules

```yaml
cve_modules:
  - cve_id: CVE-2024-6386                # Required. CVE identifier shown in the Threats tab.
    severity: critical                   # critical | high | medium | low. Controls confidence.
    description: >
      WPML plugin remote code execution via Twig SSTI.
    affected_versions:                   # Informational; not used in matching.
      - "5.8"
      - "5.9"
    probe_paths:                         # Required. List of paths that indicate active probing.
      - /wp-admin/admin-ajax.php
    probe_response:                      # Optional. Overrides pack-level templates for these paths.
      path_pattern: /wp-admin/admin-ajax.php
      status_code: 200
      content_type: application/json
      min_delay_ms: 200
      max_delay_ms: 600
      dynamic: true
      body: '{"success":false,"data":"0"}'
      response_hints:
        endpoint_description: "WordPress admin-ajax.php AJAX handler"
        response_format: json
        exploit_flow: >
          Step 1: POST action=wpml_action checks for WPML.
          Step 2: Twig SSTI payload in translatable string.
        error_template: '{"success":false,"data":"-1"}'
```

Severity drives the confidence delta applied by `CveProbeContributor`:

| Severity | Confidence delta |
|---|---|
| `critical` | 0.95 |
| `high` | 0.90 |
| `medium` | 0.80 |
| `low` / unset | 0.75 |

CVE probe paths always carry a weight of `2.5`. CVE `probe_response` templates take priority over pack-level `response_templates` when both could match the same path.

---

## Registering a Custom Pack

### Option 1: Replace the Registry Before `AddBotDetection()`

`AddBotDetection()` uses `TryAddSingleton<ISimulationPackRegistry, SimulationPackLoader>()`, so registering your own implementation beforehand wins:

```csharp
builder.Services.AddSingleton<ISimulationPackRegistry>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SimulationPackLoader>>();
    var loader = new SimulationPackLoader(logger);

    // Parse YAML from a file on disk:
    var yaml = File.ReadAllText("/etc/stylobot/packs/my-custom-pack.yaml");
    loader.LoadFromYamlString(yaml);   // see Option 2 note below

    return loader;
});
builder.Services.AddBotDetection();
```

### Option 2: Implement `ISimulationPackRegistry` Directly

For full control, implement the interface and compose with `SimulationPackLoader`:

```csharp
public sealed class CompositePackRegistry : ISimulationPackRegistry
{
    private readonly ISimulationPackRegistry _builtIn;
    private readonly List<SimulationPack> _custom;

    public CompositePackRegistry(ILogger<SimulationPackLoader> logger, string packDirectory)
    {
        _builtIn = new SimulationPackLoader(logger);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _custom = Directory.GetFiles(packDirectory, "*.yaml")
            .Select(f => deserializer.Deserialize<SimulationPack>(File.ReadAllText(f)))
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id))
            .ToList();
    }

    public IReadOnlyList<SimulationPack> GetLoadedPacks()
        => _builtIn.GetLoadedPacks().Concat(_custom).ToList();

    public SimulationPack? GetPack(string id)
        => _builtIn.GetPack(id) ?? _custom.FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<PackCveModule> GetAllCveModules()
        => _builtIn.GetAllCveModules()
            .Concat(_custom.SelectMany(p => p.CveModules))
            .ToList();

    public bool IsHoneypotPath(string path, out SimulationPack? matchedPack, out PackCveModule? matchedCve)
    {
        if (_builtIn.IsHoneypotPath(path, out matchedPack, out matchedCve))
            return true;

        foreach (var pack in _custom)
        {
            foreach (var hp in pack.HoneypotPaths)
            {
                if (FileSystemName.MatchesSimpleExpression(hp.Path, path, ignoreCase: true))
                {
                    matchedPack = pack;
                    matchedCve = null;
                    return true;
                }
            }
            foreach (var cve in pack.CveModules)
            {
                foreach (var probe in cve.ProbePaths)
                {
                    if (FileSystemName.MatchesSimpleExpression(probe, path, ignoreCase: true))
                    {
                        matchedPack = pack;
                        matchedCve = cve;
                        return true;
                    }
                }
            }
        }

        matchedPack = null;
        matchedCve = null;
        return false;
    }

    public PackResponseTemplate? FindResponseTemplate(string path, out SimulationPack? pack)
    {
        var result = _builtIn.FindResponseTemplate(path, out pack);
        if (result is not null) return result;

        foreach (var p in _custom)
        {
            foreach (var cve in p.CveModules)
            {
                if (cve.ProbeResponse is not null &&
                    FileSystemName.MatchesSimpleExpression(cve.ProbeResponse.PathPattern, path, ignoreCase: true))
                {
                    pack = p;
                    return cve.ProbeResponse;
                }
            }
            foreach (var template in p.ResponseTemplates)
            {
                if (FileSystemName.MatchesSimpleExpression(template.PathPattern, path, ignoreCase: true))
                {
                    pack = p;
                    return template;
                }
            }
        }

        pack = null;
        return null;
    }
}
```

Register before `AddBotDetection()`:

```csharp
builder.Services.AddSingleton<ISimulationPackRegistry>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SimulationPackLoader>>();
    return new CompositePackRegistry(logger, "/etc/stylobot/packs");
});
builder.Services.AddBotDetection();
```

### Option 3: Embed in Your Own Assembly

Place the YAML file in your project under any path, mark it as `EmbeddedResource`, then load it using `Assembly.GetManifestResourceStream`:

```csharp
var assembly = Assembly.GetExecutingAssembly();
using var stream = assembly.GetManifestResourceStream("MyApp.Packs.my-pack.yaml")!;
using var reader = new StreamReader(stream);
var yaml = reader.ReadToEnd();
```

Parse using `YamlDotNet` with `UnderscoredNamingConvention` (required to match the field names):

```csharp
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
var pack = deserializer.Deserialize<SimulationPack>(yaml);
```

---

## Designing Effective Honeypot Paths

**Match real probe patterns.** Review CVE databases, Shodan results, and your own bot traffic logs. WordPress bots reliably hit `/wp-login.php`, `/xmlrpc.php`, `/wp-config.php`, and REST API user enumeration endpoints. Generic PHP scanners hit `/admin/`, `/phpmyadmin/`, and `/phpinfo.php`.

**Use globs deliberately.** `/wp-admin/*` catches all admin subpaths with one entry. `/wp-content/plugins/*` catches plugin enumeration. Avoid over-broad patterns like `/*` which would match legitimate traffic.

**Set confidence to match specificity.** Use these thresholds as a guide:

- `0.95`: Only an exploit tool requests this (e.g., `/wp-config.php`, `/.env`).
- `0.85-0.90`: Strongly indicates malicious intent but could appear in security audits (e.g., `/wp-login.php`, `/xmlrpc.php`).
- `0.70-0.80`: Admin paths that could be hit by misconfigured crawlers (e.g., `/wp-admin/*`).
- `0.55-0.65`: Fingerprinting or enumeration paths (e.g., `/readme.html`, theme/plugin directory listings).

**Use `weight` to control contribution influence.** A `weight` of `2.5` (the CVE default) means this single path match dominates the aggregated evidence. Pack-level honeypot paths default to `2.0`. Use lower weights (`0.8-1.2`) for paths that provide corroborating evidence but are not individually conclusive.

**Add a category for dashboard filtering.** The `category` field groups paths in the Threats tab. Use a consistent prefix per pack, e.g., `wordpress-auth`, `wordpress-admin`, `wordpress-api`.

---

## CVE Module Design

Add a CVE module when the probe path is associated with a specific, named vulnerability and you want the detection to appear in the Threats tab with CVE attribution. Use pack-level honeypot paths for generic admin paths without a specific CVE.

**Probe patterns should match the actual exploit probe.** CVE-2024-27956 (WP-Automatic SQL injection) is always probed via `/wp-content/plugins/wp-automatic/inc/csv.php`. That is a reliable, high-confidence signal. Avoid adding overly generic paths (e.g., `/wp-content/plugins/`) to a CVE module; put those in `honeypot_paths` instead.

**A `probe_response` is optional.** If you omit it, the pack-level response templates serve the response for that path. Add a `probe_response` when the CVE requires a specific response to continue the exploit flow (e.g., the SSTI probe expects an AJAX JSON response, not a generic 404).

**`affected_versions` is informational only.** It appears in the dashboard and can be used for filtering by future tooling, but does not affect path matching or confidence scoring in the current implementation.

---

## Testing Your Pack

### Unit Test: Path Matching

Instantiate `SimulationPackLoader` directly (or your composite registry) and call `IsHoneypotPath`:

```csharp
var registry = new SimulationPackLoader(NullLogger<SimulationPackLoader>.Instance);

var matched = registry.IsHoneypotPath("/wp-login.php", out var pack, out var cve);
Assert.True(matched);
Assert.Equal("wordpress-5.9", pack!.Id);
Assert.Null(cve); // /wp-login.php is a pack honeypot, not a CVE probe
```

For a CVE probe path:

```csharp
var matched = registry.IsHoneypotPath(
    "/wp-content/plugins/backup-migration/includes/backup-heart.php",
    out _, out var cve);
Assert.True(matched);
Assert.Equal("CVE-2023-6553", cve!.CveId);
```

### Unit Test: Template Rendering

```csharp
var template = registry.FindResponseTemplate("/wp-login.php", out var pack);
Assert.NotNull(template);
Assert.Equal(200, template!.StatusCode);
Assert.Contains("loginform", template.Body);
```

### Manual Test via Demo App

Run the demo application and issue requests to honeypot paths:

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo

curl -s http://localhost:5080/wp-login.php
curl -s http://localhost:5080/wp-config.php
curl -s http://localhost:5080/wp-json/wp/v2/users
```

Check the dashboard at `http://localhost:5080/_stylobot` under the Threats tab to confirm the pack is engaged and CVE signals appear.

### Verify Canary Substitution

Include `{{nonce}}` in a response body and confirm it is replaced in the served response:

```yaml
body: |
  {"success":false,"token":"{{nonce}}"}
```

The served response should contain a 32-character hex string in place of `{{nonce}}`. Make a second request from the same fingerprint and confirm the value matches. The value changes if the fingerprint changes.

### Verify Timing Profile

Use `curl -w "%{time_total}"` to confirm delays are within the expected range:

```bash
curl -w "\nTotal time: %{time_total}s\n" -s http://localhost:5080/wp-login.php
```

---

## Complete Minimal Example

A full working pack for a generic PHP admin panel, demonstrating all key fields:

```yaml
id: php-admin-panel-1.0
name: PHP Admin Panel Honeypot
framework: php
version: "1.0"
description: >
  Simulates a generic PHP administration panel to detect
  admin credential-stuffing bots and phpMyAdmin scanners.

timing_profile:
  min_response_ms: 80
  max_response_ms: 400
  jitter_ms: 60

honeypot_paths:
  - path: /admin/login.php
    confidence: 0.85
    weight: 1.8
    category: php-admin-auth
  - path: /admin/*
    confidence: 0.75
    weight: 1.5
    category: php-admin-panel
  - path: /phpmyadmin/*
    confidence: 0.90
    weight: 2.0
    category: php-pma
  - path: /phpinfo.php
    confidence: 0.80
    weight: 1.8
    category: php-info-disclosure

response_templates:
  - path_pattern: /admin/login.php
    status_code: 200
    content_type: "text/html; charset=UTF-8"
    min_delay_ms: 100
    max_delay_ms: 350
    headers:
      X-Powered-By: "PHP/8.1.27"
    body: |
      <!DOCTYPE html>
      <html>
      <head><title>Admin Panel Login</title></head>
      <body>
      <form method="POST" action="/admin/login.php">
        <input type="hidden" name="_token" value="{{nonce}}">
        <input type="text" name="username" placeholder="Username">
        <input type="password" name="password" placeholder="Password">
        <button type="submit">Login</button>
      </form>
      </body>
      </html>

  - path_pattern: /phpmyadmin/*
    status_code: 200
    content_type: "text/html; charset=UTF-8"
    min_delay_ms: 120
    max_delay_ms: 400
    headers:
      X-Powered-By: "PHP/8.1.27"
    dynamic: true
    body: |
      phpMyAdmin 5.2.1 login page with server selection dropdown
      and username/password fields.
    response_hints:
      endpoint_description: "phpMyAdmin web interface login page"
      response_format: html
      body_schema: >
        Standard phpMyAdmin login form with server selector,
        username, password, and remember-me checkbox.
      expected_methods: ["GET", "POST"]
      exploit_flow: >
        Step 1: GET /phpmyadmin/ returns login page.
        Step 2: POST credentials attempts authentication.
        Step 3: Successful auth returns dashboard with database list.
      product_context:
        phpmyadmin_version: "5.2.1"
        php_version: "8.1.27"
      error_template: >
        Error: Access denied for user 'root'@'localhost'

  - path_pattern: /phpinfo.php
    status_code: 200
    content_type: "text/html; charset=UTF-8"
    min_delay_ms: 50
    max_delay_ms: 150
    headers:
      X-Powered-By: "PHP/8.1.27"
    body: |
      <!DOCTYPE html>
      <html><head><title>phpinfo()</title></head>
      <body>
      <h1>PHP Version 8.1.27</h1>
      <table>
      <tr><td>System</td><td>Linux web01 5.15.0</td></tr>
      <tr><td>Build Date</td><td>Nov 14 2023</td></tr>
      <tr><td>Server API</td><td>Apache 2.0 Handler</td></tr>
      </table>
      </body></html>

cve_modules:
  - cve_id: CVE-2023-26562
    severity: high
    description: phpMyAdmin CSRF token bypass allowing unauthorized database access
    affected_versions: ["5.0", "5.1", "5.2"]
    probe_paths:
      - /phpmyadmin/import.php
      - /phpmyadmin/tbl_sql.php
    probe_response:
      path_pattern: /phpmyadmin/import.php
      status_code: 200
      content_type: "text/html; charset=UTF-8"
      min_delay_ms: 150
      max_delay_ms: 500
      dynamic: true
      body: |
        <html><head><title>phpMyAdmin 5.2.1</title></head>
        <body><div class="error">No data was received to import.
        Either no file name was submitted, or the file size exceeded the maximum size
        permitted by your PHP configuration.</div></body></html>
      response_hints:
        endpoint_description: "phpMyAdmin file import handler"
        response_format: html
        exploit_flow: >
          Step 1: GET /phpmyadmin/import.php with crafted token probes CSRF bypass.
          Step 2: POST with SQL payload attempts unauthorized execution.
        error_template: >
          <div class="error">Access denied.</div>
```

This pack registers four honeypot paths, three response templates (two static, one dynamic), and one CVE module for a specific phpMyAdmin vulnerability. The `{{nonce}}` in the admin login form will be replaced with a canary value. If the bot replays the canary in a subsequent POST, `BeaconContributor` flags the rotation.
