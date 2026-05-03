# Mostlylucid.BotDetection.ApiHolodeck

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.BotDetection.ApiHolodeck.svg)](https://www.nuget.org/packages/Mostlylucid.BotDetection.ApiHolodeck/)

A honeypot extension for [Mostlylucid.BotDetection](../Mostlylucid.BotDetection/) that creates a fake API "holodeck" for
detected bots.

## What It Does

Instead of simply blocking detected bots, ApiHolodeck redirects them to **fake API endpoints** that return
realistic-looking but useless data. This:

- **Wastes bot resources** - They scrape fake data instead of your real content
- **Studies bot behavior** - You learn what they're looking for
- **Contributes to threat intelligence** - Reports malicious IPs to Project Honeypot

## Quick Start

```bash
# Install the package
dotnet add package Mostlylucid.BotDetection.ApiHolodeck
```

```csharp
// Program.cs
builder.Services.AddBotDetection();
builder.Services.AddApiHolodeck(options =>
{
    options.MockApiBaseUrl = "http://localhost:5116/api/mock";
    options.Mode = HolodeckMode.RealisticButUseless;
});

app.UseBotDetection();
```

## Requirements

- **Mostlylucid.BotDetection** - Core bot detection (installed automatically)
- **MockLLMApi server** - For generating fake responses (requires Ollama)

### Setting Up MockLLMApi

1. Install Ollama: https://ollama.ai/
2. Pull a model: `ollama pull gemma3:4b`
3. Run MockLLMApi (Docker or NuGet package)

```bash
# Docker
docker run -p 5116:5116 -e OLLAMA_URL=http://host.docker.internal:11434 scottgal/mockllmapi

# Or add to your app
dotnet add package mostlylucid.mockllmapi
```

```csharp
// Add MockLLMApi to your app
builder.Services.AddLLMockApi(config =>
{
    config.OllamaUrl = "http://localhost:11434";
    config.DefaultModel = "gemma3:4b";
});
app.MapLLMockApi("/api/mock");
```

## Components

### 1. HolodeckActionPolicy

Redirects detected bots to MockLLMApi instead of your real backend.

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "holodeck": {
        "Type": "Holodeck",
        "MockApiBaseUrl": "http://localhost:5116/api/mock",
        "Mode": "realistic-but-useless",
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

### 2. HoneypotLinkContributor

Detects when bots access trap paths that real users would never visit.

Built-in honeypot paths include:

- `/wp-login.php`, `/wp-admin` - WordPress probes
- `/.env`, `/config.php` - Config file access
- `/.git/config` - Version control exposure
- `/phpmyadmin`, `/adminer.php` - Database admin
- `/backup.sql`, `/dump.sql` - Database dumps

Any access to these paths = **instant high-confidence bot detection**.

### 3. HoneypotReporter

Reports malicious IPs to threat intelligence services (Project Honeypot, AbuseIPDB).

```json
{
  "BotDetection": {
    "Holodeck": {
      "ReportToProjectHoneypot": true,
      "ProjectHoneypotAccessKey": "your-key",
      "MinRiskToReport": 0.85
    }
  }
}
```

## Holodeck Modes

| Mode                  | Description                               |
|-----------------------|-------------------------------------------|
| `Realistic`           | Generate believable fake data             |
| `RealisticButUseless` | Fake data with wrong schemas, demo values |
| `Chaos`               | Random errors, timeouts, inconsistencies  |
| `StrictSchema`        | OpenAPI-based structured fakes            |
| `Adversarial`         | Mix of all tactics                        |

## Configuration

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
        "/.env"
      ],

      "ReportToProjectHoneypot": false,
      "ProjectHoneypotAccessKey": "",
      "MinRiskToReport": 0.85,
      "MaxReportsPerHour": 100
    }
  }
}
```

## How It Works

```
Request -> BotDetection
              |
              +-- Low Risk -> Real Backend
              |
              +-- High Risk -> HolodeckActionPolicy
                                    |
                                    +-- Build context key (fingerprint/IP)
                                    |
                                    +-- Forward to MockLLMApi
                                    |       +-- /api/mock/{original-path}?context={key}
                                    |
                                    +-- Return LLM-generated fake response
                                            +-- Bot thinks it's real data!
```

Each bot gets a **consistent fake world** based on their fingerprint. If they make 10 requests, they get coherent (but
fake) responses. This makes it harder to detect they're being sandboxed.

## Context Keys

The `ContextSource` setting determines how bots are identified:

| Source        | Description                                |
|---------------|--------------------------------------------|
| `Fingerprint` | Browser/client fingerprint (most accurate) |
| `Ip`          | IP address only                            |
| `Session`     | Session ID                                 |
| `Combined`    | IP + Fingerprint                           |

## Study Cutoff

After `MaxStudyRequests`, the bot is hard-blocked. This prevents infinite resource consumption while still gathering
useful intelligence about their scraping patterns.

## Example: API Protection

```csharp
// Protect your API with a holodeck fallback
builder.Services.AddBotDetection(options =>
{
    options.BotThreshold = 0.6; // Lower threshold for holodeck
    options.BlockDetectedBots = false; // Don't block, redirect to holodeck
});

builder.Services.AddApiHolodeck(options =>
{
    options.Mode = HolodeckMode.Adversarial;
    options.MaxStudyRequests = 100;
});
```

Legitimate users get your real API. Bots get a fake one that wastes their time.

## Full YarpGateway Integration

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway
    ports:
      - "8080:8080"
    environment:
      - DEFAULT_UPSTREAM=https://your-api.com
      - BOTDETECTION__HOLODECK__MOCKAPIBASEURL=http://mockllmapi:5116/api/mock
      - BOTDETECTION__HOLODECK__MODE=RealisticButUseless
    depends_on:
      - mockllmapi

  mockllmapi:
    image: scottgal/mockllmapi
    environment:
      - OLLAMA_URL=http://host.docker.internal:11434
```

## License

[Unlicense](https://unlicense.org/) - Public Domain
