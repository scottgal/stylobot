# BDF v2 → k6 Execution Mapping

## How k6 Replays BDF v2 Signatures

### 1. Timing Profile → k6 Sleep Logic

**BDF v2:**
```json
"timingProfile": {
  "burstRequests": 10,
  "delayAfterMs": { "min": 20, "max": 150 },
  "pauseAfterBurstMs": { "min": 500, "max": 2000 }
}
```

**k6 Implementation:**
```javascript
let requestCount = 0;
for (let req of sig.requests) {
    http.request(req.method, url, null, params);
    requestCount++;

    // Burst delay (jitter)
    if (requestCount < sig.timingProfile.burstRequests) {
        const jitter = randomBetween(
            sig.timingProfile.delayAfterMs.min / 1000,
            sig.timingProfile.delayAfterMs.max / 1000
        );
        sleep(jitter);
    } else {
        // Pause after burst
        const pause = randomBetween(
            sig.timingProfile.pauseAfterBurstMs.min / 1000,
            sig.timingProfile.pauseAfterBurstMs.max / 1000
        );
        sleep(pause);
        requestCount = 0; // Reset burst counter
    }
}
```

### 2. Client Profile → k6 Headers & Cookie Jar

**BDF v2:**
```json
"clientProfile": {
  "cookieMode": "none",
  "headerCompleteness": "minimal",
  "clientHintsPresent": false
}
```

**k6 Implementation:**
```javascript
// Cookie jar
const jar = cookieMode === 'sticky' ? http.cookieJar() : null;
if (cookieMode === 'none') {
    // Disable cookies entirely
    params.jar = null;
}

// Headers based on completeness
let headers = { ...req.headers };

if (headerCompleteness === 'minimal') {
    // Only User-Agent, no Accept-Language, no Sec-Fetch-*
    headers = {
        'User-Agent': req.headers['User-Agent']
    };
} else if (headerCompleteness === 'full') {
    // Full browser headers
    headers = {
        ...req.headers,
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
        'Accept-Language': 'en-US,en;q=0.9',
        'Accept-Encoding': 'gzip, deflate, br',
        'Sec-Fetch-Dest': 'document',
        'Sec-Fetch-Mode': 'navigate',
        'Sec-Fetch-Site': 'none'
    };

    if (clientHintsPresent) {
        headers['Sec-CH-UA'] = '"Chromium";v="120", "Not_A Brand";v="8"';
        headers['Sec-CH-UA-Mobile'] = '?0';
        headers['Sec-CH-UA-Platform'] = '"Windows"';
    }
}
```

### 3. Expected Status → Validation

**BDF v2:**
```json
"requests": [
  {
    "expectedStatusAny": [200, 301, 403, 404, 429],
    "expectedOutcome": "data_exfil",
    "successCondition": "any 2xx with payload > 10KB"
  }
]
```

**k6 Implementation:**
```javascript
check(res, {
    'status is acceptable': (r) => req.expectedStatusAny.includes(r.status),
    'outcome achieved': (r) => {
        if (req.expectedOutcome === 'data_exfil') {
            return r.status === 200 && r.body.length > 10240; // 10KB
        }
        if (req.expectedOutcome === 'probing') {
            return [200, 403, 404].includes(r.status);
        }
        return true;
    }
});
```

### 4. Evidence Signals → Custom Metrics

**BDF v2:**
```json
"evidence": [
  { "signal": "interval_ms_p95", "op": "<", "value": 200, "weight": 0.35 },
  { "signal": "sensitive_path_rate", "op": ">", "value": 0.4, "weight": 0.30 }
]
```

**k6 Implementation:**
```javascript
import { Trend, Rate } from 'k6/metrics';

const intervalTrend = new Trend('interval_ms');
const sensitivePaths = new Rate('sensitive_path_rate');

// Track intervals
let lastRequestTime = Date.now();
// ... during request
const now = Date.now();
intervalTrend.add(now - lastRequestTime);
lastRequestTime = now;

// Track sensitive paths
if (req.path.includes('/admin') || req.path.includes('/api')) {
    sensitivePaths.add(1);
} else {
    sensitivePaths.add(0);
}

// Validate evidence at end
export function handleSummary(data) {
    const interval_p95 = data.metrics.interval_ms.values['p(95)'];
    const sensitive_rate = data.metrics.sensitive_path_rate.values.rate;

    for (let evidence of sig.evidence) {
        if (evidence.signal === 'interval_ms_p95') {
            const passed = evidence.op === '<' ?
                interval_p95 < evidence.value :
                interval_p95 > evidence.value;
            console.log(`Evidence ${evidence.signal}: ${passed ? '✓' : '✗'} (weight: ${evidence.weight})`);
        }
    }
}
```

### 5. Robots.txt Consultation

**BDF v2:**
```json
"clientProfile": {
  "robotsConsulted": false
}
```

**k6 Implementation:**
```javascript
if (sig.clientProfile.robotsConsulted) {
    // Fetch robots.txt first (but may still violate it)
    http.get(`${TARGET_URL}/robots.txt`);
}

// Then proceed with requests regardless
```

### 6. Method Variety (HEAD/GET pairs)

**BDF v2:**
```json
"requests": [
  { "method": "HEAD", "path": "/api/data" },
  { "method": "GET", "path": "/api/data" }
]
```

**k6 Implementation:**
```javascript
// k6 supports HEAD natively
const res = http.request(req.method, url, null, params);
```

### 7. Range Requests

**BDF v2:**
```json
"requests": [
  {
    "method": "GET",
    "path": "/large-file.pdf",
    "headers": { "Range": "bytes=0-4095" }
  }
]
```

**k6 Implementation:**
```javascript
params.headers['Range'] = req.headers.Range;
```

## Complete Example

BDF v2 signature → k6 execution with all features:

```javascript
export default function() {
    const sig = pickRandomSignature();
    let requestCount = 0;
    let lastRequestTime = Date.now();

    // Setup cookie jar
    const jar = sig.clientProfile.cookieMode === 'sticky' ? http.cookieJar() : null;

    // Robots.txt consultation
    if (sig.clientProfile.robotsConsulted) {
        http.get(`${TARGET_URL}/robots.txt`);
    }

    // Execute burst
    for (let req of sig.requests) {
        const url = `${TARGET_URL}${req.path}`;

        // Build headers
        const headers = buildHeaders(req, sig.clientProfile);
        const params = { headers, jar };

        // Make request
        const res = http.request(req.method, url, null, params);

        // Track evidence
        intervalTrend.add(Date.now() - lastRequestTime);
        lastRequestTime = Date.now();

        if (req.path.includes('/admin') || req.path.includes('/api')) {
            sensitivePaths.add(1);
        } else {
            sensitivePaths.add(0);
        }

        // Validate
        check(res, {
            'status acceptable': (r) => req.expectedStatusAny.includes(r.status)
        });

        // Timing: burst vs pause
        requestCount++;
        if (requestCount < sig.timingProfile.burstRequests) {
            sleep(randomBetween(
                sig.timingProfile.delayAfterMs.min / 1000,
                sig.timingProfile.delayAfterMs.max / 1000
            ));
        } else {
            sleep(randomBetween(
                sig.timingProfile.pauseAfterBurstMs.min / 1000,
                sig.timingProfile.pauseAfterBurstMs.max / 1000
            ));
            requestCount = 0;
        }
    }
}
```

This mapping preserves all the realism from BDF v2 signatures!
