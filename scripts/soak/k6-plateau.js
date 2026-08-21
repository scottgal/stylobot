/**
 * StyloBot Plateau / Breaking Point Test
 *
 * Ramps traffic in steps, holds each plateau for 2 minutes,
 * measures latency/error rate at each level. Finds the breaking point.
 *
 * Usage:
 *   k6 run scripts/soak/k6-plateau.js --env TARGET=http://192.168.0.89:5080
 *   k6 run scripts/soak/k6-plateau.js --env TARGET=http://192.168.0.89:5080 --env MAX_RPS=500
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const TARGET = __ENV.TARGET || 'http://192.168.0.89:5080';
const MAX_RPS = parseInt(__ENV.MAX_RPS || '300');
// Debug/ops key sent as X-SB-Api-Key on EVERY request. Set it (to a key with
// DisableLearningWrites) so a soak's synthetic traffic does NOT train the model — un-keyed soak
// traffic is learned and poisons centroids/reputation. Leave empty ONLY when deliberately
// exercising the learning pipeline. See feedback_always_api_key_on_stylobot_traffic.
const API_KEY = __ENV.API_KEY || '';

// Custom metrics
const detectionLatency = new Trend('detection_latency_ms');
const botDetectionRate = new Rate('bot_detected');
const errorRate = new Rate('request_errors');
const plateauLevel = new Counter('plateau_level');

// 40 realistic browser UAs
const HUMAN_UAS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1',
  'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0',
];

const BOT_UAS = [
  'curl/8.4.0',
  'python-requests/2.31.0',
  'Go-http-client/1.1',
  'Scrapy/2.11.0',
  'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
  'Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)',
  'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)',
  'wget/1.21.4',
  'axios/1.6.0',
  'node-fetch/1.0',
];

const PATHS = ['/', '/products', '/products/123', '/api/data', '/about', '/contact', '/blog', '/blog/post-1'];
const ATTACK_PATHS = ['/wp-login.php', '/.env', '/phpmyadmin/', '/.git/config', "/products?id=1' OR '1'='1"];
const HONEYPOT_PATHS = ['/wp-admin/', '/xmlrpc.php', '/config.php', '/backup.sql'];

// ── Test-control admin endpoints (stream-, 25596a49) ─────────────────────
// Gated behind PostgreSQLStorageOptions.EnableTestControlEndpoints — routes
// are UNMAPPED (404, not 403) when the option is off, so these are inert
// no-ops against any non-rig target by construction. RIG-ONLY: the option
// must be set only in docker-compose.loadtest.yml, same structural-isolation
// requirement as TrustAllForwardedProxies/STYLOBOT_DASHBOARD_PUBLIC. Exported
// for reuse by other scenario scripts (e.g. the boundary-crossing scenario),
// not wired into the default traffic — compressing time/memory is a
// deliberate, isolated action, not something to fire on every request.
export function advanceClock(timeSpanString) {
  return http.post(`${TARGET}/admin/test-clock/advance`,
    JSON.stringify({ by: timeSpanString }),
    { headers: { 'Content-Type': 'application/json' }, tags: { traffic_type: 'admin_control' } });
}

export function setMemoryBudget(bytesOrNull) {
  return http.post(`${TARGET}/admin/test-clock/memory-budget`,
    JSON.stringify({ bytes: bytesOrNull }),
    { headers: { 'Content-Type': 'application/json' }, tags: { traffic_type: 'admin_control' } });
}

// ── Signature-cardinality growth ─────────────────────────────────────────
// primary_signature = HMAC(RemoteIpAddress|UserAgent) (MultiFactorSignatureService.cs).
// The default plateau traffic runs from ONE k6 host through a handful of fixed
// UAs, which caps cardinality at ~19 forever regardless of RPS or duration —
// this is why the soak has been green through cache-growth OOMs. Send
// X-Forwarded-For per request so ForwardedHeadersMiddleware rewrites
// RemoteIpAddress (Program.cs:135-190,352) and every request produces a
// distinct signature. RIG-ONLY — the gateway must run with
// Network:TrustAllForwardedProxies=true (or a KnownNetworks allowlist)
// scoped so it cannot reach staging/prod; this script does not set that
// config, it only relies on it.
// (VU, ITER) pairs are unique for the life of a run, so encoding both into
// the address gives near-total uniqueness without a fixed pool size.
function synthIp(vu, iter) {
  const b2 = vu % 256;
  const b3 = Math.floor(iter / 256) % 256;
  const b4 = iter % 256;
  return `10.${b2}.${b3}.${b4}`;
}

// Fixed control identities — dual purpose, NOT a cardinality cap:
//  1. Freshness baseline (existing) — this low-cardinality stream's
//     persist-to-visible latency is what the growing one-shot stream is
//     measured against.
//  2. THE "SIGNIFICANT FINGERPRINT" POPULATION (operator, 2026-08-21): the
//     memory-curve-shape gate needs BOTH a high-cardinality throwaway stream
//     AND a stable set of genuinely important, repeatedly-hit fingerprints
//     present SIMULTANEOUSLY — a corpus of only one kind cannot demonstrate
//     whether compression follows behavioural significance rather than
//     volume. A small, fixed, HEAVILY reused set of identities standing in
//     for real recurring visitors is exactly that population; growing traffic
//     alone (synthIp, one-shot per request) cannot produce it.
const CONTROL_IPS = ['10.0.0.1', '10.0.0.2', '10.0.0.3'];
// Fraction of the main plateau traffic that goes to the significant/control
// population instead of a fresh one-shot identity — an engineering/executor
// choice (how much of each population to mix), not a correctness threshold.
// Deliberately high enough that CONTROL_IPS gets sustained, repeated hits
// throughout the run, not just the freshness probe's occasional touch.
const SIGNIFICANT_TRAFFIC_FRACTION = parseFloat(__ENV.SIGNIFICANT_TRAFFIC_FRACTION || '0.2');

const freshnessMs = new Trend('freshness_ms');
const freshnessTimeout = new Rate('freshness_timeout');

// Total requested run length, in seconds. Passed via --env, NEVER via the k6
// CLI --duration flag: --duration alongside a script-defined options.scenarios
// is a real conflict (confirmed 2026-08-21 — a 3h run's freshness_probe
// scenario never executed even once, not even a zero-point registration, and
// --duration was the only thing touching scenario timing outside the script
// itself). Keeping ALL duration control inside the script, driven by one env
// var both scenarios read, removes that class of bug entirely rather than
// guessing at k6's exact CLI-vs-scenarios precedence rules.
const TOTAL_DURATION_S = parseFloat(__ENV.DURATION_HOURS || '0') * 3600;

// Build ramp stages: 10 → 20 → 50 → 100 → 150 → 200 → MAX_RPS, 2 min each,
// then hold at the final level for whatever's left of TOTAL_DURATION_S (this
// is what the removed CLI --duration flag used to do).
function buildStages() {
  const levels = [10, 20, 50, 100, 150, 200];
  if (MAX_RPS > 200) levels.push(MAX_RPS);

  const stages = [];
  for (const level of levels) {
    if (level > MAX_RPS) break;
    stages.push({ duration: '30s', target: level });  // ramp up
    stages.push({ duration: '90s', target: level });  // hold plateau
  }
  if (TOTAL_DURATION_S > 0) {
    const rampedS = stages.reduce((sum, s) => sum + parseInt(s.duration), 0);
    const remaining = TOTAL_DURATION_S - rampedS;
    if (remaining > 0) {
      stages.push({ duration: `${Math.round(remaining)}s`, target: stages.length ? stages[stages.length - 1].target : MAX_RPS });
    }
  }
  return stages;
}

// Freshness probe rate: an engineering/executor choice (how many correctness
// checks to run), not a correctness threshold — separate from the PASS/FAIL
// assertions themselves, which stay threshold-free (see freshnessProbe()).
const PROBE_ENABLED = (__ENV.FRESHNESS_PROBE || 'true') !== 'false';
const DASHBOARD_SESSION_COOKIE = __ENV.DASHBOARD_SESSION_COOKIE || '';

export const options = {
  scenarios: Object.assign({
    plateau: {
      executor: 'ramping-arrival-rate',
      startRate: 5,
      timeUnit: '1s',
      preAllocatedVUs: 200,
      maxVUs: 500,
      stages: buildStages(),
    },
  }, PROBE_ENABLED ? {
    freshness_probe: {
      executor: 'constant-arrival-rate',
      rate: 1,
      timeUnit: '1s',
      // Same TOTAL_DURATION_S as the plateau scenario (see above) — both
      // scenarios must run for the SAME wall-clock length or one silently
      // stops probing/loading before the other finishes.
      duration: (TOTAL_DURATION_S > 0 ? TOTAL_DURATION_S : buildStages().reduce((sum, s) => sum + parseInt(s.duration), 0)) + 's',
      // Worst case per iteration: db_only_detections never resolves (60s) +
      // conforming_dashboard never resolves (15s) = 75s held. At rate 1/s
      // that needs ~75-80 concurrent VUs in the all-timeout worst case.
      preAllocatedVUs: 20,
      maxVUs: 90,
      exec: 'freshnessProbe',
    },
  } : {}),
  thresholds: {
    http_req_duration: ['p(95)<2000'],  // Will likely be exceeded at breaking point
    request_errors: ['rate<0.20'],       // 20% error = definitely broken
    // Correctness, not liveness: a probe that never sees its own write is the
    // failure signal, independent of any chosen millisecond budget.
    freshness_timeout: ['rate<1.0'],
  },
};

export default function () {
  // Mix: 60% human, 25% bot, 10% attack, 5% honeypot
  const roll = Math.random();

  let url, headers, tag;

  if (roll < 0.60) {
    // Human browsing
    const ua = HUMAN_UAS[Math.floor(Math.random() * HUMAN_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    url = `${TARGET}${path}`;
    headers = {
      'User-Agent': ua,
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'same-origin',
    };
    tag = 'human';
  } else if (roll < 0.85) {
    // Bot traffic
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': ua, 'Accept': '*/*' };
    tag = 'bot';
  } else if (roll < 0.95) {
    // Attack traffic
    const path = ATTACK_PATHS[Math.floor(Math.random() * ATTACK_PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': 'python-requests/2.31.0', 'Accept': '*/*' };
    tag = 'attack';
  } else {
    // Honeypot probing
    const path = HONEYPOT_PATHS[Math.floor(Math.random() * HONEYPOT_PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': 'curl/8.4.0', 'Accept': '*/*' };
    tag = 'honeypot';
  }

  // Poison-guard: carry the debug key so the model does not learn from this synthetic flood.
  if (API_KEY) headers['X-SB-Api-Key'] = API_KEY;
  // Dual population (see CONTROL_IPS comment above): most traffic is
  // one-shot growing cardinality; a deliberate slice reuses a fixed,
  // significant identity so a compression-by-importance mechanism has both
  // a throwaway population to compress away and a stable one to keep.
  headers['X-Forwarded-For'] = Math.random() < SIGNIFICANT_TRAFFIC_FRACTION
    ? CONTROL_IPS[(__VU + __ITER) % CONTROL_IPS.length]
    : synthIp(__VU, __ITER);

  const res = http.get(url, { headers, tags: { traffic_type: tag } });

  // Track metrics
  const isError = res.status === 0 || res.status >= 500;
  errorRate.add(isError);

  const processingMs = res.headers['X-Bot-Detection-ProcessingMs'];
  if (processingMs) {
    detectionLatency.add(parseFloat(processingMs));
  }

  const isBot = res.headers['X-StyloBot-IsBot'] === 'true' ||
                res.status === 403;
  botDetectionRate.add(isBot);

  // Tiny sleep to prevent pure CPU spin
  sleep(0.01);
}

// ── Freshness probe: write once, poll every read surface until visible ──
// Two populations alternate each iteration:
//   'control' — a fixed, reused synthetic IP (small, repeated cardinality).
//   'oneshot' — a synthetic IP touched exactly ONCE, same as the growth
//               traffic. Per the canonical LFU pattern (a signature that will
//               never be revisited has its natural completion boundary
//               IMMEDIATELY after the touch — there is no session to wait
//               out), a working store should persist+surface a one-shot
//               entry just as fast as a control entry. If it does not, the
//               entry is not reaching completion/eviction at all.
// PASS/FAIL is never a chosen millisecond number: the assertion is whether
// the probe resolves before its own generous give-up point (a correctness
// gate — "did this ever become visible" — not a tuned budget). The DIFFERENCE
// between the two populations' resolution times is the reported measurement.
const GIVE_UP_MS = 60000; // generous ceiling so a slow-but-working store still
                            // resolves; a probe that times out is the failure
                            // signal, not the number itself.
// Shorter ceiling for the best-effort dashboard check ONLY — it is not
// authoritative (see freshnessProbe below), so it must not hold a VU for as
// long as the regression-gate surface; a probe that never resolves here is
// inconclusive, not proof of a gap, so there is no correctness reason to wait
// the full 60s. Keeps VU consumption bounded for the constant-arrival-rate
// executor even if this surface never resolves.
const DASHBOARD_GIVE_UP_MS = 15000;
const POLL_INTERVAL_MS = 500;

export function freshnessProbe() {
  const isOneShot = __ITER % 2 === 0;
  const population = isOneShot ? 'oneshot' : 'control';
  const ip = isOneShot ? synthIp(__VU, __ITER) : CONTROL_IPS[__ITER % CONTROL_IPS.length];
  const marker = `k6-probe-${__VU}-${__ITER}-${population}`;
  const ua = `Mozilla/5.0 (compatible; ${marker})`;
  const headers = { 'User-Agent': ua, 'X-Forwarded-For': ip };
  if (API_KEY) headers['X-SB-Api-Key'] = API_KEY;

  const sendTime = Date.now();
  http.get(`${TARGET}/`, { headers, tags: { traffic_type: 'probe', population } });

  // DB-only surface (the surface the mission names as broken — GetDetectionsAsync).
  // since= bypasses the top-N aggregate-cache snapshot (ReadEndpoints.cs:147),
  // so this exercises the real store read path, not the fast cache path.
  const sinceIso = new Date(sendTime - 5000).toISOString();
  pollUntilVisible(
    () => http.get(`${TARGET}/api/v1/detections?since=${sinceIso}&limit=200`, {
      headers: API_KEY ? { 'X-SB-Api-Key': API_KEY } : {},
      tags: { traffic_type: 'probe_read', surface: 'db_only_detections', population },
    }),
    (res) => res.status === 200 && res.body && res.body.includes(marker),
    sendTime, population, 'db_only_detections'
  );

  // Conforming surface (LFU∪DB read-through). Auth resolved 2026-08-20
  // (deploy-): STYLOBOT_DASHBOARD_PUBLIC=true on the rig only — plain GET,
  // no session needed; DASHBOARD_SESSION_COOKIE stays as an override for a
  // non-public-mode rig. BEST-EFFORT ONLY, not authoritative: the page's Top
  // Bots/Threats widget renders PrimarySignature/BotName, not raw UserAgent
  // text, and the client can't compute the HMAC signature to match against —
  // so a marker-in-body match can false-negative even when the entry is
  // genuinely present (it just didn't make the widget's top-N or doesn't
  // surface UA text at all). Treat a positive match as strong signal; treat a
  // miss as inconclusive, not a proven gap. The demo app's Playwright-driven,
  // rendered-content assertion is the authoritative check for this surface.
  const dashHeaders = DASHBOARD_SESSION_COOKIE ? { Cookie: DASHBOARD_SESSION_COOKIE } : {};
  pollUntilVisible(
    () => http.get(`${TARGET}/dashboard/traffic`, {
      headers: dashHeaders,
      tags: { traffic_type: 'probe_read', surface: 'conforming_dashboard', population },
    }),
    (res) => res.status === 200 && res.body && res.body.includes(marker),
    sendTime, population, 'conforming_dashboard', DASHBOARD_GIVE_UP_MS
  );

  sleep(0.1);
}

function pollUntilVisible(doRequest, isVisible, sendTime, population, surface, giveUpMs) {
  const ceiling = giveUpMs || GIVE_UP_MS;
  let elapsed = 0;
  while (elapsed < ceiling) {
    const res = doRequest();
    if (isVisible(res)) {
      freshnessMs.add(Date.now() - sendTime, { population, surface });
      freshnessTimeout.add(0, { population, surface });
      return;
    }
    sleep(POLL_INTERVAL_MS / 1000);
    elapsed = Date.now() - sendTime;
  }
  // Never became visible within the give-up ceiling — the failure signal.
  freshnessTimeout.add(1, { population, surface });
}

export function handleSummary(data) {
  // Output summary to both stdout and JSON file
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
  return {
    'stdout': textSummary(data),
    [`soak-results/plateau-${timestamp}.json`]: JSON.stringify(data, null, 2),
  };
}

function textSummary(data) {
  const metrics = data.metrics;
  const lines = [
    '\n═══════════════════════════════════════════════════════════',
    '  PLATEAU TEST RESULTS',
    '═══════════════════════════════════════════════════════════',
    `  Target: ${TARGET}`,
    `  Max RPS: ${MAX_RPS}`,
    `  Duration: ${(data.state?.testRunDurationMs / 1000 / 60).toFixed(1)} min`,
    '',
    `  Requests:    ${metrics.http_reqs?.values?.count || 0}`,
    `  Errors:      ${((metrics.request_errors?.values?.rate || 0) * 100).toFixed(1)}%`,
    `  p50 latency: ${(metrics.http_req_duration?.values?.['p(50)'] || 0).toFixed(0)}ms`,
    `  p95 latency: ${(metrics.http_req_duration?.values?.['p(95)'] || 0).toFixed(0)}ms`,
    `  p99 latency: ${(metrics.http_req_duration?.values?.['p(99)'] || 0).toFixed(0)}ms`,
    `  Max latency: ${(metrics.http_req_duration?.values?.max || 0).toFixed(0)}ms`,
    `  Bot detect:  ${((metrics.bot_detected?.values?.rate || 0) * 100).toFixed(1)}%`,
    '',
    `  Detection p50: ${(metrics.detection_latency_ms?.values?.['p(50)'] || 0).toFixed(0)}ms`,
    `  Detection p95: ${(metrics.detection_latency_ms?.values?.['p(95)'] || 0).toFixed(0)}ms`,
    '',
    `  Freshness (all populations combined) p50: ${(metrics.freshness_ms?.values?.['p(50)'] || 0).toFixed(0)}ms`,
    `  Freshness timeouts (never became visible): ${((metrics.freshness_timeout?.values?.rate || 0) * 100).toFixed(1)}%`,
    '  NOTE: population/surface breakdown (control vs oneshot, db_only vs conforming)',
    '  is NOT split here — k6 only aggregates the base metric in this summary.',
    '  The wrapper script computes the real differential from --out json raw tags.',
    '═══════════════════════════════════════════════════════════\n',
  ];
  return lines.join('\n');
}
