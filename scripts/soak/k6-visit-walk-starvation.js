/**
 * Visit-walk rotating-cursor scenario (absorb-, 2026-08-21).
 *
 * Tests the SPECIFIC property the visit-walk cursor fix (stylobot-commercial main
 * 1fe3a9b0) guarantees: every cold (one-shot) signature present during sustained
 * hot-signature saturation is PERSISTED within a bounded number of sweep ticks --
 * not "eventually", a specific tick count derived from the fix's own arithmetic
 * (ticksPerHotWindow + 2).
 *
 * This is NOT soak-'s memory-curve-shape gate (sawtooth/flat/climb) and does not
 * replace it -- that gate asserts aggregate memory behaviour; this scenario asserts
 * PER-ENTRY DRAIN COMPLETION under a specific saturating condition (hot count above
 * SweepVisitBatchSize). See, in stylobot-commercial:
 *   .styloagent/scratch/absorb-visit-walk-cursor-fix-design-2026-08-21.md
 *   .styloagent/scratch/absorb-visit-walk-k6-scenario-design-2026-08-21.md
 * for the full design, the population-ceiling arithmetic, and the RED-FIRST
 * requirement.
 *
 * ABORT CONDITION (mandatory, overview- 2026-08-21): watch the gateway log for
 * "Session persistence channel FULL" while this runs (soak-'s crash-precursor
 * warning, 2026-08-21 SIGSEGV finding on a 3h real-time baseline run). If it
 * appears, STOP THE RUN and report it -- this scenario is void for its own purpose
 * and has independently reproduced the OTHER (soak-'s) defect under a different
 * traffic shape (compressed-clock hot-pool saturation vs. real-time plateau).
 *
 * RIG-ONLY, same isolation requirement as k6-plateau.js's admin-control functions:
 * requires PostgreSQLStorageOptions.EnableTestControlEndpoints on the target, which
 * must only ever be true on the isolated loadtest rig, never staging/prod.
 *
 * Usage:
 *   k6 run scripts/soak/k6-visit-walk-starvation.js --env TARGET=http://127.0.0.1:8290 --env API_KEY=<debug-key>
 *
 * Tunables via env:
 *   HOT_SIGNATURE_COUNT   (default 600 -- must exceed SweepVisitBatchSize, default 500)
 *   COLD_PROBE_COUNT      (default 50  -- one-shot signatures seeded at T0, checked for drain)
 *   WARMUP_SECONDS        (default 120 -- real-time traffic before T0, so the hot pool's
 *                          epochs are genuinely resident before probes are seeded)
 *   SWEEP_CADENCE_MINUTES (default 5   -- must match PostgreSQLStorageOptions.SweepCadence
 *                          on the target; Tick5m = 5)
 *   COMPRESSION_HOT_WINDOW_HOURS (default 2 -- must match CompressionHotWindow on the target)
 *   TICK_ADVANCE_MARGIN   (default 2   -- extra ticks past ticksPerHotWindow before giving up)
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const TARGET  = __ENV.TARGET || 'http://127.0.0.1:8290';
const API_KEY = __ENV.API_KEY || '';
if (!API_KEY) {
  throw new Error('API_KEY is required -- keyless traffic poisons the corpus. Pass --env API_KEY=<gateway-debug-api-key>.');
}

const HOT_SIGNATURE_COUNT       = parseInt(__ENV.HOT_SIGNATURE_COUNT || '600');
const COLD_PROBE_COUNT          = parseInt(__ENV.COLD_PROBE_COUNT || '50');
const WARMUP_SECONDS            = parseInt(__ENV.WARMUP_SECONDS || '120');
const SWEEP_CADENCE_MINUTES     = parseInt(__ENV.SWEEP_CADENCE_MINUTES || '5');
const COMPRESSION_HOT_WINDOW_HOURS = parseInt(__ENV.COMPRESSION_HOT_WINDOW_HOURS || '2');
const TICK_ADVANCE_MARGIN       = parseInt(__ENV.TICK_ADVANCE_MARGIN || '2');

const TICKS_PER_HOT_WINDOW = Math.ceil((COMPRESSION_HOT_WINDOW_HOURS * 60) / SWEEP_CADENCE_MINUTES);
const TOTAL_TICKS_TO_ADVANCE = TICKS_PER_HOT_WINDOW + TICK_ADVANCE_MARGIN;

// Fixed, deterministic hot pool -- distinct address space from k6-plateau.js's
// CONTROL_IPS (10.0.0.x) and cardinality growth (10.<vu>.x.x) so a run of this
// scenario never collides with a concurrent plateau run's signatures.
const HOT_IPS = Array.from({ length: HOT_SIGNATURE_COUNT }, (_, i) =>
  `10.98.${Math.floor(i / 256) % 256}.${i % 256}`);

const HUMAN_UAS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
];
const PATHS = ['/', '/products', '/api/data', '/about'];

const hotHits = new Counter('hot_pool_hits');
const coldProbeSeeded = new Counter('cold_probe_seeded');
const coldProbeDrained = new Rate('cold_probe_drained');

function synthColdIp(seed) {
  const b2 = (seed >> 16) % 256;
  const b3 = (seed >> 8) % 256;
  const b4 = seed % 256;
  return `10.97.${b3}.${b4}`; // distinct octet range from HOT_IPS (10.98.x.x)
}

// ---- Admin test-control endpoint (mirrors k6-plateau.js's, same rig-only gate) ----
function advanceClock(timeSpanString) {
  return http.post(`${TARGET}/admin/test-clock/advance`,
    JSON.stringify({ by: timeSpanString }),
    { headers: { 'Content-Type': 'application/json' }, tags: { traffic_type: 'admin_control' } });
}

// ---- Stage 1: warm-up. Real-time traffic against the hot pool so epochs are resident. ----
export function warmup() {
  const ip = HOT_IPS[__ITER % HOT_IPS.length];
  const ua = HUMAN_UAS[__ITER % HUMAN_UAS.length];
  const path = PATHS[__ITER % PATHS.length];
  const res = http.get(`${TARGET}${path}`, {
    headers: { 'User-Agent': ua, 'X-Forwarded-For': ip, 'X-SB-Api-Key': API_KEY },
    tags: { traffic_type: 'hot_warmup' },
  });
  hotHits.add(1);
  check(res, { 'warmup not 5xx': (r) => r.status < 500 });
}

// ---- Stage 2: seed T0 cold probes (one-shot, tagged, never touched again) ----
function seedColdProbes() {
  const markers = [];
  for (let i = 0; i < COLD_PROBE_COUNT; i++) {
    const marker = `k6-vwstarve-${__VU}-${Date.now()}-${i}`;
    const ip = synthColdIp(i * 7919 + __VU);
    const ua = `Mozilla/5.0 (compatible; ${marker})`;
    http.get(`${TARGET}/`, {
      headers: { 'User-Agent': ua, 'X-Forwarded-For': ip, 'X-SB-Api-Key': API_KEY },
      tags: { traffic_type: 'cold_probe_seed' },
    });
    markers.push(marker);
    coldProbeSeeded.add(1);
  }
  return markers;
}

// ---- Stage 3: keep the hot pool saturating the visit budget WHILE the clock advances ----
// The sweep tick fires against advanceClock's simulated boundaries; hot-pool traffic
// during this window keeps re-aging the hot signatures' epochs so they keep
// re-qualifying for the visit budget every tick, exactly the starvation precondition.
function keepHotPoolSaturated(rounds) {
  for (let r = 0; r < rounds; r++) {
    for (let i = 0; i < Math.min(50, HOT_IPS.length); i++) {
      const ip = HOT_IPS[(r * 50 + i) % HOT_IPS.length];
      http.get(`${TARGET}/`, {
        headers: { 'User-Agent': HUMAN_UAS[0], 'X-Forwarded-For': ip, 'X-SB-Api-Key': API_KEY },
        tags: { traffic_type: 'hot_saturate' },
      });
      hotHits.add(1);
    }
  }
}

// ---- Stage 4: advance the clock through ticksPerHotWindow + margin ticks ----
function advanceThroughTicks() {
  for (let t = 0; t < TOTAL_TICKS_TO_ADVANCE; t++) {
    advanceClock(`${SWEEP_CADENCE_MINUTES}m`);
    keepHotPoolSaturated(1);
    sleep(1); // let the tick's async sweep actually run before the next advance
  }
}

// ---- Stage 5: poll for the T0 probe markers on the DB-only surface ----
function checkColdProbesDrained(markers) {
  for (const marker of markers) {
    const res = http.get(`${TARGET}/api/v1/detections?limit=500`, {
      headers: { 'X-SB-Api-Key': API_KEY },
      tags: { traffic_type: 'drain_check' },
    });
    const drained = res.status === 200 && res.body && res.body.includes(marker);
    coldProbeDrained.add(drained ? 1 : 0);
  }
}

export const options = {
  scenarios: {
    hot_warmup: {
      executor: 'constant-vus',
      vus: 20,
      duration: `${WARMUP_SECONDS}s`,
      exec: 'warmup',
    },
    starvation_scenario: {
      executor: 'shared-iterations',
      vus: 1,
      iterations: 1,
      startTime: `${WARMUP_SECONDS}s`,
      maxDuration: '20m',
      exec: 'runScenario',
    },
  },
  thresholds: {
    // Zero tolerance on the never-visible fraction -- any non-zero rate is a fail.
    cold_probe_drained: ['rate==1'],
  },
};

export function runScenario() {
  const markers = seedColdProbes();
  advanceThroughTicks();
  checkColdProbesDrained(markers);
}

export function handleSummary(data) {
  const drainedRate = data.metrics.cold_probe_drained?.values?.rate ?? null;
  const seeded = data.metrics.cold_probe_seeded?.values?.count ?? 0;
  console.log('\n=== Visit-walk starvation scenario ===');
  console.log(`Hot pool size:        ${HOT_SIGNATURE_COUNT}`);
  console.log(`Cold probes seeded:   ${seeded}`);
  console.log(`Cold probe drain rate: ${drainedRate === null ? 'N/A' : (drainedRate * 100).toFixed(1) + '%'}`);
  console.log(`Ticks advanced:       ${TOTAL_TICKS_TO_ADVANCE} (ticksPerHotWindow=${TICKS_PER_HOT_WINDOW} + margin=${TICK_ADVANCE_MARGIN})`);
  console.log('REMINDER: this script cannot itself watch the gateway log for the');
  console.log('"Session persistence channel FULL" abort signal -- that must be tailed');
  console.log('separately on the rig host WHILE this runs. See the design doc.');
  console.log('100% drain rate => the fix works. <100% => starvation reproduced (expected pre-fix).');
  console.log('=========================================\n');
  return { stdout: JSON.stringify(data, null, 2) };
}
