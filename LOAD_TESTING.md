# Load Testing with k6

This guide shows how to replay bot signatures through the gateway using k6.

## Architecture

```
k6 (bot/human traffic) → Gateway :5080 → Test Site :7240
```

## Setup

### 1. Build the Test Site

```powershell
cd TestSite
dotnet run
```

This starts a minimal ASP.NET site on `http://localhost:7240`.

### 2. Start the Gateway (in another terminal)

```powershell
cd Mostlylucid.BotDetection.Console
dotnet run --mode production --upstream http://localhost:7240 --port 5080
```

The gateway runs in production mode (only logs bot detections) and proxies to the test site.

### 3. Convert Signatures to k6 Script

```powershell
dotnet script convert-signatures-to-k6.csx -- signatures-2025-12-12.jsonl load-test.js
```

This reads your JSON-LD bot signatures and generates a k6 JavaScript test script.

**What it does:**
- Parses all signatures from the JSONL file
- Extracts request patterns (path, method, user agent, threat type)
- Groups into bot patterns (confidence >= 0.5) and human patterns (confidence < 0.5)
- Generates k6 script with realistic traffic mix (70% human, 30% bot)
- Adds appropriate user agents based on threat type

### 4. Run the Load Test

```powershell
# Basic run with default settings (from k6 script)
k6 run load-test.js

# Custom VUs and duration
k6 run --vus 20 --duration 60s load-test.js

# High load test
k6 run --vus 100 --duration 2m load-test.js

# Save results to JSON
k6 run --out json=results.json load-test.js
```

## Load Test Features

### Traffic Mix
- **70% human traffic** - Normal browsing patterns, slower pacing (1-3s between requests)
- **30% bot traffic** - Automated patterns, faster pacing (0-0.5s between requests)

### Metrics Tracked
- `bot_requests` - Counter of bot requests sent
- `human_requests` - Counter of human requests sent
- `detection_rate` - Rate of requests detected as bots
- `http_req_duration` - Standard k6 HTTP timing metrics
- `http_req_failed` - Failed request rate

### Thresholds
- **95% of requests < 500ms** - Performance target
- **Less than 10% requests fail** - Reliability target

### Detection Validation
The script logs:
- **False negatives**: Known bots that weren't detected
- **False positives**: Humans that were detected as bots

## Example Output

```
     ✓ status is 200 or 403
     ✓ response has bot detection header

     bot_requests..................: 312    5.2/s
     human_requests................: 728    12.1/s
     detection_rate................: 29.8%
     http_req_duration..............: avg=87ms   p(95)=245ms
     http_req_failed................: 0.00%
```

## Generated User Agents

The converter generates appropriate user agents based on threat type:

| Threat Type | Example User Agent |
|-------------|-------------------|
| Scraper (curl) | `curl/7.68.0` |
| Scraper (wget) | `Wget/1.20.3` |
| Scraper (Python) | `python-requests/2.28.0` |
| Scraper (Scrapy) | `Scrapy/2.8.0` |
| Scraper (Selenium) | `HeadlessChrome/120.0.0.0` |
| MaliciousBot | `BadBot/1.0` |
| Human | `Chrome/120.0.0.0 Safari/537.36` |

## Advanced Usage

### Custom Scenarios

Edit the generated `load-test.js` to add custom scenarios:

```javascript
export const options = {
    scenarios: {
        // Gradual ramp-up
        normal_load: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '30s', target: 10 },
                { duration: '1m', target: 10 },
                { duration: '10s', target: 0 },
            ],
        },
        // Spike test
        spike_test: {
            executor: 'constant-vus',
            vus: 100,
            duration: '10s',
            startTime: '1m', // Start after normal load
        },
    },
};
```

### Filter Signatures

Only test specific threat types:

```powershell
# Only test scrapers
dotnet script convert-signatures-to-k6.csx -- signatures-2025-12-12.jsonl scrapers-test.js

# Then edit scrapers-test.js to filter:
const botPatterns = allPatterns.filter(p => p.threatType.includes('Scraper'));
```

### Combine Multiple Signature Files

```powershell
# Merge multiple days
cat signatures-2025-12-10.jsonl signatures-2025-12-11.jsonl signatures-2025-12-12.jsonl > combined.jsonl

# Convert combined file
dotnet script convert-signatures-to-k6.csx -- combined.jsonl combined-test.js
```

## Analyzing Results

### View Real-time Metrics

While k6 is running, watch the console output for:
- Request rate (requests/second)
- Detection rate
- False positives/negatives
- Response times

### Save Results for Analysis

```powershell
# JSON output for detailed analysis
k6 run --out json=results.json load-test.js

# InfluxDB integration (if you have it)
k6 run --out influxdb=http://localhost:8086/k6 load-test.js
```

### Check Gateway Logs

The gateway logs bot detections in production mode:

```powershell
# Filter for blocked requests
Select-String "BLOCK" .\path\to\gateway\logs.txt

# Count detections by threat type
Select-String "ThreatType:" .\signatures-*.jsonl | Group-Object -NoElement
```

## Troubleshooting

### "unexpected EOF" Errors

This happens when the upstream (test site) can't keep up with load:

1. **Reduce VUs**: Start with `--vus 10`
2. **Check test site**: Make sure `dotnet run` in TestSite is still running
3. **Increase upstream capacity**: The test site is minimal - this is expected under very high load

### High False Positive Rate

If humans are being detected as bots:

1. **Check signature source**: Ensure signatures came from actual bot detections
2. **Adjust thresholds**: Edit the k6 script's `isBot` logic (currently `Math.random() < 0.3`)
3. **Review user agents**: Make sure human patterns have realistic user agents

### Low Detection Rate

If known bots aren't being detected:

1. **Check gateway policy**: Ensure bot detection is enabled
2. **Review bot patterns**: Make sure threat types match expected detections
3. **Increase confidence threshold**: Edit k6 script to only use high-confidence signatures

## Tips

1. **Start small**: Begin with `--vus 10 --duration 30s` and scale up
2. **Monitor resources**: Watch CPU/memory on both gateway and test site
3. **Use realistic patterns**: The more diverse your signatures, the better the test
4. **Test different policies**: Create multiple signature files for different scenarios
5. **Baseline first**: Run a pure human traffic test to establish baseline performance
