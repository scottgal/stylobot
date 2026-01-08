# Mostlylucid.BotDetection.Holodeck - Feature Specification

## Overview

The **Holodeck** extension provides a honeypot ecosystem for BotDetection that:

1. Redirects detected bots to fake API endpoints powered by LLM (using `mostlylucid.mockllmapi`)
2. Detects when bots follow hidden honeypot links
3. Reports malicious IPs back to Project Honeypot (contributing to the community database)

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Request Flow                                │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Request ──► BotDetection Middleware                                │
│                    │                                                │
│                    ▼                                                │
│              ┌─────────────────┐                                    │
│              │ HoneypotLink    │  "Did they follow a hidden link?" │
│              │ Contributor     │                                    │
│              └────────┬────────┘                                    │
│                       │                                             │
│                       ▼                                             │
│              ┌─────────────────┐                                    │
│              │ Risk Threshold  │  "Is risk > 0.7?"                 │
│              │ Check           │                                    │
│              └────────┬────────┘                                    │
│                       │                                             │
│          ┌────────────┴────────────┐                               │
│          │                         │                                │
│          ▼ (Low Risk)              ▼ (High Risk)                   │
│    ┌───────────┐            ┌─────────────────┐                    │
│    │ Continue  │            │ HolodeckAction  │                    │
│    │ to Real   │            │ Policy          │                    │
│    │ Backend   │            └────────┬────────┘                    │
│    └───────────┘                     │                             │
│                                      ▼                             │
│                         ┌─────────────────────┐                    │
│                         │ MockLLMApi          │                    │
│                         │ (Fake API World)    │                    │
│                         └────────┬────────────┘                    │
│                                  │                                  │
│                                  ▼                                  │
│                         ┌─────────────────────┐                    │
│                         │ HoneypotReporter    │                    │
│                         │ (→ Project Honeypot)│                    │
│                         └─────────────────────┘                    │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Components

### 1. HolodeckActionPolicy

**Purpose:** Redirect detected bots to fake API endpoints powered by MockLLMApi.

**Features:**

- [x] Configurable redirect to MockLLMApi endpoints
- [ ] Context key based on fingerprint/IP for consistent fake world per bot
- [ ] Mode selection: realistic, realistic-but-useless, chaos, strict-schema
- [ ] MaxStudyRequests counter before hard-blocking
- [ ] URL template placeholders: {fingerprint}, {risk}, {originalPath}

**Configuration:**

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "holodeck": {
        "Type": "Holodeck",
        "MockApiBaseUrl": "http://localhost:5116/api/mock",
        "Mode": "realistic-but-useless",
        "ContextSource": "Fingerprint",
        "MaxStudyRequests": 50
      }
    }
  }
}
```

### 2. HoneypotLinkContributor

**Purpose:** Detect when bots follow hidden honeypot links that real users would never click.

**Features:**

- [ ] Configurable honeypot paths (e.g., `/admin-secret`, `/wp-login.php`)
- [ ] Hidden link injection into responses (CSS hidden, tiny font, etc.)
- [ ] Referer header analysis (came from our honeypot page?)
- [ ] Immediate high-confidence bot detection when honeypot is triggered
- [ ] Signal tracking for follow-up analysis

**Configuration:**

```json
{
  "BotDetection": {
    "Holodeck": {
      "HoneypotPaths": ["/admin-secret", "/wp-login.php", "/.env", "/xmlrpc.php"],
      "HiddenLinkSelector": ".honeypot-link",
      "InjectHiddenLinks": true
    }
  }
}
```

### 3. HoneypotReporter

**Purpose:** Report confirmed malicious IPs back to Project Honeypot, contributing to the community database.

**Features:**

- [ ] Project Honeypot http:BL submission API integration
- [ ] Configurable reporting thresholds (only report high-confidence bots)
- [ ] Rate limiting to avoid flooding
- [ ] Visitor type classification (harvester, comment spammer, suspicious)
- [ ] Async background reporting queue

**Configuration:**

```json
{
  "BotDetection": {
    "Holodeck": {
      "ReportToProjectHoneypot": true,
      "ProjectHoneypotAccessKey": "your-key",
      "MinRiskToReport": 0.85,
      "ReportVisitorTypes": ["Harvester", "CommentSpammer"]
    }
  }
}
```

### 4. HoneypotMiddleware (HTML Injection)

**Purpose:** Inject hidden honeypot links into HTML responses to trap crawlers.

**Features:**

- [ ] Middleware that modifies HTML responses
- [ ] Configurable injection strategies (CSS hidden, off-screen, tiny font)
- [ ] Random link generation to avoid pattern detection
- [ ] Only inject for text/html content types

---

## Implementation Checklist

### Phase 1: Project Setup

- [x] Create project structure (csproj, directories)
- [ ] Add ReleaseNotes.txt
- [ ] Add README.md documentation
- [ ] Add to solution file

### Phase 2: Core Components

- [ ] **HolodeckOptions.cs** - Configuration model
- [ ] **HolodeckActionPolicy.cs** - Action policy implementation
- [ ] **HolodeckActionPolicyFactory.cs** - Factory for configuration-based creation

### Phase 3: Honeypot Detection

- [ ] **HoneypotLinkContributor.cs** - Contributor for honeypot path detection
- [ ] **HoneypotPathMatcher.cs** - Pattern matching for common honeypot paths

### Phase 4: Project Honeypot Integration

- [ ] **HoneypotReporter.cs** - Background service for reporting
- [ ] **ProjectHoneypotClient.cs** - HTTP:BL submission client
- [ ] **ReportingQueue.cs** - Async queue for batched reporting

### Phase 5: HTML Injection (Optional)

- [ ] **HoneypotInjectionMiddleware.cs** - HTML response modification
- [ ] **LinkInjector.cs** - Hidden link generation strategies

### Phase 6: DI & Configuration

- [ ] **ServiceCollectionExtensions.cs** - AddHolodeck() extension
- [ ] **ApplicationBuilderExtensions.cs** - UseHolodeck() extension

### Phase 7: Testing

- [ ] Unit tests for HolodeckActionPolicy
- [ ] Unit tests for HoneypotLinkContributor
- [ ] Integration tests with MockLLMApi
- [ ] Test mode simulation support

---

## Dependencies

| Package                  | Version | Purpose                        |
|--------------------------|---------|--------------------------------|
| Mostlylucid.BotDetection | latest  | Core bot detection             |
| mostlylucid.mockllmapi   | 2.1.0   | LLM-powered fake API responses |

---

## Usage Example

```csharp
// Program.cs
builder.Services.AddBotDetection();
builder.Services.AddHolodeck(options =>
{
    options.MockApiBaseUrl = "http://localhost:5116/api/mock";
    options.Mode = HolodeckMode.RealisticButUseless;
    options.ReportToProjectHoneypot = true;
    options.ProjectHoneypotAccessKey = "your-key";
});

// ...

app.UseBotDetection();
app.UseHolodeck(); // Optional: for HTML injection
```

```json
// appsettings.json
{
  "BotDetection": {
    "ActionPolicies": {
      "holodeck": {
        "Type": "Holodeck",
        "MockApiBaseUrl": "http://localhost:5116/api/mock",
        "Mode": "realistic-but-useless"
      }
    },
    "DetectionPolicies": {
      "default": {
        "ActionPolicyName": "allow",
        "Transitions": [
          { "WhenRiskExceeds": 0.5, "ActionPolicyName": "holodeck" },
          { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block" }
        ]
      }
    }
  }
}
```

---

## Future Enhancements

1. **GraphQL Honeypot** - Redirect GraphQL queries to MockLLMApi's GraphQL endpoint
2. **SignalR Trap** - Keep WebSocket connections alive with fake streaming data
3. **Adaptive Learning** - Learn bot patterns from holodeck interactions
4. **IP Reputation Sharing** - Share detected IPs across multiple instances
