# Gateway CLI/Docker UX and Cookbook Design

**Goal:** Make the StyloBot Gateway immediately legible for operators choosing between Caddy and StyloBot, with a polished first-run experience and practical copy-paste examples for real-world scenarios.

**Architecture:** Two parallel improvements: a startup banner that confirms key config at a glance, and a set of complete scenario examples (WordPress, e-commerce, API, shadow mode, multi-site) that demonstrate YARP signal routing as the core differentiator over Caddy.

**Primary audience:** Operators who know what a reverse proxy is, have used Caddy or nginx, and need to understand why StyloBot adds value and how to configure it for their specific app type.

---

## Startup Banner

Print a single bordered block immediately at boot (before async startup tasks run) showing the facts an operator needs to verify their config is correct. Replaces the current scattered log lines and the "no routes" ASCII box.

```
╔══════════════════════════════════════════════════════════╗
║  StyloBot Gateway  v0.6.0                                ║
╠══════════════════════════════════════════════════════════╣
║  HTTP   :8080                                            ║
║  HTTPS  :8443  (cert-from-file)                          ║
║  Route  http://myapp:3000  [catch-all]                   ║
║  Policy  throttle-stealth  |  threshold  0.70            ║
║  Admin  /admin  [protected]                              ║
╚══════════════════════════════════════════════════════════╝
```

Fields: HTTP port, HTTPS status (disabled / port + mode), upstream or "config file" or "NOT CONFIGURED", default action policy + bot threshold, admin path + auth status.

**Validation warnings** printed below the banner (not inside it):
- `[WARN] ADMIN_SECRET not set -admin API is disabled until configured`
- `[WARN] No proxy routes -gateway returns 503 for all requests; set DEFAULT_UPSTREAM or mount a yarp.json`

The existing TRUST_ALL_FORWARDED_PROXIES warning already exists and remains unchanged.

Implementation: new file `Configuration/StartupBanner.cs` with a single static `Print(IConfiguration config, TlsOptions tls)` method. Called from Program.cs right after `builder.Services.AddGatewayConfiguration()` (before `builder.Build()`) so the config is resolved but no DI overhead needed.

---

## YARP Signal Routing (the differentiator)

The gateway injects the following headers into every proxied upstream request after bot detection runs:

| Header | Values | Always sent |
|--------|--------|-------------|
| `X-Bot-Detected` | `true` / `false` | Yes |
| `X-Bot-Confidence` | `0.00`–`1.00` | Yes |
| `X-Bot-Detection-Probability` | `0.0000`–`1.0000` | Yes |
| `X-Bot-Detection-RiskBand` | `Low`, `Elevated`, `Medium`, `High` | Yes |
| `X-Bot-Type` | `MaliciousBot`, `Scraper`, `SearchEngine`, etc. | When bot detected |
| `X-Bot-Name` | `GPTBot`, `AhrefsBot`, etc. | When named bot detected |
| `X-Is-Malicious-Bot` | `true` / `false` | When bot detected |
| `X-Is-Search-Engine` | `true` / `false` | When bot detected |

Your backend reads these headers. YARP routing rules in `yarp.json` can route to different clusters based on them. This is the capability Caddy does not have.

---

## Practical Examples

Five complete scenarios, each as a self-contained directory under `src/Stylobot.Gateway/examples/`:

```
examples/
  wordpress/
    docker-compose.yml
    config/yarp.json
    config/appsettings.json
    README.md
  ecommerce/
    docker-compose.yml
    config/yarp.json
    config/appsettings.json
    README.md
  api-protection/
    docker-compose.yml
    config/yarp.json
    config/appsettings.json
    README.md
  shadow-mode/
    docker-compose.yml
    config/appsettings.json
    README.md
  multi-site/
    docker-compose.yml
    config/yarp.json
    config/appsettings.json
    README.md
```

### Scenario 1: WordPress Protection

**Problem:** xmlrpc.php DDoS amplification, wp-login.php brute force, comment spam, scanner probes.

**Config approach:**
- `/xmlrpc.php` → `strict` policy with block threshold 0.3 (almost everything hitting this is a bot)
- `/wp-login.php`, `/wp-admin/**` → `strict` policy with block threshold 0.7
- `/**` → `default` policy
- Single upstream cluster pointing at WordPress container
- Signals forwarded to WordPress so plugins like Jetpack/Wordfence can read `X-Bot-Detected`

**yarp.json:** Single route, single cluster. PathPolicies handle the graduated response.

### Scenario 2: E-commerce / Magento Scraper Defense

**Problem:** Price scrapers hitting `/catalog/**`, inventory bots, checkout abuse.

**Config approach:**
- `/catalog/**`, `/api/products/**` → `scraper-defense` policy (behavioral + velocity detectors weighted up)
- `/checkout/**`, `/customer/account/**` → `strict` policy
- `/**` → `default` policy
- Two clusters: `real-backend` and `bot-sink` (returns 429 with Retry-After header)
- YARP routes bots (`X-Bot-Detection-RiskBand: High`) to `bot-sink` cluster

**yarp.json:** Two routes -one for bot traffic (header match condition), one for human traffic. Header match: `X-Bot-Detection-RiskBand` = `High`.

### Scenario 3: API Service Protection

**Problem:** Credential stuffing on `/api/auth/**`, API key probing, scraping.

**Config approach:**
- `/api/auth/**` → `strict` policy
- `/api/**` → `api` policy (no behavioral analysis -APIs don't have page-load sequences)
- Signals forwarded so backend can implement app-level rate limiting on top

**yarp.json:** Single route, single cluster. PathPolicies handle the split.

### Scenario 4: Shadow Mode (Transparent Monitoring)

**Problem:** Operator wants to understand traffic before committing to blocking.

**Config approach:**
- All paths → `logonly` policy
- No blocks, no throttling
- Dashboard at `/_stylobot` shows what would have been blocked
- After 7 days of data, flip `BlockDetectedBots: true`

**docker-compose.yml:** No yarp.json needed -DEFAULT_UPSTREAM covers it.

### Scenario 5: Multi-Site / SaaS

**Problem:** Single gateway fronting multiple virtual hosts with different protection levels.

**Config approach:**
- `admin.example.com` → strict policy, separate cluster
- `api.example.com` → api policy, separate cluster
- `www.example.com` → default policy, main cluster
- YARP host-based routing

**yarp.json:** Three routes with host match conditions.

---

## README Restructure

Reorder README.md sections:

1. What StyloBot Gateway does (one paragraph: "drop-in reverse proxy with bot intelligence")
2. Quick Start (30 seconds, unchanged)
3. **NEW: Why not just Caddy?** -the YARP signals differentiator section
4. **NEW: Cookbook** -links to the 5 example directories with one-line descriptions
5. Deployment Tiers (existing, unchanged)
6. Bot Detection Headers Reference (existing)
7. Configuration Reference (existing)
8. Detection Methods (existing)
9. Advanced Configuration (existing)

The "Why not just Caddy?" section is 200 words maximum. Shows the signal header table and a two-line YARP routing example. Ends with "See the cookbook for complete working examples."

---

## Scope

This spec does NOT change the detection engine, signal model, action policies, dashboard, or any non-Gateway project. All changes are in `src/Stylobot.Gateway/`.