# Load Test Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a progressive k6 load test suite against StyloBot FOSS running locally on M5 Mac Air, from realistic log-mode baseline through DDoS flood, with LlamaSharp Metal (Gemma 4 1B) wired in for Phase 2.

**Architecture:** Three k6 scripts (log mode, LLM in path, DDoS flood) + LlamaSharp Metal backend fix + Demo app wiring. All scripts target `http://localhost:5080`. Phase 2 requires the Demo app to be rebuilt with `LLamaSharp.Backend.Metal` and LlamaSharp registered in DI.

**Tech Stack:** k6, .NET 10, LLamaSharp 0.26.0 (Metal backend), Gemma 4 1B IT Q4_K_M GGUF (HF auto-download)

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj` | Modify | Swap `Cpu` backend -> `Metal` |
| `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpProviderOptions.cs` | Modify | Add `GpuLayerCount` option |
| `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpLlmProvider.cs` | Modify | Pass `GpuLayerCount` to `ModelParams` |
| `Mostlylucid.BotDetection.Demo/Program.cs` | Modify | Wire `AddStylobotLlamaSharp()` + LLM policy |
| `Mostlylucid.BotDetection.Demo/appsettings.Development.json` | Modify | Add `LlamaSharp` config block |
| `scripts/k6/k6-local-logmode.js` | Create | Phase 1: realistic traffic, logonly |
| `scripts/k6/k6-local-llm.js` | Create | Phase 2: LLM in path, latency profiling |
| `scripts/k6/k6-local-ddos.js` | Create | Phase 3: flood mode, no think time |

---

## Task 1: Swap LlamaSharp to Metal Backend

**Files:**
- Modify: `Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj`
- Modify: `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpProviderOptions.cs`
- Modify: `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpLlmProvider.cs`

- [ ] **Step 1: Swap backend package in csproj**

Open `Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj`.

Keep `LLamaSharp.Backend.Cpu Version="0.26.0"` — **do NOT change this package**. In LLamaSharp 0.26.x, Metal support for Apple Silicon is bundled inside the Cpu backend via native RID-specific libs. There is no separate `LLamaSharp.Backend.Metal` or `LLamaSharp.Backend.MacMetal` package at this version. `GpuLayerCount = -1` in `ModelParams` is sufficient to enable full Metal offload at runtime.

- [ ] **Step 2: Add GpuLayerCount to options**

Open `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpProviderOptions.cs`.

Add one property after `ThreadCount`:
```csharp
/// <summary>Number of model layers to offload to GPU. -1 = all layers. 0 = CPU only. Default: -1 (full Metal offload)</summary>
public int GpuLayerCount { get; set; } = -1;
```

- [ ] **Step 3: Pass GpuLayerCount to ModelParams**

Open `Mostlylucid.BotDetection.Llm.LlamaSharp/LlamaSharpLlmProvider.cs`.

Find the block where `ModelParams` is built (around line 57-63). Replace:
```csharp
var @params = new ModelParams(modelPath)
{
    ContextSize = (uint)_options.ContextSize
};

if (_options.ThreadCount > 0)
    @params.Threads = _options.ThreadCount;
```
With:
```csharp
var @params = new ModelParams(modelPath)
{
    ContextSize = (uint)_options.ContextSize,
    GpuLayerCount = _options.GpuLayerCount
};

if (_options.ThreadCount > 0)
    @params.Threads = _options.ThreadCount;
```

Also update the log line (around line 69) to include GPU info:
```csharp
_logger.LogInformation("LlamaSharp model initialized. CPU cores: {Threads}, GPU layers: {GpuLayers}, IsReady: {IsReady}",
    _options.ThreadCount, _options.GpuLayerCount, IsReady);
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj
```
Expected: no errors. Metal backend resolves on macOS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.LlamaSharp/
git commit -m "feat(llamasharp): Metal backend + GpuLayerCount for Apple Silicon"
```

---

## Task 2: Wire LlamaSharp into Demo App

**Files:**
- Modify: `Mostlylucid.BotDetection.Demo/Program.cs`
- Modify: `Mostlylucid.BotDetection.Demo/appsettings.Development.json`
- Modify: `Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj`

- [ ] **Step 1: Add project reference to Demo csproj**

Open `Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj`.

Add inside the existing `<ItemGroup>` that has project references:
```xml
<ProjectReference Include="..\Mostlylucid.BotDetection.Llm.LlamaSharp\Mostlylucid.BotDetection.Llm.LlamaSharp.csproj" />
```

- [ ] **Step 2: Add LlamaSharp config block to appsettings.Development.json**

Open `Mostlylucid.BotDetection.Demo/appsettings.Development.json`. Add inside the `BotDetection` -> `AiDetection` section (alongside the existing `Heuristic` and `Ollama` blocks):

```json
"LlamaSharp": {
  "ModelPath": "bartowski/gemma-4-1b-it-GGUF/gemma-4-1b-it-Q4_K_M.gguf",
  "ModelCacheDir": "~/.cache/stylobot-models",
  "ContextSize": 512,
  "GpuLayerCount": -1,
  "ThreadCount": 8,
  "Temperature": 0.1,
  "MaxTokens": 150,
  "TimeoutMs": 5000
}
```

Note: `GpuLayerCount: -1` offloads all layers to M5 Metal. `TimeoutMs: 5000` is tight enough to not stall the load test if inference hangs.

- [ ] **Step 3: Register LlamaSharp in Program.cs**

Open `Mostlylucid.BotDetection.Demo/Program.cs`. Find where `AddLLMockApi` is called (around line 83). Add before that line:

```csharp
// LlamaSharp in-process LLM (Metal GPU on Apple Silicon)
builder.Services.AddStylobotLlamaSharp();
```

Add the using at the top of the file if not present:
```csharp
using Mostlylucid.BotDetection.Llm.LlamaSharp.Extensions;
```

- [ ] **Step 4: Build Demo app**

```bash
dotnet build Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj
```
Expected: no errors.

- [ ] **Step 5: Smoke test LlamaSharp loads**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```
Watch startup logs. Expected to see within ~30s (first run downloads model ~600MB):
```
info: Mostlylucid.BotDetection.Llm.LlamaSharp.LlamaSharpLlmProvider[0]
      LlamaSharp model initialized. CPU cores: 8, GPU layers: -1, IsReady: True
```

If it shows `Downloading model from Hugging Face: bartowski/gemma-4-1b-it-GGUF/...` - wait for the download to complete. Subsequent runs use cache.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection.Demo/
git commit -m "feat(demo): wire LlamaSharp Metal provider (Gemma 4 1B)"
```

---

## Task 3: k6 Phase 1 - Log Mode Realistic Traffic

**Files:**
- Create: `scripts/k6/k6-local-logmode.js`

- [ ] **Step 1: Create the script**

Create `scripts/k6/k6-local-logmode.js` with this exact content:

```javascript
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const botDetected = new Rate('bot_detected');
const humanPassRate = new Rate('human_pass_rate');
const detectionLatency = new Trend('detection_latency_ms');
const blockedRequests = new Counter('blocked_requests');

const BASE_URL = 'http://localhost:5080';
// Uses logonly policy - nothing gets blocked, all traffic flows through
const LOGONLY_KEY = 'SB-K6-FULL-DETECTION';

export const options = {
  stages: [
    { duration: '30s', target: 10 },
    { duration: '1m',  target: 50 },
    { duration: '1m',  target: 100 },
    { duration: '2m',  target: 100 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration:   ['p(95)<500', 'p(99)<1000'],
    http_req_failed:     ['rate<0.01'],
    detection_latency_ms: ['p(95)<20'],
  },
};

// Weighted traffic profiles
const profiles = [
  {
    name: 'chrome_human', weight: 40,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Dest': 'document',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'none',
      'Sec-Fetch-User': '?1',
      'Upgrade-Insecure-Requests': '1',
    },
    paths: ['/', '/about', '/features', '/docs', '/pricing'],
    thinkTime: () => Math.random() * 2 + 0.5,
    isBot: false,
  },
  {
    name: 'curl_bot', weight: 15,
    headers: {
      'User-Agent': 'curl/8.7.1',
      'Accept': '*/*',
    },
    paths: ['/', '/api/health', '/.env', '/wp-admin', '/config.php'],
    thinkTime: () => Math.random() * 0.2,
    isBot: true,
  },
  {
    name: 'python_scraper', weight: 15,
    headers: {
      'User-Agent': 'python-requests/2.31.0',
      'Accept-Encoding': 'gzip, deflate',
      'Accept': '*/*',
      'Connection': 'keep-alive',
    },
    paths: ['/', '/api/data', '/sitemap.xml', '/robots.txt', '/api/v1/items'],
    thinkTime: () => Math.random() * 0.3,
    isBot: true,
  },
  {
    name: 'googlebot', weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
      'Accept': 'text/html',
      'Accept-Encoding': 'gzip, deflate, br',
    },
    paths: ['/', '/about', '/docs', '/sitemap.xml'],
    thinkTime: () => Math.random() * 1 + 0.5,
    isBot: true,
  },
  {
    name: 'headless_chrome', weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml',
      'Accept-Encoding': 'gzip, deflate, br',
    },
    paths: ['/', '/login', '/api/data', '/checkout'],
    thinkTime: () => Math.random() * 0.1,
    isBot: true,
  },
  {
    name: 'sqlmap', weight: 5,
    headers: {
      'User-Agent': 'sqlmap/1.7 (http://sqlmap.org)',
      'Accept': '*/*',
    },
    paths: ['/api/search?q=1%27+OR+%271%27%3D%271', '/login?id=1+AND+1=1'],
    thinkTime: () => 0,
    isBot: true,
  },
  {
    name: 'nikto', weight: 5,
    headers: {
      'User-Agent': 'Mozilla/5.00 (Nikto/2.1.6) (Evasions:None) (Test:Port Check)',
    },
    paths: ['/.git/config', '/backup.sql', '/phpmyadmin', '/wp-login.php'],
    thinkTime: () => 0,
    isBot: true,
  },
];

function pickProfile() {
  const total = profiles.reduce((sum, p) => sum + p.weight, 0);
  let r = Math.random() * total;
  for (const p of profiles) {
    r -= p.weight;
    if (r <= 0) return p;
  }
  return profiles[0];
}

export default function () {
  const profile = pickProfile();
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: {
      ...profile.headers,
      'X-SB-Api-Key': LOGONLY_KEY,
    },
    tags: { profile: profile.name },
  });

  const isBot = res.headers['X-Bot-IsBot'] === 'true';
  const confidence = parseFloat(res.headers['X-Bot-Confidence'] || '0');
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');

  botDetected.add(isBot ? 1 : 0);
  humanPassRate.add(!profile.isBot && !isBot ? 1 : 0);
  detectionLatency.add(processingMs);
  if (res.status === 403) blockedRequests.add(1);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'has detection header': (r) => r.headers['X-Bot-IsBot'] !== undefined,
    'detection under 50ms': () => processingMs < 50,
  });

  sleep(profile.thinkTime());
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(1) ?? 'N/A';
  const p99 = data.metrics.http_req_duration?.values?.['p(99)']?.toFixed(1) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(1) ?? 'N/A';
  const botRate = (data.metrics.bot_detected?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP95 = data.metrics.detection_latency_ms?.values?.['p(95)']?.toFixed(2) ?? 'N/A';

  console.log('\n=== StyloBot Local Log-Mode Load Test ===');
  console.log(`Throughput:        ${rps} req/s`);
  console.log(`Response p95/p99:  ${p95}ms / ${p99}ms`);
  console.log(`Detection p95:     ${detP95}ms`);
  console.log(`Bot detection rate: ${botRate}%`);
  console.log('=========================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
```

- [ ] **Step 2: Start Demo app in fastpath policy mode**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo -- --urls http://localhost:5080
```

Wait for startup. Dashboard available at `http://localhost:5080/_stylobot`.

- [ ] **Step 3: Run Phase 1**

In a separate terminal:
```bash
k6 run scripts/k6/k6-local-logmode.js
```

Expected output at end:
```
=== StyloBot Local Log-Mode Load Test ===
Throughput:        XX req/s
Response p95/p99:  XXms / XXms
Detection p95:     X.XXms
Bot detection rate: XX%
```

Success: `detection_latency_ms p(95) < 20ms`, no 5xx errors. Record the RPS number.

- [ ] **Step 4: Commit**

```bash
git add scripts/k6/k6-local-logmode.js
git commit -m "feat(loadtest): Phase 1 log-mode k6 script with realistic traffic mix"
```

---

## Task 4: k6 Phase 2 - LLM in the Path

**Files:**
- Create: `scripts/k6/k6-local-llm.js`

- [ ] **Step 1: Create the script**

Create `scripts/k6/k6-local-llm.js`:

```javascript
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const botDetected = new Rate('bot_detected');
const detectionLatency = new Trend('detection_latency_ms');
const llmEscalated = new Rate('llm_escalated');

const BASE_URL = 'http://localhost:5080';
// No bypass key - LLM escalation runs for real; policy = "demo" (full sync)
// Set path policy to "demo" in appsettings for this test

export const options = {
  stages: [
    { duration: '30s', target: 5 },
    { duration: '2m',  target: 20 },
    { duration: '1m',  target: 20 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration:    ['p(95)<3000'],  // LLM adds latency - 3s p95 is acceptable
    http_req_failed:      ['rate<0.01'],
    detection_latency_ms: ['p(95)<2500'],
  },
};

// Borderline profiles most likely to trigger LLM escalation
// (obvious bots + obvious humans are handled by fast path; edge cases hit LLM)
const profiles = [
  {
    name: 'chrome_human', weight: 30,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Dest': 'document',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'none',
    },
    paths: ['/', '/about', '/docs'],
    thinkTime: () => Math.random() * 1 + 0.5,
  },
  {
    name: 'suspicious_ua', weight: 40,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36',
      'Accept': '*/*',
      'Accept-Language': 'en-US',
    },
    paths: ['/', '/api/data', '/products', '/search'],
    thinkTime: () => Math.random() * 0.3,
  },
  {
    name: 'headless_chrome', weight: 30,
    headers: {
      'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html',
      'Accept-Encoding': 'gzip, deflate',
    },
    paths: ['/', '/login', '/api/data'],
    thinkTime: () => Math.random() * 0.2,
  },
];

function pickProfile() {
  const total = profiles.reduce((sum, p) => sum + p.weight, 0);
  let r = Math.random() * total;
  for (const p of profiles) {
    r -= p.weight;
    if (r <= 0) return p;
  }
  return profiles[0];
}

export default function () {
  const profile = pickProfile();
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: profile.headers,
    tags: { profile: profile.name },
    timeout: '10s',
  });

  const isBot = res.headers['X-Bot-IsBot'] === 'true';
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
  const detectors = res.headers['X-Bot-Detectors'] || '';
  const usedLlm = detectors.includes('Llm') || detectors.includes('LlamaSharp');

  botDetected.add(isBot ? 1 : 0);
  detectionLatency.add(processingMs);
  llmEscalated.add(usedLlm ? 1 : 0);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'has detection header': (r) => r.headers['X-Bot-IsBot'] !== undefined,
  });

  sleep(profile.thinkTime());
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(1) ?? 'N/A';
  const llmRate = (data.metrics.llm_escalated?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP95 = data.metrics.detection_latency_ms?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const botRate = (data.metrics.bot_detected?.values?.rate * 100)?.toFixed(1) ?? 'N/A';

  console.log('\n=== StyloBot LLM-In-Path Load Test ===');
  console.log(`Throughput:       ${rps} req/s`);
  console.log(`Response p95:     ${p95}ms`);
  console.log(`Detection p95:    ${detP95}ms`);
  console.log(`LLM escalation:   ${llmRate}%`);
  console.log(`Bot detection:    ${botRate}%`);
  console.log('=======================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
```

- [ ] **Step 2: Run Phase 2**

Start Demo app (needs LlamaSharp wired from Task 2). The `demo` policy runs full LLM sync. Either set `BotDetection:Policies:demo` as the default in appsettings, or hit `/bot-test` which uses `full-demo` policy.

```bash
k6 run scripts/k6/k6-local-llm.js
```

Watch the `llm_escalated` metric - tells you what % of requests actually hit LlamaSharp. The `detection_latency_ms p95` vs Phase 1 shows the LLM overhead in absolute terms.

- [ ] **Step 3: Commit**

```bash
git add scripts/k6/k6-local-llm.js
git commit -m "feat(loadtest): Phase 2 LLM-in-path k6 script (Gemma 4 Metal)"
```

---

## Task 5: k6 Phase 3 - DDoS Flood

**Files:**
- Create: `scripts/k6/k6-local-ddos.js`

- [ ] **Step 1: Create the script**

Create `scripts/k6/k6-local-ddos.js`:

```javascript
import http from 'k6/http';
import { check } from 'k6';
import { Rate, Counter, Trend } from 'k6/metrics';

const blocked = new Rate('blocked_rate');
const detected = new Rate('detected_rate');
const detectionLatency = new Trend('detection_latency_ms');
const errors = new Counter('error_count');

const BASE_URL = 'http://localhost:5080';
// No API key - bots should get blocked or holodeck'd under realfast policy

export const options = {
  stages: [
    { duration: '30s', target: 50 },
    { duration: '30s', target: 200 },
    { duration: '30s', target: 500 },
    { duration: '1m',  target: 500 },
    { duration: '30s', target: 1000 },
    { duration: '1m',  target: 1000 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_failed:      ['rate<0.05'],   // <5% errors at peak (503s acceptable, 5xxs not)
    detection_latency_ms: ['p(99)<100'],   // Even at 1000 VUs, detection stays <100ms p99
  },
};

// Pure bot flood - no think time, no sleep
const attackProfiles = [
  {
    headers: { 'User-Agent': 'curl/8.7.1', 'Accept': '*/*' },
    paths: ['/', '/.env', '/wp-login.php', '/admin'],
  },
  {
    headers: { 'User-Agent': 'sqlmap/1.7 (http://sqlmap.org)', 'Accept': '*/*' },
    paths: ['/api/search?q=1+OR+1=1', '/login?id=1--'],
  },
  {
    headers: { 'User-Agent': 'python-requests/2.31.0', 'Accept': '*/*' },
    paths: ['/', '/api/data', '/sitemap.xml'],
  },
  {
    headers: { 'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 HeadlessChrome/125.0.0.0 Safari/537.36' },
    paths: ['/', '/login', '/checkout'],
  },
  {
    headers: { 'User-Agent': 'Mozilla/5.00 (Nikto/2.1.6) (Evasions:None)' },
    paths: ['/.git/config', '/backup.sql', '/phpmyadmin'],
  },
];

export default function () {
  const profile = attackProfiles[Math.floor(Math.random() * attackProfiles.length)];
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: profile.headers,
    timeout: '5s',
  });

  const isBot = res.headers['X-Bot-IsBot'] === 'true';
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');

  detected.add(isBot ? 1 : 0);
  blocked.add(res.status === 403 ? 1 : 0);
  if (processingMs > 0) detectionLatency.add(processingMs);
  if (res.status >= 500) errors.add(1);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'blocked or ok': (r) => r.status === 200 || r.status === 403 || r.status === 429,
  });

  // No sleep - pure flood
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const p99 = data.metrics.http_req_duration?.values?.['p(99)']?.toFixed(0) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(0) ?? 'N/A';
  const blockedRate = (data.metrics.blocked_rate?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detectedRate = (data.metrics.detected_rate?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP99 = data.metrics.detection_latency_ms?.values?.['p(99)']?.toFixed(1) ?? 'N/A';
  const errCount = data.metrics.error_count?.values?.count ?? 0;

  console.log('\n=== StyloBot DDoS Flood Test ===');
  console.log(`Peak RPS:         ${rps} req/s`);
  console.log(`Response p95/p99: ${p95}ms / ${p99}ms`);
  console.log(`Detection p99:    ${detP99}ms`);
  console.log(`Bot detected:     ${detectedRate}%`);
  console.log(`Requests blocked: ${blockedRate}%`);
  console.log(`5xx errors:       ${errCount}`);
  console.log('================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
```

- [ ] **Step 2: Switch Demo app to realfast policy**

For DDoS phase, the Demo app should use the `realfast` policy (fast-path only, blocks at 0.70). The demo app routes all paths to `full-demo` by default via `PathPolicies`. 

Temporarily override `BotDetection:DefaultActionPolicyName` and set the default policy to `realfast` - or pass an env var override:

```bash
BotDetection__PathPolicies__0__Policy=realfast dotnet run --project Mostlylucid.BotDetection.Demo -- --urls http://localhost:5080
```

Alternatively, edit `appsettings.Development.json` to change `"PathPolicies"` to use `realfast` for `"/*"` and revert after the test.

- [ ] **Step 3: Run Phase 3**

```bash
k6 run scripts/k6/k6-local-ddos.js
```

Watch for the saturation point: where does p99 latency climb? Where do 503s start? The detection latency should remain low even as ASP.NET queue depth rises.

Record: peak sustained RPS before p99 > 100ms, and peak RPS before 5xx errors appear.

- [ ] **Step 4: Commit**

```bash
git add scripts/k6/k6-local-ddos.js
git commit -m "feat(loadtest): Phase 3 DDoS flood k6 script (no think time, 1000 VU ceiling)"
```

---

## Self-Review

**Spec coverage check:**
- Phase 1 (log mode realistic traffic): Task 3 - covered
- Phase 2 (LlamaSharp in path): Tasks 1 + 2 + 4 - covered
- Phase 3 (DDoS edge): Task 5 - covered
- Phase 4 (multi-machine / Pi): not implemented here - spec marks it as future; no task needed now
- Metal backend: Task 1 - covered
- Gemma 4 1B model: Task 2 appsettings config - covered
- `GpuLayerCount` option: Task 1 - covered
- Demo app wiring: Task 2 - covered

**Placeholder scan:** None found. All steps have complete code.

**Type consistency:**
- `LlamaSharpProviderOptions.GpuLayerCount` defined Task 1 Step 2, used Task 1 Step 3 - consistent
- `LOGONLY_KEY = 'SB-K6-FULL-DETECTION'` matches the key in Demo `appsettings.json` - consistent
- `X-Bot-IsBot`, `X-Bot-Confidence`, `X-Bot-Processing-Ms` headers match `ResponseHeaders` config in Demo `appsettings.json` prefix `X-Bot-` - consistent
