# Bot Detection Format (BDF) v2 - Schema

## Improved Schema for Realistic Scraper Simulation

```json
{
  "scenarioName": "kebab-case-identifier",
  "scenario": "Human-readable description",
  "confidence": 0.92,

  "clientProfile": {
    "userAgent": "exact-user-agent-string",
    "cookieMode": "none | stateless | sticky",
    "headerCompleteness": "minimal | partial | full",
    "clientHintsPresent": false,
    "robotsConsulted": false,
    "protocol": "http/1.1 | http/2"
  },

  "timingProfile": {
    "burstRequests": 20,
    "delayAfterMs": { "min": 10, "max": 90 },
    "pauseAfterBurstMs": { "min": 1500, "max": 4500 }
  },

  "requests": [
    {
      "method": "GET | POST | HEAD | OPTIONS",
      "path": "/api/endpoint",
      "headers": {
        "User-Agent": "...",
        // Optional: missing headers intentionally
      },
      "expectedStatusAny": [200, 301, 403, 404, 429],
      "expectedOutcome": "data_exfil | probing | indexing | auth_bypass_attempt",
      "successCondition": "any 2xx with payload > 10KB",
      "note": "Optional explanation"
    }
  ],

  "patterns": {
    "requestInterval": "bursty, p95 < 100ms",
    "pathSequence": "description",
    "userAgentPattern": "description",
    "statefulness": "no cookies; missing headers",
    "statusCodePattern": "mix of 2xx/3xx/4xx/429"
  },

  "labels": ["Scraper", "RobotsIgnore", "SensitiveProbing"],
  "evidence": [
    { "signal": "interval_ms_p95", "op": "<", "value": 100, "weight": 0.35 },
    { "signal": "sensitive_path_rate", "op": ">", "value": 0.4, "weight": 0.30 }
  ],

  "reasoning": "Why this is bot/human behavior"
}
```

## Key Improvements:

### 1. Client Profile
- **cookieMode**: Models cookie jar behavior
- **headerCompleteness**: How many headers are present
- **clientHintsPresent**: Sec-CH-UA-* headers
- **robotsConsulted**: Did it check robots.txt?

### 2. Timing Profile
- **Bursts + Jitter**: Not fixed delays
- **burstRequests**: How many in a burst
- **delayAfterMs range**: Random jitter
- **pauseAfterBurstMs**: Pause between bursts

### 3. Request Intent
- **expectedStatusAny**: Accept multiple statuses
- **expectedOutcome**: What bot is trying to achieve
- **successCondition**: When bot considers it successful

### 4. Structured Evidence
- Machine-readable signal contributions
- Weighted evidence for detection
