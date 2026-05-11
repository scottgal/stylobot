# Gateway CLI/Docker UX and Cookbook Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a startup banner with validation warnings, then create five complete copy-paste operator examples (WordPress, e-commerce, API, shadow mode, multi-site) and restructure the README to lead with the YARP signal routing differentiator.

**Architecture:** New `StartupBanner.cs` reads config early and prints a bordered summary block. Example directories under `src/Stylobot.Gateway/examples/` contain self-contained docker-compose + yarp.json + appsettings files. README restructure adds "Why not just Caddy?" and a cookbook section near the top.

**Tech Stack:** C# 12 / .NET 10, YARP 2.3, Serilog, Docker Compose v2, Markdown.

---

### Task 1: Startup Banner

**Files:**
- Create: `src/Stylobot.Gateway/Configuration/StartupBanner.cs`
- Modify: `src/Stylobot.Gateway/Program.cs` (lines 32–34)

- [ ] **Step 1: Create `StartupBanner.cs`**

```csharp
// src/Stylobot.Gateway/Configuration/StartupBanner.cs
namespace Stylobot.Gateway.Configuration;

public static class StartupBanner
{
    public static void Print(IConfiguration config, TlsOptions tls)
    {
        var version = typeof(StartupBanner).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var httpPort = config.GetValue("GATEWAY_HTTP_PORT", 8080);
        var upstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");
        var yarpConfig = GatewayPaths.YarpConfig;
        var botThreshold = config.GetValue("BotDetection:BotThreshold", 0.7);
        var policy = config.GetValue("BotDetection:DefaultActionPolicyName", "throttle-stealth");
        var adminPath = config.GetValue("Gateway:AdminBasePath", "/admin");
        var adminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET")
                          ?? config.GetValue<string>("Gateway:AdminSecret");

        var httpsLine = tls.Enabled
            ? $":{tls.Port}  ({(tls.IsAcme ? $"ACME / {tls.Domain}" : "cert-from-file")})"
            : "disabled";

        string routeLine;
        if (!string.IsNullOrWhiteSpace(upstream))
            routeLine = $"{upstream}  [catch-all]";
        else if (File.Exists(yarpConfig))
            routeLine = $"config file  ({yarpConfig})";
        else
            routeLine = "NOT CONFIGURED";

        var adminLine = string.IsNullOrWhiteSpace(adminSecret)
            ? $"{adminPath}  [no secret - disabled]"
            : $"{adminPath}  [protected]";

        var width = 58;
        var border = new string('═', width - 2);

        Console.WriteLine($"╔{border}╗");
        Console.WriteLine(Pad($"  StyloBot Gateway  v{version}", width));
        Console.WriteLine($"╠{border}╣");
        Console.WriteLine(Pad($"  HTTP   :{httpPort}", width));
        Console.WriteLine(Pad($"  HTTPS  {httpsLine}", width));
        Console.WriteLine(Pad($"  Route  {routeLine}", width));
        Console.WriteLine(Pad($"  Policy  {policy}  |  threshold  {botThreshold:F2}", width));
        Console.WriteLine(Pad($"  Admin  {adminLine}", width));
        Console.WriteLine($"╚{border}╝");

        if (string.IsNullOrWhiteSpace(adminSecret))
            Console.WriteLine("[WARN] ADMIN_SECRET not set -admin API is disabled until configured");

        if (routeLine == "NOT CONFIGURED")
            Console.WriteLine("[WARN] No proxy routes -gateway returns 503 for all requests; set DEFAULT_UPSTREAM or mount a yarp.json");
    }

    private static string Pad(string content, int width)
    {
        var truncated = content.Length > width - 4 ? content[..(width - 7)] + "..." : content;
        return $"║{truncated.PadRight(width - 2)}║";
    }
}
```

- [ ] **Step 2: Wire the banner into Program.cs**

In `Program.cs`, replace line 32:
```csharp
Log.Information("Starting Stylobot.Gateway v0.1");
```
with:
```csharp
var earlyConfig = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(GatewayPaths.Config, "appsettings.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();
var earlyTlsForBanner = ServiceCollectionExtensions.ReadTlsOptionsFromEnv();
StartupBanner.Print(earlyConfig, earlyTlsForBanner);
Log.Information("Starting Stylobot.Gateway");
```

- [ ] **Step 3: Build and verify banner prints**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj -c Debug 2>&1 | tail -5
```

Expected: `Build succeeded.  0 Error(s)`

- [ ] **Step 4: Run and visually confirm banner output**

```bash
DEFAULT_UPSTREAM=http://myapp:3000 \
dotnet run --project src/Stylobot.Gateway --no-launch-profile 2>&1 | head -15
```

Expected first lines:
```
╔══════════════════════════════════════════════════════════╗
║  StyloBot Gateway  v...                                  ║
╠══════════════════════════════════════════════════════════╣
║  HTTP   :8080                                            ║
...
╚══════════════════════════════════════════════════════════╝
```

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/Configuration/StartupBanner.cs \
        src/Stylobot.Gateway/Program.cs
git commit -m "feat(gateway): startup banner with config summary and validation warnings"
```

---

### Task 2: WordPress Protection Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/wordpress/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/wordpress/config/yarp.json`
- Create: `src/Stylobot.Gateway/examples/wordpress/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/wordpress/README.md`

- [ ] **Step 1: Create `examples/wordpress/docker-compose.yml`**

```yaml
# StyloBot Gateway -WordPress Protection Example
# Blocks xmlrpc.php abuse, brute-force on wp-login.php, and scanner probes.
#
# Usage:
#   cp .env.example .env   # edit ADMIN_SECRET
#   docker compose up -d
#
# Your WordPress container must be on the same Docker network.
# Replace WP_UPSTREAM with your actual WordPress container URL.

services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=${ADMIN_SECRET:?Set ADMIN_SECRET in .env}
      - KNOWN_PROXIES=${KNOWN_PROXIES:-}
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped

  wordpress:
    image: wordpress:latest
    environment:
      - WORDPRESS_DB_HOST=db
      - WORDPRESS_DB_USER=wp
      - WORDPRESS_DB_PASSWORD=${WP_DB_PASSWORD:?Set WP_DB_PASSWORD in .env}
      - WORDPRESS_DB_NAME=wordpress
    depends_on:
      - db

  db:
    image: mysql:8.0
    environment:
      - MYSQL_DATABASE=wordpress
      - MYSQL_USER=wp
      - MYSQL_PASSWORD=${WP_DB_PASSWORD:?Set WP_DB_PASSWORD in .env}
      - MYSQL_RANDOM_ROOT_PASSWORD=1
    volumes:
      - db_data:/var/lib/mysql

volumes:
  db_data:
```

- [ ] **Step 2: Create `examples/wordpress/config/yarp.json`**

```json
{
  "ReverseProxy": {
    "Routes": {
      "wordpress": {
        "ClusterId": "wordpress",
        "Match": {
          "Path": "{**catch-all}"
        }
      }
    },
    "Clusters": {
      "wordpress": {
        "Destinations": {
          "wordpress": {
            "Address": "http://wordpress:80"
          }
        }
      }
    }
  }
}
```

- [ ] **Step 3: Create `examples/wordpress/config/appsettings.json`**

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "block",

    "Policies": {
      "default": {
        "Description": "Standard WordPress page detection",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      },
      "strict": {
        "Description": "Strict policy for wp-login.php and wp-admin -block at 0.4 confidence",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.6,
        "ImmediateBlockThreshold": 0.4
      },
      "xmlrpc-lockdown": {
        "Description": "xmlrpc.php -block almost everything; only allow verified search engines",
        "FastPath": ["FastPathReputation", "UserAgent", "Ip", "SecurityTool"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.3,
        "ImmediateBlockThreshold": 0.3
      }
    },

    "PathPolicies": {
      "/xmlrpc.php": "xmlrpc-lockdown",
      "/wp-login.php": "strict",
      "/wp-admin/**": "strict",
      "/**": "default"
    }
  }
}
```

- [ ] **Step 4: Create `examples/wordpress/README.md`**

```markdown
# StyloBot Gateway -WordPress Protection

Protects a WordPress site from the most common automated attacks:

- **xmlrpc.php abuse** -used for DDoS amplification and mass brute-force. Nearly all
  legitimate traffic stopped using REST API years ago. This example blocks at 30% bot
  confidence -almost everything hitting xmlrpc.php is a bot.
- **wp-login.php brute force** -credential stuffing at scale. Blocked at 40% confidence.
- **wp-admin scanner probes** -tools like WPScan, Nuclei, and generic vuln scanners.
  Blocked at 40% confidence.
- **General bot traffic** -scrapers, AI crawlers, headless browsers. Throttled by default
  on the main site (won't break legitimate Googlebot traffic).

## Quick Start

1. Copy `.env.example` to `.env` and set the required secrets.
2. Update `wordpress` destination in `config/yarp.json` if your container name differs.
3. Start:

```bash
docker compose up -d
```

## What Your WordPress Backend Receives

Every request to WordPress arrives with these headers from the gateway:

```
X-Bot-Detected: true
X-Bot-Confidence: 0.94
X-Bot-Detection-RiskBand: High
X-Bot-Type: SecurityScanner
X-Is-Malicious-Bot: true
```

WordPress plugins (Wordfence, Jetpack) can read these. To log them:

```php
// In functions.php or a plugin
add_action('init', function() {
    if ($_SERVER['HTTP_X_BOT_DETECTED'] === 'true') {
        error_log('Bot blocked by gateway: ' . $_SERVER['HTTP_X_BOT_DETECTION_RISKBAND']);
    }
});
```

## Tuning

- Too aggressive on wp-login.php? Raise `ImmediateBlockThreshold` in the `strict` policy to `0.6`.
- Want to monitor before blocking? Change `DefaultActionPolicyName` to `logonly` and check logs
  for one week before enabling blocking.
- Running WooCommerce? See `../ecommerce/` for checkout-specific protection.
```

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/examples/wordpress/
git commit -m "feat(gateway): wordpress protection example with per-path policies"
```

---

### Task 3: E-commerce / Scraper Defense Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/ecommerce/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/ecommerce/config/yarp.json`
- Create: `src/Stylobot.Gateway/examples/ecommerce/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/ecommerce/README.md`

- [ ] **Step 1: Create `examples/ecommerce/docker-compose.yml`**

```yaml
# StyloBot Gateway -E-commerce / Scraper Defense
# Routes detected scrapers to a bot-sink cluster instead of your real backend.
# Price scrapers and inventory bots get stale or synthetic data.
#
# Usage:
#   cp .env.example .env
#   docker compose up -d

services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=${ADMIN_SECRET:?Set ADMIN_SECRET in .env}
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped

  # Your real e-commerce backend (Magento, WooCommerce, Shopify headless, etc.)
  app:
    image: your-ecommerce-app:latest
    # ... your app config

  # Bot sink: returns 429 Too Many Requests with a Retry-After header.
  # Replace with a honeypot service if you want to feed bots fake data.
  bot-sink:
    image: nginx:alpine
    volumes:
      - ./bot-sink.conf:/etc/nginx/conf.d/default.conf:ro
```

- [ ] **Step 2: Create `examples/ecommerce/config/yarp.json`**

```json
{
  "ReverseProxy": {
    "Routes": {
      "bot-traffic": {
        "ClusterId": "bot-sink",
        "Match": {
          "Path": "{**catch-all}",
          "Headers": [
            {
              "Name": "X-Bot-Detection-RiskBand",
              "Values": ["High"],
              "Mode": "ExactHeader"
            }
          ]
        },
        "Order": 1
      },
      "human-traffic": {
        "ClusterId": "app",
        "Match": {
          "Path": "{**catch-all}"
        },
        "Order": 2
      }
    },
    "Clusters": {
      "app": {
        "Destinations": {
          "app": {
            "Address": "http://app:80"
          }
        }
      },
      "bot-sink": {
        "Destinations": {
          "bot-sink": {
            "Address": "http://bot-sink:80"
          }
        }
      }
    }
  }
}
```

- [ ] **Step 3: Create `examples/ecommerce/config/appsettings.json`**

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "throttle-stealth",

    "Policies": {
      "default": {
        "Description": "Standard detection for general pages",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      },
      "scraper-defense": {
        "Description": "Catalog and product pages -elevated behavioral and velocity detection",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "AdvancedBehavioral", "CacheBehavior", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.75,
        "ImmediateBlockThreshold": 0.85
      },
      "strict": {
        "Description": "Checkout and account pages -strict behavioral detection",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "AdvancedBehavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.6,
        "ImmediateBlockThreshold": 0.7
      }
    },

    "PathPolicies": {
      "/catalog/**": "scraper-defense",
      "/api/products/**": "scraper-defense",
      "/api/pricing/**": "scraper-defense",
      "/checkout/**": "strict",
      "/customer/account/**": "strict",
      "/api/auth/**": "strict",
      "/**": "default"
    }
  }
}
```

- [ ] **Step 4: Create `examples/ecommerce/README.md`**

```markdown
# StyloBot Gateway -E-commerce / Scraper Defense

Protects e-commerce sites (Magento, WooCommerce, Shopify headless) from:

- **Price scrapers** -bots that crawl `/catalog/**` and `/api/products/**` to harvest
  pricing data. This example routes them to a bot-sink cluster instead of your real
  backend, so scrapers receive stale or fake data.
- **Inventory bots** -high-frequency crawlers checking stock levels.
- **Checkout abuse** -credential stuffing and payment probing on `/checkout/**`.
- **Account takeover** -automated login attempts on `/customer/account/**`.

## How the Bot-Sink Routing Works

YARP has two routes. The first matches requests where `X-Bot-Detection-RiskBand: High`
(added by StyloBot after detection). Those go to `bot-sink`. Everything else goes to
your real backend. The two routes have explicit `Order` values (1 and 2) so the bot
route is checked first.

```
Incoming request
  → StyloBot detects: RiskBand = High
  → Gateway adds header: X-Bot-Detection-RiskBand: High
  → YARP route "bot-traffic" matches
  → Proxied to bot-sink (returns 429)

Incoming request
  → StyloBot detects: RiskBand = Low
  → YARP route "human-traffic" matches
  → Proxied to your real backend
```

## Tuning

- Routing on `High` only? To also route `Medium` confidence bots, add `"Medium"` to the
  header match `Values` array in `yarp.json`.
- Want to feed bots fake catalog data instead of 429s? Replace `bot-sink` with a service
  that returns plausible-but-wrong prices. StyloBot's holodeck feature automates this.
- Magento 2 specific: add `/rest/V1/products/**` and `/rest/V1/inventory/**` to the
  `scraper-defense` PathPolicies.
```

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/examples/ecommerce/
git commit -m "feat(gateway): ecommerce example with YARP signal-based bot-sink routing"
```

---

### Task 4: API Protection Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/api-protection/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/api-protection/config/yarp.json`
- Create: `src/Stylobot.Gateway/examples/api-protection/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/api-protection/README.md`

- [ ] **Step 1: Create `examples/api-protection/docker-compose.yml`**

```yaml
# StyloBot Gateway -API Service Protection
# Protects REST APIs from credential stuffing, scraping, and abuse.
#
# Usage:
#   cp .env.example .env
#   docker compose up -d

services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=${ADMIN_SECRET:?Set ADMIN_SECRET in .env}
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped

  api:
    image: your-api:latest
    # ... your api config
```

- [ ] **Step 2: Create `examples/api-protection/config/yarp.json`**

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "api",
        "Match": {
          "Path": "{**catch-all}"
        }
      }
    },
    "Clusters": {
      "api": {
        "Destinations": {
          "api": {
            "Address": "http://api:80"
          }
        }
      }
    }
  }
}
```

- [ ] **Step 3: Create `examples/api-protection/config/appsettings.json`**

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "throttle-stealth",

    "Policies": {
      "api": {
        "Description": "API endpoints -skip behavioral analysis (no page-load sequences)",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Inconsistency", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      },
      "auth-strict": {
        "Description": "Auth endpoints -block credential stuffing at 0.5 confidence",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.5,
        "ImmediateBlockThreshold": 0.5
      }
    },

    "PathPolicies": {
      "/api/auth/**": "auth-strict",
      "/api/login": "auth-strict",
      "/api/token": "auth-strict",
      "/api/**": "api",
      "/**": "api"
    }
  }
}
```

- [ ] **Step 4: Create `examples/api-protection/README.md`**

```markdown
# StyloBot Gateway -API Service Protection

Protects REST APIs from:

- **Credential stuffing** on `/api/auth/**` and `/api/login` -automated login attempts
  using leaked username/password lists. Blocked at 50% confidence.
- **API key probing** -bots trying to discover valid API keys through brute force.
- **Scraping** -bots consuming your API endpoints at machine speed.

## API-Specific Detection

Pure API traffic behaves differently from browser traffic: no cookies, no page-load
sequences, no referrer headers. The `api` policy skips behavioral analysis detectors
that rely on page-load patterns (CacheBehavior, AdvancedBehavioral) to avoid false
positives on legitimate API clients.

Auth endpoints get the `auth-strict` policy, which re-enables behavioral analysis
since credential stuffing has a distinctive pattern: many rapid sequential POST
requests to the same endpoint.

## What Your API Backend Receives

```
X-Bot-Detected: true
X-Bot-Confidence: 0.87
X-Bot-Detection-RiskBand: High
X-Bot-Type: CredentialStuffer
X-Is-Malicious-Bot: true
```

Your API can read these headers to apply additional rate limiting or logging:

```javascript
// Express.js
app.post('/api/auth/login', (req, res) => {
  if (req.headers['x-is-malicious-bot'] === 'true') {
    return res.status(429).json({ error: 'rate_limited' });
  }
  // ... normal login logic
});
```

## Tuning

- **Too many false positives on mobile clients?** Raise `ImmediateBlockThreshold` in
  `auth-strict` to `0.65`.
- **Using API keys for machine clients?** Add `X-SB-Api-Key` to exempt your own
  monitoring and health-check traffic from detection.
```

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/examples/api-protection/
git commit -m "feat(gateway): api protection example with auth-strict policy"
```

---

### Task 5: Shadow Mode Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/shadow-mode/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/shadow-mode/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/shadow-mode/README.md`

- [ ] **Step 1: Create `examples/shadow-mode/docker-compose.yml`**

```yaml
# StyloBot Gateway -Shadow Mode (Transparent Monitoring)
# Detects bots but never blocks. Observe traffic for 7 days, then enable blocking.
#
# Usage:
#   docker compose up -d
#   # Access admin at http://localhost:8080/admin/summary (no secret required in this example)
#   # After 7 days: set BLOCK_BOTS=true and restart

services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - DEFAULT_UPSTREAM=${APP_UPSTREAM:?Set APP_UPSTREAM=http://yourapp:3000 in .env}
      - ADMIN_ALLOW_INSECURE=true
      - BOTDETECTION__BLOCKDETECTEDBOTS=${BLOCK_BOTS:-false}
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped
```

- [ ] **Step 2: Create `examples/shadow-mode/config/appsettings.json`**

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": false,
    "DefaultActionPolicyName": "logonly",
    "LogAllRequests": true,
    "LogDetailedReasons": true,

    "Policies": {
      "logonly": {
        "Description": "Log everything, block nothing -shadow mode for traffic analysis",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 1.1
      }
    },

    "PathPolicies": {
      "/**": "logonly"
    }
  }
}
```

- [ ] **Step 3: Create `examples/shadow-mode/README.md`**

```markdown
# StyloBot Gateway -Shadow Mode

Use this when you want to understand your traffic before committing to blocking.
All requests pass through. Bots are detected and logged, but never blocked or throttled.

## Quick Start

```bash
echo "APP_UPSTREAM=http://yourapp:3000" > .env
docker compose up -d
```

Check what's being detected:

```bash
curl http://localhost:8080/admin/summary
curl http://localhost:8080/admin/topbots
```

## Enabling Blocking After Your Review

After reviewing 7 days of data, flip the switch:

```bash
echo "BLOCK_BOTS=true" >> .env
docker compose up -d --force-recreate gateway
```

Or without restart (takes effect on next request):

```bash
curl -X POST http://localhost:8080/admin/config \
  -H "Content-Type: application/json" \
  -d '{"BotDetection": {"BlockDetectedBots": true}}'
```

## Reading the Data

The admin API (no secret required in this example -add `ADMIN_SECRET` in production)
returns JSON summaries:

- `GET /admin/summary` -overall bot vs. human ratio, top bot types
- `GET /admin/topbots` -most active bot signatures with risk scores
- `GET /admin/countries` -traffic by country
- `GET /admin/endpoints` -which paths bots target most

## What to Look For

- **High `X-Bot-Detection-RiskBand: High` count on `/wp-login.php`?** Brute force in progress.
- **Lots of `X-Bot-Type: Scraper` on your product catalog?** Price scraping in progress.
- **`X-Is-Search-Engine: true` traffic?** That is Googlebot -do NOT block it.

Use the per-endpoint data to tune your PathPolicies before enabling blocking.
```

- [ ] **Step 4: Commit**

```bash
git add src/Stylobot.Gateway/examples/shadow-mode/
git commit -m "feat(gateway): shadow mode example for traffic monitoring before blocking"
```

---

### Task 6: Multi-Site Example

**Files:**
- Create: `src/Stylobot.Gateway/examples/multi-site/docker-compose.yml`
- Create: `src/Stylobot.Gateway/examples/multi-site/config/yarp.json`
- Create: `src/Stylobot.Gateway/examples/multi-site/config/appsettings.json`
- Create: `src/Stylobot.Gateway/examples/multi-site/README.md`

- [ ] **Step 1: Create `examples/multi-site/docker-compose.yml`**

```yaml
# StyloBot Gateway -Multi-Site / SaaS
# One gateway, multiple virtual hosts, different protection per host.
#
# admin.example.com → strict (internal admin tool)
# api.example.com   → api-protection (REST API consumers)
# www.example.com   → default (public marketing + app)
#
# Usage:
#   cp .env.example .env
#   docker compose up -d

services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
      - "443:8443"
    environment:
      - ADMIN_SECRET=${ADMIN_SECRET:?Set ADMIN_SECRET in .env}
      - GATEWAY_HTTPS_DOMAIN=${DOMAIN:?Set DOMAIN=example.com in .env}
      - GATEWAY_HTTPS_ACME_EMAIL=${ACME_EMAIL:?Set ACME_EMAIL in .env}
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped

  admin-app:
    image: your-admin-app:latest

  api-app:
    image: your-api:latest

  www-app:
    image: your-website:latest
```

- [ ] **Step 2: Create `examples/multi-site/config/yarp.json`**

```json
{
  "ReverseProxy": {
    "Routes": {
      "admin": {
        "ClusterId": "admin-app",
        "Match": {
          "Hosts": ["admin.{**}"],
          "Path": "{**catch-all}"
        },
        "Order": 1
      },
      "api": {
        "ClusterId": "api-app",
        "Match": {
          "Hosts": ["api.{**}"],
          "Path": "{**catch-all}"
        },
        "Order": 2
      },
      "www": {
        "ClusterId": "www-app",
        "Match": {
          "Path": "{**catch-all}"
        },
        "Order": 3
      }
    },
    "Clusters": {
      "admin-app": {
        "Destinations": {
          "admin": { "Address": "http://admin-app:80" }
        }
      },
      "api-app": {
        "Destinations": {
          "api": { "Address": "http://api-app:80" }
        }
      },
      "www-app": {
        "Destinations": {
          "www": { "Address": "http://www-app:80" }
        }
      }
    }
  }
}
```

- [ ] **Step 3: Create `examples/multi-site/config/appsettings.json`**

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "throttle-stealth",

    "Policies": {
      "default": {
        "Description": "Standard detection for public website traffic",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      },
      "admin-strict": {
        "Description": "Internal admin tools -strict policy, block at 0.4",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.4,
        "ImmediateBlockThreshold": 0.4
      },
      "api": {
        "Description": "API endpoints -skip page-load behavioral detectors",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Inconsistency", "ReputationBias", "Heuristic"],
        "UseFastPath": true,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      }
    },

    "PathPolicies": {
      "/**": "default"
    }
  }
}
```

- [ ] **Step 4: Create `examples/multi-site/README.md`**

```markdown
# StyloBot Gateway -Multi-Site / SaaS

One gateway fronts three virtual hosts with different protection levels:

| Host | Policy | Why |
|------|--------|-----|
| `admin.example.com` | `admin-strict` (block at 0.4) | Internal tool -no bots should ever reach it |
| `api.example.com` | `api` (skip page-load detectors) | REST clients don't have browser behavior |
| `www.example.com` | `default` (standard) | Public site -balanced detection |

## Automatic HTTPS

This example uses ACME auto-cert (Let's Encrypt). Set `DOMAIN` and `ACME_EMAIL` in `.env`:

```
DOMAIN=example.com
ACME_EMAIL=ops@example.com
ADMIN_SECRET=your-secret-here
```

The gateway will automatically obtain certificates for all three subdomains on first start.
Certificates renew automatically before expiry.

## Adding More Virtual Hosts

1. Add a new route to `config/yarp.json` with the host match condition.
2. Add a new cluster pointing at the new service.
3. Add the new service to `docker-compose.yml`.

## Per-Host Policies

StyloBot's `PathPolicies` applies globally. To use different policies per host,
add a dedicated YARP route per host with a path prefix that maps to a policy, or
use the `BotPolicyAttribute` in your backend to override per-endpoint.
```

- [ ] **Step 5: Commit**

```bash
git add src/Stylobot.Gateway/examples/multi-site/
git commit -m "feat(gateway): multi-site example with host-based routing and per-host policies"
```

---

### Task 7: README Restructure

**Files:**
- Modify: `src/Stylobot.Gateway/README.md`

The current README leads with a badge and a generic description. This task inserts two new sections after "Quick Start" and adds a Cookbook section. The existing content is preserved unchanged after these additions.

- [ ] **Step 1: Read the current README top section**

```bash
head -100 src/Stylobot.Gateway/README.md
```

- [ ] **Step 2: Replace the opening and add the differentiator + cookbook sections**

Replace the content from line 1 through the end of the `## Quick Start` section (approximately line 20) with the following, then append the rest of the existing file unchanged:

```markdown
# StyloBot Gateway

A Docker-first YARP reverse proxy with built-in bot detection. Drop it in front of any
HTTP backend to start detecting and blocking bots -no code changes required.

Unlike Caddy or nginx, StyloBot knows what your traffic **is**. Every proxied request
carries detection signals your backend and routing rules can act on.

[![Docker Hub](https://img.shields.io/docker/pulls/scottgal/stylobot-gateway?label=Docker%20Hub)](https://hub.docker.com/r/scottgal/stylobot-gateway)

## Quick Start (30 Seconds)

```bash
docker run -p 8080:8080 -e DEFAULT_UPSTREAM=http://your-backend:3000 scottgal/stylobot-gateway
```

Every request is now analyzed. Check the logs:

```
╔══════════════════════════════════════════════════════════╗
║  StyloBot Gateway  v0.6.0                                ║
╠══════════════════════════════════════════════════════════╣
║  HTTP   :8080                                            ║
║  HTTPS  disabled                                         ║
║  Route  http://your-backend:3000  [catch-all]            ║
║  Policy  throttle-stealth  |  threshold  0.70            ║
║  Admin  /admin  [no secret - disabled]                   ║
╚══════════════════════════════════════════════════════════╝
[WARN] ADMIN_SECRET not set -admin API is disabled until configured
```

---

## Why Not Just Caddy?

Caddy terminates TLS and routes traffic. StyloBot does that -and also classifies every
request as bot or human using up to 49 detectors running in <1ms.

The result: YARP routing rules can make decisions based on **what traffic is**, not just
where it came from.

Every request your backend receives includes these headers:

| Header | Example | What it means |
|--------|---------|---------------|
| `X-Bot-Detected` | `true` | Bot detected |
| `X-Bot-Confidence` | `0.94` | Confidence score (0–1) |
| `X-Bot-Detection-RiskBand` | `High` | `Low`, `Elevated`, `Medium`, `High` |
| `X-Bot-Type` | `Scraper` | Bot category |
| `X-Bot-Name` | `AhrefsBot` | Named bot if identified |
| `X-Is-Malicious-Bot` | `true` | Convenience flag |
| `X-Is-Search-Engine` | `false` | Convenience flag -never block these |

Route bots to a different backend based on `X-Bot-Detection-RiskBand`:

```json
{
  "Routes": {
    "bot-traffic": {
      "ClusterId": "bot-sink",
      "Match": {
        "Path": "{**catch-all}",
        "Headers": [{ "Name": "X-Bot-Detection-RiskBand", "Values": ["High"], "Mode": "ExactHeader" }]
      },
      "Order": 1
    },
    "human-traffic": {
      "ClusterId": "real-backend",
      "Match": { "Path": "{**catch-all}" },
      "Order": 2
    }
  }
}
```

See the cookbook below for complete working examples.

---

## Cookbook

Complete copy-paste examples for common scenarios. Each includes `docker-compose.yml`,
`yarp.json`, and `appsettings.json`.

| Scenario | Use when |
|----------|----------|
| [WordPress Protection](examples/wordpress/) | Protecting wp-login.php, xmlrpc.php, wp-admin |
| [E-commerce / Scraper Defense](examples/ecommerce/) | Price scrapers, inventory bots, checkout abuse |
| [API Protection](examples/api-protection/) | Credential stuffing, API key probing |
| [Shadow Mode](examples/shadow-mode/) | Monitor traffic for 7 days before blocking |
| [Multi-Site / SaaS](examples/multi-site/) | Multiple virtual hosts, per-host policies, ACME HTTPS |

---

```

Then append the remaining sections of the original README starting from `## Deployment Tiers`.

- [ ] **Step 3: Verify the README renders correctly**

```bash
wc -l src/Stylobot.Gateway/README.md
head -120 src/Stylobot.Gateway/README.md
```

Expected: file is longer than before, starts with the new content, contains `## Deployment Tiers` after the cookbook table.

- [ ] **Step 4: Commit**

```bash
git add src/Stylobot.Gateway/README.md
git commit -m "docs(gateway): add why-not-caddy differentiator and cookbook to README"
```

---

### Task 8: Final Build Verification

**Files:** No new files.

- [ ] **Step 1: Build the project**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.  0 Error(s)`

- [ ] **Step 2: Run gateway and verify banner**

```bash
lsof -ti :9080 | xargs kill -9 2>/dev/null; sleep 1
DEFAULT_UPSTREAM=http://httpbin.org \
GATEWAY_HTTP_PORT=9080 \
dotnet run --project src/Stylobot.Gateway --no-launch-profile 2>&1 | head -20
```

Expected: banner with HTTP :9080, Route http://httpbin.org [catch-all], WARN for no ADMIN_SECRET.

- [ ] **Step 3: Verify examples directory structure**

```bash
find src/Stylobot.Gateway/examples -type f | sort
```

Expected output:
```
src/Stylobot.Gateway/examples/api-protection/README.md
src/Stylobot.Gateway/examples/api-protection/config/appsettings.json
src/Stylobot.Gateway/examples/api-protection/config/yarp.json
src/Stylobot.Gateway/examples/api-protection/docker-compose.yml
src/Stylobot.Gateway/examples/ecommerce/README.md
src/Stylobot.Gateway/examples/ecommerce/config/appsettings.json
src/Stylobot.Gateway/examples/ecommerce/config/yarp.json
src/Stylobot.Gateway/examples/ecommerce/docker-compose.yml
src/Stylobot.Gateway/examples/multi-site/README.md
src/Stylobot.Gateway/examples/multi-site/config/appsettings.json
src/Stylobot.Gateway/examples/multi-site/config/yarp.json
src/Stylobot.Gateway/examples/multi-site/docker-compose.yml
src/Stylobot.Gateway/examples/shadow-mode/README.md
src/Stylobot.Gateway/examples/shadow-mode/config/appsettings.json
src/Stylobot.Gateway/examples/shadow-mode/docker-compose.yml
src/Stylobot.Gateway/examples/wordpress/README.md
src/Stylobot.Gateway/examples/wordpress/config/appsettings.json
src/Stylobot.Gateway/examples/wordpress/config/yarp.json
src/Stylobot.Gateway/examples/wordpress/docker-compose.yml
```

- [ ] **Step 4: Commit spec and plan docs**

```bash
git add docs/superpowers/specs/2026-05-07-gateway-ux-cookbook-design.md \
        docs/superpowers/plans/2026-05-07-gateway-ux-cookbook.md
git commit -m "docs: gateway ux cookbook spec and implementation plan"
```