# YARP Gateway Demo Mode

## Overview

Demo mode enables **comprehensive bot detection with ALL headers** passed to downstream services for UI display and analysis. This is perfect for:

- **Live demonstrations** of bot detection capabilities
- **Development and testing** of UI components that consume detection data
- **Training and learning** about bot detection signals
- **Debugging** detection logic

⚠️ **DO NOT USE IN PRODUCTION** - Demo mode exposes detailed detection information that could help attackers evade detection.

---

## Quick Start

### Enable Demo Mode

**Option 1: Environment Variable (Recommended)**
```bash
docker run -d \
  -e GATEWAY_DEMO_MODE=true \
  -e DEFAULT_UPSTREAM=http://your-backend:8080 \
  -p 8080:8080 \
  mostlylucid-yarp-gateway
```

**Option 2: Configuration File**
```json
{
  "Gateway": {
    "DemoMode": {
      "Enabled": true
    }
  }
}
```

**Option 3: Docker Compose**
```yaml
services:
  yarp-gateway:
    image: mostlylucid-yarp-gateway
    environment:
      - GATEWAY_DEMO_MODE=true
      - DEFAULT_UPSTREAM=http://backend:8080
    ports:
      - "8080:8080"
```

---

## What Demo Mode Does

### 1. Switches to 'demo' Policy

Automatically activates the `demo` policy with **ALL 29 detectors** enabled:

**Fast Path Detectors:**
- FastPathReputation - Cached reputation
- HoneypotLink - Honeypot trap detection
- UserAgent - Bot UA patterns
- Header - HTTP header analysis
- Ip - Datacenter/cloud IP detection
- SecurityTool - Scanner signatures
- ProjectHoneypot - IP reputation (DNS lookup)
- CacheBehavior - Cache header analysis
- Behavioral - Rate limiting patterns
- AdvancedBehavioral - Advanced behavioral analysis
- ClientSide - Browser fingerprints
- **ResponseBehavior** - Historical feedback ⭐ NEW
- Inconsistency - Cross-signal contradictions
- VersionAge - Browser/OS freshness
- ReputationBias - Historical reputation
- **Heuristic** - ML-trained weighted model (with learning enabled)
- **TlsFingerprint** - JA3/JA4 TLS fingerprinting ⭐ NEW
- **TcpIpFingerprint** - p0f passive OS detection ⭐ NEW
- **Http2Fingerprint** - HTTP/2 SETTINGS analysis ⭐ NEW
- **MultiLayerCorrelation** - Cross-layer consistency ⭐ NEW
- **BehavioralWaveform** - Temporal pattern detection ⭐ NEW

### 2. Passes ALL Headers Downstream

Demo mode forwards **comprehensive detection headers** to your backend:

#### Core Detection Results
```
X-Bot-Detection-Result: true
X-Bot-Detection-Probability: 0.8523
X-Bot-Detection-Confidence: 0.9245
X-Bot-Detection-RiskBand: High
```

#### Bot Identification
```
X-Bot-Detection-BotType: Scraper
X-Bot-Detection-BotName: Headless Chrome
```

#### Policy & Action
```
X-Bot-Detection-Policy: demo
X-Bot-Detection-Action: Allow
X-Bot-Detection-ProcessingMs: 18.45
```

#### Detection Reasons (JSON Array)
```
X-Bot-Detection-Reasons: ["Headless browser detected in User-Agent","Chrome DevTools Protocol detected","Missing browser plugins","TLS fingerprint matches automation tool","TCP window size indicates non-standard stack"]
```

#### Detector Contributions (JSON Array)
```
X-Bot-Detection-Contributions: [
  {
    "Name": "UserAgent",
    "Category": "UserAgent",
    "ConfidenceDelta": 0.25,
    "Weight": 1.5,
    "Contribution": 0.375,
    "Reason": "Headless browser detected",
    "ExecutionTimeMs": 0.12,
    "Priority": 2
  },
  {
    "Name": "TlsFingerprint",
    "Category": "Fingerprint",
    "ConfidenceDelta": 0.15,
    "Weight": 1.2,
    "Contribution": 0.18,
    "Reason": "JA3 fingerprint matches Puppeteer",
    "ExecutionTimeMs": 0.08,
    "Priority": 17
  }
  // ... 19 more detectors
]
```

#### Signature ID (for lookup)
```
X-Signature-ID: sig-abc123def456
```

#### YARP Routing Info
```
X-Bot-Detection-Cluster: backend-cluster
X-Bot-Detection-Destination: http://backend-01:8080
```

#### Request Metadata
```
X-Bot-Detection-RequestId: 0HN6QJKJ2M3K4:00000001
```

---

## Using Demo Mode Headers in Your UI

### Example: ASP.NET Core TagHelper

```html
@{
    var botProbability = Context.Request.Headers["X-Bot-Detection-Probability"];
    var contributions = Context.Request.Headers["X-Bot-Detection-Contributions"];
}

<div class="bot-detection-card">
    <h3>Bot Detection Result</h3>
    <p>Bot Probability: <strong>@botProbability</strong></p>

    @if (!string.IsNullOrEmpty(contributions))
    {
        var detectors = System.Text.Json.JsonSerializer.Deserialize<List<DetectorContribution>>(contributions);
        <h4>Detector Contributions (@detectors.Count)</h4>
        <table>
            <thead>
                <tr>
                    <th>Detector</th>
                    <th>Category</th>
                    <th>Impact</th>
                    <th>Reason</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var detector in detectors.OrderByDescending(d => Math.Abs(d.Contribution)))
                {
                    <tr>
                        <td>@detector.Name</td>
                        <td>@detector.Category</td>
                        <td class="@(detector.Contribution > 0 ? "bot" : "human")">
                            @detector.Contribution.ToString("F3")
                        </td>
                        <td>@detector.Reason</td>
                    </tr>
                }
            </tbody>
        </table>
    }
</div>
```

### Example: JavaScript/Fetch

```javascript
fetch('/api/data')
    .then(response => {
        // Read bot detection headers
        const botProbability = parseFloat(response.headers.get('X-Bot-Detection-Probability'));
        const riskBand = response.headers.get('X-Bot-Detection-RiskBand');
        const contributions = JSON.parse(response.headers.get('X-Bot-Detection-Contributions'));

        console.log(`Bot Probability: ${botProbability * 100}%`);
        console.log(`Risk Band: ${riskBand}`);
        console.log(`Detectors ran: ${contributions.length}`);

        // Display top 5 contributing detectors
        const topDetectors = contributions
            .sort((a, b) => Math.abs(b.Contribution) - Math.abs(a.Contribution))
            .slice(0, 5);

        topDetectors.forEach(d => {
            console.log(`${d.Name}: ${d.Reason} (impact: ${d.Contribution.toFixed(3)})`);
        });

        return response.json();
    });
```

### Example: React Component

```jsx
function BotDetectionBadge({ headers }) {
  const botProbability = parseFloat(headers['x-bot-detection-probability']);
  const riskBand = headers['x-bot-detection-riskband'];
  const contributions = JSON.parse(headers['x-bot-detection-contributions'] || '[]');

  const getBadgeColor = () => {
    if (botProbability < 0.3) return 'green';
    if (botProbability < 0.7) return 'yellow';
    return 'red';
  };

  return (
    <div className={`badge badge-${getBadgeColor()}`}>
      <h4>Bot Probability: {(botProbability * 100).toFixed(1)}%</h4>
      <span className="risk-band">{riskBand}</span>

      <details>
        <summary>{contributions.length} detectors analyzed</summary>
        <ul>
          {contributions.map((c, i) => (
            <li key={i}>
              <strong>{c.Name}</strong>: {c.Reason}
              <span className="impact">{c.Contribution > 0 ? '+' : ''}{c.Contribution.toFixed(3)}</span>
            </li>
          ))}
        </ul>
      </details>
    </div>
  );
}
```

---

## Production vs Demo Mode

| Feature | Production | Demo Mode |
|---------|-----------|-----------|
| **Policy** | `default` (10 detectors) | `demo` (29 detectors) |
| **Headers Passed** | Basic only | ALL (comprehensive) |
| **Detector Contributions** | ❌ Not exposed | ✅ Full JSON array |
| **Detection Reasons** | ❌ Not exposed | ✅ Top 5 reasons |
| **Processing Time** | ❌ Not exposed | ✅ Exposed |
| **Signature ID** | ❌ Not exposed | ✅ Exposed |
| **Risk Band** | ❌ Not exposed | ✅ Exposed |
| **Heuristic Learning** | ✅ Enabled | ✅ Enabled |
| **Security** | ✅ Secure | ⚠️ Information leakage |

---

## Performance Impact

Demo mode runs **ALL 29 detectors** instead of the default 10:

| Metric | Production (default) | Demo Mode |
|--------|---------------------|-----------|
| **Detectors** | 10 | 29 |
| **Typical Latency** | 5-15ms | 18-25ms |
| **Worst-Case** | 20ms | 150ms (with ProjectHoneypot DNS) |
| **Memory/Request** | ~30KB | ~43KB |
| **Throughput** | 80,000+ req/s | 52,000+ req/s |

**Impact:** +3-10ms latency, +13KB memory per request

---

## Disabling Demo Mode

**Option 1: Environment Variable**
```bash
docker run -e GATEWAY_DEMO_MODE=false ...
```

**Option 2: Configuration**
```json
{
  "Gateway": {
    "DemoMode": {
      "Enabled": false
    }
  }
}
```

**Option 3: Restart Without Env Var**
```bash
# Remove GATEWAY_DEMO_MODE from environment
docker rm -f yarp-gateway
docker run -d ... (without -e GATEWAY_DEMO_MODE)
```

---

## Troubleshooting

### Headers Not Appearing Downstream

**Problem:** Backend doesn't see `X-Bot-Detection-*` headers

**Solution:**
1. Verify demo mode is enabled:
   ```bash
   docker logs yarp-gateway | grep "DEMO MODE ENABLED"
   ```

2. Check YARP is forwarding headers:
   ```bash
   curl -v http://localhost:8080/api/test
   # Look for X-Bot-Detection-* in response
   ```

3. Ensure backend is reading request headers (not response headers)

### Demo Policy Not Active

**Problem:** Only 10 detectors running instead of 29

**Solution:**
1. Check PathPolicies configuration:
   ```bash
   curl http://localhost:8080/admin/config
   ```

2. Verify demo policy exists in BotDetection:Policies

3. Restart gateway after config change

### Performance Issues

**Problem:** High latency in demo mode

**Solution:**
1. Disable ProjectHoneypot (100ms DNS lookup):
   ```json
   {
     "BotDetection": {
       "ProjectHoneypot": {
         "Enabled": false
       }
     }
   }
   ```

2. Use selective detectors (edit demo policy FastPath)

---

## Security Considerations

⚠️ **Demo mode exposes sensitive information:**

- Bot detection logic and signals
- Detector weights and thresholds
- Processing times (timing attacks)
- Policy configurations
- Signature IDs for lookup

**Attackers can use this to:**
- Understand how detection works
- Craft evasion techniques
- Bypass specific detectors

**Mitigation:**
- Only use on internal networks
- Require authentication on downstream UI
- Rate limit demo endpoints
- Monitor for abuse

---

## Best Practices

1. **Use Environment Variable** - Easy to toggle without rebuilding
2. **Secure Downstream** - Add authentication to UI endpoints
3. **Monitor Performance** - Demo mode adds latency
4. **Test Locally First** - Verify headers before deploying
5. **Document UI Integration** - Clear examples for your team

---

## See Also

- [QUICKSTART.md](../../QUICKSTART.md) - Full bot detection demo guide
- [TLS_FINGERPRINTING_SETUP.md](./TLS_FINGERPRINTING_SETUP.md) - TLS capture setup
- [appsettings.json](../appsettings.json) - Full configuration reference

---

**Built with Claude Code** 🤖
