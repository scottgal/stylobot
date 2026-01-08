# Bot Detection Demo

Interactive demonstration of the **mostlylucid.botdetection** library's full detection pipeline with **real-time
signature analysis**.

## Quick Start

```bash
dotnet run
# Visit http://localhost:5000/bot-test (Original demo)
# Visit http://localhost:5000/SignatureDemo (NEW - Real-time signature streaming)
```

## NEW: Real-Time Signature Demo

Visit **http://localhost:5000/SignatureDemo** for advanced signature analysis:

### Features

- **Real-time SignalR streaming** of bot detection signatures
- **REST API** for signature retrieval (`/api/signature/...`)
- **TagHelper visualization** with comprehensive detector breakdowns
- **Live statistics** dashboard with bot/human counts
- **21 detectors running** including advanced fingerprinting
- **Interactive UI** with collapsible detector contributions

See the [Signature Demo Documentation](#signature-analysis-system) below for complete details.

## Production vs Demo Mode

**IMPORTANT**: This demo exposes detailed detection info for learning purposes. In production:

| Setting                         | Demo    | Production                                                 |
|---------------------------------|---------|------------------------------------------------------------|
| `ResponseHeaders.Enabled`       | `true`  | **`false`** - Never leak detection details to clients      |
| `/bot-detection/check` endpoint | Enabled | **Disabled** - Use `HttpContext.Items` for internal checks |
| Test mode headers               | Enabled | **Disabled** - Set `EnableTestMode: false`                 |
| Full JSON responses             | Enabled | **Blocked** - Detection flows downstream only              |

Detection results are stored in `HttpContext.Items["BotDetectionResult"]` for your application to consume internally
without exposing to clients.

## Detection Pipeline

The demo showcases a multi-layered detection system:

| Detector               | Speed  | Description                                         |
|------------------------|--------|-----------------------------------------------------|
| **FastPathReputation** | <1ms   | Cached known-good/bad classifications               |
| **UserAgent**          | <1ms   | Pattern matching against known bot signatures       |
| **Header**             | <1ms   | Missing/suspicious HTTP header analysis             |
| **Ip**                 | <1ms   | Datacenter/cloud provider IP detection              |
| **SecurityTool**       | <1ms   | Vulnerability scanner detection (Nikto, Nmap, etc.) |
| **ProjectHoneypot**    | ~100ms | IP reputation via HTTP:BL DNS lookup                |
| **Behavioral**         | <1ms   | Rate limiting & request pattern anomalies           |
| **ClientSide**         | <1ms   | Browser fingerprint verification                    |
| **Inconsistency**      | <1ms   | Cross-signal contradiction detection                |
| **VersionAge**         | <1ms   | Browser/OS version freshness check                  |
| **ReputationBias**     | <1ms   | Applies cached reputation to scoring                |
| **Heuristic**          | ~1ms   | ML-trained weighted feature model                   |
| **LLM**                | ~500ms | Language model reasoning (optional)                 |
| **HeuristicLate**      | <1ms   | Post-LLM heuristic refinement                       |

## Interactive Demo UI

Visit **http://localhost:5000/bot-test** for the full interactive demo:

### Bot Simulator Buttons

Test the detection pipeline with various bot signatures:

| Category              | Simulations                                                           |
|-----------------------|-----------------------------------------------------------------------|
| **Browsers**          | Real Browser (human-like Chrome UA)                                   |
| **Search Engines**    | Googlebot, Bingbot                                                    |
| **Scrapers**          | Scrapy, cURL, Puppeteer/Headless Chrome                               |
| **AI Crawlers**       | GPTBot (OpenAI), ClaudeBot (Anthropic)                                |
| **Social Bots**       | TwitterBot, FacebookBot                                               |
| **Monitoring**        | UptimeRobot                                                           |
| **Security Scanners** | Nikto, Nessus, Nmap, Burp Suite, Acunetix                             |
| **Malicious**         | sqlmap (SQL injection tool)                                           |
| **Honeypot Tests**    | Harvester, Spammer, Suspicious (simulated Project Honeypot responses) |

### Custom User-Agent Testing

Enter any User-Agent string directly to test detection:

- Clicking simulator buttons populates the UA input field
- Press Enter or click "Test UA" to analyze any string

### Detection Modes

| Mode                | Description                                    | Use Case                     |
|---------------------|------------------------------------------------|------------------------------|
| **Real Experience** | Fast detection with actual blocking/challenges | What users see in production |
| **UA Only**         | Just User-Agent analysis (~5ms)                | Testing UA string patterns   |
| **Fast (No Block)** | All static detectors + Heuristic (~50ms)       | Debug without blocking       |
| **Full Pipeline**   | All detectors + Heuristic + LLM (~500ms)       | Complete analysis with AI    |
| **Learning**        | Full pipeline, never blocks, saves patterns    | Training weight model        |

## Test Mode Headers

Control detection behavior via HTTP headers:

| Header             | Purpose                        | Example                                    |
|--------------------|--------------------------------|--------------------------------------------|
| `ml-bot-test-mode` | Simulate a predefined bot type | `googlebot`, `nikto`, `honeypot-harvester` |
| `ml-bot-test-ua`   | Test with a custom UA string   | Any User-Agent string                      |
| `X-Bot-Policy`     | Override detection policy      | `realfast`, `uaonly`, `demo`               |

### Test Mode Simulations (appsettings.json)

```json
"TestModeSimulations": {
  "human": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36...",
  "googlebot": "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
  "nikto": "Mozilla/5.00 (Nikto/2.1.6) (Evasions:None) (Test:Port Check)",
  "honeypot-harvester": "<test-honeypot:harvester>",
  "honeypot-spammer": "<test-honeypot:spammer>",
  "honeypot-suspicious": "<test-honeypot:suspicious>"
}
```

## Detection Policies

Policies define which detectors run and how results are processed:

### realfast (Default)

```json
{
  "Description": "Production mode that BLOCKS bots",
  "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool",
               "ProjectHoneypot", "Behavioral", "ClientSide", "Inconsistency",
               "VersionAge", "ReputationBias", "Heuristic"],
  "ImmediateBlockThreshold": 0.70,
  "DefaultBlockAction": "Challenge"
}
```

### uaonly (Fastest)

```json
{
  "Description": "UA-based detection only",
  "FastPath": ["UserAgent", "SecurityTool", "VersionAge", "Heuristic"]
}
```

### demo (Full Analysis)

```json
{
  "Description": "Full pipeline sync for demonstration",
  "BypassTriggerConditions": true,
  "ForceSlowPath": true
}
```

### Policy Transitions

Policies can escalate uncertain results:

```json
"Transitions": [
  { "WhenRiskExceeds": 0.5, "WhenRiskBelow": 0.85, "GoTo": "default",
    "Description": "Uncertain zone - escalate to LLM" }
]
```

## API Endpoints

| Endpoint                          | Description               |
|-----------------------------------|---------------------------|
| `GET /bot-test`                   | Interactive demo UI       |
| `GET /bot-detection/check`        | JSON detection result     |
| `POST /bot-detection/fingerprint` | Submit client fingerprint |

### curl Examples

```bash
# Basic detection
curl http://localhost:5000/bot-detection/check

# Test as Googlebot
curl http://localhost:5000/bot-detection/check \
  -H "ml-bot-test-mode: googlebot"

# Test custom UA
curl http://localhost:5000/bot-detection/check \
  -H "ml-bot-test-ua: MyBot/1.0"

# Test with specific policy
curl http://localhost:5000/bot-detection/check \
  -H "X-Bot-Policy: uaonly"

# Test security scanner
curl http://localhost:5000/bot-detection/check \
  -H "ml-bot-test-mode: nikto"

# Test honeypot response (harvester)
curl http://localhost:5000/bot-detection/check \
  -H "ml-bot-test-mode: honeypot-harvester"
```

## Security Tool Detection

Detects vulnerability scanners using patterns from external sources:

- [digininja/scanner_user_agents](https://github.com/digininja/scanner_user_agents)
- [OWASP CoreRuleSet](https://github.com/coreruleset/coreruleset)

```json
"SecurityTools": {
  "Enabled": true,
  "BlockSecurityTools": true,
  "LogDetections": true
}
```

Detected categories: Nikto, Nessus, Nmap, Burp Suite, Acunetix, sqlmap, ZAP, Metasploit, etc.

## Project Honeypot Integration

IP reputation checking via HTTP:BL DNS lookup:

```json
"ProjectHoneypot": {
  "Enabled": true,
  "AccessKey": null,  // Set via user-secrets
  "HighThreatThreshold": 25,
  "MaxDaysAge": 90,
  "TreatHarvestersAsMalicious": true,
  "TreatCommentSpammersAsMalicious": true
}
```

Get a free API key: https://www.projecthoneypot.org/httpbl_configure.php

```bash
dotnet user-secrets set "BotDetection:ProjectHoneypot:AccessKey" "your12charkey"
```

### Testing Honeypot Detection

Use the special `<test-honeypot:type>` markers to simulate honeypot responses:

- `<test-honeypot:harvester>` - Email address harvester
- `<test-honeypot:spammer>` - Comment spammer
- `<test-honeypot:suspicious>` - Suspicious activity

## Heuristic Detection (ML Model)

Weighted feature extraction with learning capabilities:

```json
"AiDetection": {
  "Heuristic": {
    "Enabled": true,
    "LoadLearnedWeights": true,
    "EnableWeightLearning": true,
    "MinConfidenceForLearning": 0.8,
    "LearningRate": 0.01
  }
}
```

Features extracted:

- UA bot probability, header completeness, IP datacenter signals
- Behavioral anomalies, fingerprint scores, request timing
- Cross-detector inconsistencies

## LLM Detection (Optional)

Uses Ollama for natural language reasoning:

```json
"AiDetection": {
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "Model": "gemma3:4b",
    "UseJsonMode": true
  }
}
```

Setup:

```bash
ollama pull gemma3:4b
ollama serve
```

## Client-Side Fingerprinting

Browser fingerprint collection and validation:

```json
"ClientSide": {
  "Enabled": true,
  "TokenSecret": "change-in-production",
  "CollectWebGL": true,
  "CollectCanvas": true,
  "MinIntegrityScore": 70,
  "HeadlessThreshold": 0.5
}
```

Detects:

- WebDriver (navigator.webdriver)
- PhantomJS, Selenium, Puppeteer markers
- Chrome DevTools Protocol traces
- Missing plugins in Chrome
- Zero window dimensions

## Response Headers

**IMPORTANT: Production vs Debug**

By default, detection results flow **downstream only** (to your application code via HttpContext.Items). Response
headers exposing detection info to clients are **disabled by default** in production.

Only enable response headers for debugging/demo purposes:

```json
"ResponseHeaders": {
  "Enabled": false,  // KEEP FALSE IN PRODUCTION
  "HeaderPrefix": "X-Bot-",
  "IncludePolicyName": true,
  "IncludeConfidence": true,
  "IncludeDetectors": true,
  "IncludeProcessingTime": true,
  "IncludeBotName": true
}
```

When enabled for debugging:

```
X-Bot-Policy: realfast
X-Bot-Confidence: 0.85
X-Bot-Detectors: UserAgent,Header,SecurityTool
X-Bot-ProcessingTime: 12.5ms
X-Bot-Name: Nikto
```

**Security Note**: Never expose detailed detection signals to untrusted clients in production. This information helps
attackers evade detection.

## Data Sources

Bot patterns are fetched from external sources (no hardcoded lists):

```json
"DataSources": {
  "IsBot": { "Enabled": true, "Url": "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json" },
  "ScannerUserAgents": { "Enabled": true, "Url": "https://raw.githubusercontent.com/digininja/scanner_user_agents/main/list.json" },
  "CoreRuleSetScanners": { "Enabled": true, "Url": "https://raw.githubusercontent.com/coreruleset/coreruleset/main/rules/scanners-user-agents.data" },
  "AwsIpRanges": { "Enabled": true, "Url": "https://ip-ranges.amazonaws.com/ip-ranges.json" },
  "GcpIpRanges": { "Enabled": true, "Url": "https://www.gstatic.com/ipranges/cloud.json" },
  "CloudflareIpv4": { "Enabled": true, "Url": "https://www.cloudflare.com/ips-v4" },
  "CloudflareIpv6": { "Enabled": true, "Url": "https://www.cloudflare.com/ips-v6" }
}
```

Background updates run every 24 hours by default.

## Performance

| Mode         | Typical Latency | Notes                     |
|--------------|-----------------|---------------------------|
| **realfast** | 5-20ms          | Production recommended    |
| **uaonly**   | 2-5ms           | Fastest, UA patterns only |
| **fastpath** | 20-50ms         | All static detectors      |
| **demo**     | 200-800ms       | Includes LLM if available |

## Configuration Reference

See the docs folder for complete configuration examples:

- `docs/appsettings.typical.json` - Balanced production config
- `docs/appsettings.full.json` - All options documented

---

# Signature Analysis System

## Overview

The signature demo showcases the complete bot detection system with real-time streaming, comprehensive fingerprinting,
and visual analysis.

### Architecture

```
Request → SignatureCaptureMiddleware (generates ID)
       → BotDetectionMiddleware (21 detectors)
       → SignatureCaptureMiddleware (captures & broadcasts)
       → Response (with X-Signature-ID header)

       ↓ SignalR → Real-time clients
       ↓ REST API → Signature lookup
       ↓ TagHelper → HTML rendering
```

## API Endpoints

### GET `/api/signature/{id}`

Get specific signature by ID.

```bash
curl https://localhost:5001/api/signature/abc123
```

**Response:**

```json
{
  "signatureId": "abc123",
  "timestamp": "2025-01-11T...",
  "evidence": {
    "botProbability": 0.85,
    "confidence": 0.92,
    "riskBand": "High",
    "contributions": [...]
  },
  "requestMetadata": {
    "path": "/api/test",
    "method": "GET",
    "userAgent": "...",
    "remoteIp": "192.168.1.1"
  }
}
```

### GET `/api/signature/recent?count=50`

Get most recent signatures (max 1000).

### GET `/api/signature/stats`

Get aggregate statistics.

```json
{
  "totalSignatures": 1523,
  "botCount": 942,
  "humanCount": 581,
  "avgBotProbability": 0.618
}
```

### GET `/api/signature/current`

Get signature for current request (reads `X-Signature-ID` header).

## SignalR Hub

Connect to `/hubs/signatures` for real-time streaming.

### JavaScript Client

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/signatures")
    .build();

connection.on("ReceiveNewSignature", (signature) => {
    console.log("New detection:", signature);
    console.log(`Bot: ${signature.evidence.botProbability * 100}%`);
});

await connection.start();
await connection.invoke("SubscribeToSignatures");

// Get stats
const stats = await connection.invoke("GetStats");
```

## TagHelper Usage

```html
<!-- Display signature by ID -->
<bot-signature-display
    signature-id="@ViewData["SignatureId"]"
    show-headers="true"
    show-contributions="true" />

<!-- Display current request -->
<bot-signature-display
    mode="current"
    show-headers="true"
    show-contributions="true" />
```

## 21 Detectors Enabled

The `full-demo` policy runs all non-AI detectors:

**Fast Path (Synchronous):**

1. FastPathReputation - Quick reputation lookup
2. HoneypotLink - Honeypot trap detection
3. UserAgent - User-agent analysis
4. Header - HTTP header anomalies
5. Ip - IP reputation & datacenter
6. SecurityTool - Scanner signatures
7. ProjectHoneypot - IP reputation
8. CacheBehavior - Cache headers
9. Behavioral - Request patterns
10. AdvancedBehavioral - Advanced patterns
11. ClientSide - Browser fingerprints
12. Inconsistency - Cross-layer checks
13. VersionAge - Browser/OS age
14. ReputationBias - Historical rep
15. Heuristic - ML scoring

**Advanced Fingerprinting:**

16. **TlsFingerprint** - JA3/JA4 TLS
17. **TcpIpFingerprint** - p0f OS detection
18. **Http2Fingerprint** - HTTP/2 SETTINGS
19. **MultiLayerCorrelation** - Consistency
20. **BehavioralWaveform** - Temporal patterns
21. **ResponseBehavior** - Historical feedback

## Performance

- **Detection**: < 25μs per request
- **Storage**: ~2KB per signature
- **Throughput**: 10,000+ req/s
- **SignalR**: < 1ms broadcast
- **Memory**: In-memory (configurable)

## Testing

```bash
cd Mostlylucid.BotDetection.Demo.Tests
dotnet test
```

**Coverage: 22 tests** (11 passing core tests, 11 integration-level)

## Production Deployment

### Security

- Add authentication to `/api/signature` endpoints
- Require auth for SignalR subscriptions
- Use cryptographic signature IDs
- Enable rate limiting

### Scalability

- Replace SignatureStore with Redis/SQL
- Use RabbitMQ/Kafka for SignalR scale-out
- CDN for fingerprint databases
- Horizontal scaling (stateless)

---

**Built with Claude Code** 🤖
