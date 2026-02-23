// k6 v5 Scale Test: Stream Detection, Intent/Threat, and Heuristic Verification
//
// Tests three major v5 systems:
//   1. Stream-aware detection (SignalR/WebSocket/SSE false positives + abuse)
//   2. Intent classification and threat scoring
//   3. Heuristic system (logistic regression, early + late mode)
//
// 14 scenarios, ~90 seconds each, all run in parallel.
//
// Usage:
//   k6 run k6-v5-stream-intent.js --env BASE_URL=http://localhost:5090
//
// Requires API key SB-K6-V5-TEST in appsettings.json (logonly policy).

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import encoding from 'k6/encoding';

// ===== Custom Metrics (16) =====

// Stream false positives (CRITICAL)
const signalrFalsePositives = new Rate('signalr_false_positives');
const websocketFalsePositives = new Rate('websocket_false_positives');
const sseFalsePositives = new Rate('sse_false_positives');

// Abuse detection
const streamAbuseDetected = new Rate('stream_abuse_detected');

// Intent/threat
const intentThreatAccuracy = new Rate('intent_threat_accuracy');
const threatScore = new Trend('threat_score', true);
const threatFieldsPresent = new Rate('threat_fields_present');
const dashboardApiOk = new Rate('dashboard_api_ok');

// Heuristic
const heuristicRan = new Rate('heuristic_ran');
const heuristicEdgeRan = new Rate('heuristic_edge_ran');
const heuristicHumanFp = new Rate('heuristic_human_fp');
const heuristicFeatureCount = new Trend('heuristic_feature_count', true);

// General
const errors = new Rate('errors');
const detectionTimeMs = new Trend('detection_time_ms', true);
const detectorsReported = new Counter('detectors_reported');

// ===== Config =====

const BASE = __ENV.BASE_URL || 'http://localhost:5090';
const API_KEY = __ENV.V5_API_KEY || 'SB-K6-V5-TEST';
const DASHBOARD_PATH = __ENV.DASHBOARD_PATH || '/_stylobot';

// ===== Scenarios =====

export const options = {
    scenarios: {
        // --- Stream Detection (6 scenarios) ---
        signalr_legitimate: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'signalrLegitimate',
            tags: { scenario: 'signalr_legitimate' },
        },
        signalr_abuse: {
            executor: 'constant-vus',
            vus: 1,
            duration: '90s',
            exec: 'signalrAbuse',
            tags: { scenario: 'signalr_abuse' },
        },
        websocket_legitimate: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'websocketLegitimate',
            tags: { scenario: 'websocket_legitimate' },
        },
        websocket_abuse: {
            executor: 'constant-vus',
            vus: 1,
            duration: '90s',
            exec: 'websocketAbuse',
            tags: { scenario: 'websocket_abuse' },
        },
        sse_legitimate: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'sseLegitimate',
            tags: { scenario: 'sse_legitimate' },
        },
        sse_abuse: {
            executor: 'constant-vus',
            vus: 1,
            duration: '90s',
            exec: 'sseAbuse',
            tags: { scenario: 'sse_abuse' },
        },

        // --- Intent/Threat Scoring (4 scenarios) ---
        intent_probing: {
            executor: 'constant-vus',
            vus: 2,
            duration: '90s',
            exec: 'intentProbing',
            tags: { scenario: 'intent_probing' },
        },
        intent_attacking: {
            executor: 'constant-vus',
            vus: 2,
            duration: '90s',
            exec: 'intentAttacking',
            tags: { scenario: 'intent_attacking' },
        },
        intent_browsing: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'intentBrowsing',
            tags: { scenario: 'intent_browsing' },
        },
        threat_api_verification: {
            executor: 'constant-vus',
            vus: 2,
            duration: '90s',
            exec: 'threatApiVerification',
            tags: { scenario: 'threat_api_verification' },
        },

        // --- Heuristic System (3 scenarios) ---
        heuristic_full_feature: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'heuristicFullFeature',
            tags: { scenario: 'heuristic_full_feature' },
        },
        heuristic_edge_cases: {
            executor: 'constant-vus',
            vus: 2,
            duration: '90s',
            exec: 'heuristicEdgeCases',
            tags: { scenario: 'heuristic_edge_cases' },
        },
        heuristic_human_baseline: {
            executor: 'constant-vus',
            vus: 3,
            duration: '90s',
            exec: 'heuristicHumanBaseline',
            tags: { scenario: 'heuristic_human_baseline' },
        },

        // --- Cross-Cutting (1 scenario) ---
        mixed_stream_pages: {
            executor: 'constant-vus',
            vus: 2,
            duration: '90s',
            exec: 'mixedStreamPages',
            tags: { scenario: 'mixed_stream_pages' },
        },
    },
    thresholds: {
        // Stream false positives (CRITICAL)
        'signalr_false_positives': ['rate<0.01'],
        'websocket_false_positives': ['rate<0.01'],
        'sse_false_positives': ['rate<0.01'],
        // Abuse detection
        'stream_abuse_detected': ['rate>0.5'],
        // Intent/threat
        'intent_threat_accuracy': ['rate>0.7'],
        // Heuristic
        'heuristic_ran': ['rate>0.90'],
        'heuristic_human_fp': ['rate<0.05'],
        // General
        'errors': ['rate<0.05'],
        'http_req_duration': ['p(95)<2000'],
        'detection_time_ms': ['p(95)<200'],
    },
};

// ===== Helpers =====

function parseDetectionHeaders(res) {
    const isBot = res.headers['X-Bot-Detection'] === 'True' ||
                  parseFloat(res.headers['X-Bot-Risk-Score'] || '0') >= 0.7;
    const procTime = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    const detectors = res.headers['X-Bot-Detectors'] || '';
    const detectorCount = detectors ? detectors.split(',').length : 0;
    const riskScore = parseFloat(res.headers['X-Bot-Risk-Score'] || '0');
    const aiRan = res.headers['X-Bot-Ai-Ran'] === 'true' || res.headers['X-Bot-Ai-Ran'] === 'True';
    const confidence = parseFloat(res.headers['X-Bot-Confidence'] || '0');
    return { isBot, procTime, detectors, detectorCount, riskScore, aiRan, confidence };
}

function parseThreatHeaders(res) {
    const ts = parseFloat(res.headers['X-Bot-Threat-Score'] || '-1');
    const threatBand = res.headers['X-Bot-Threat-Band'] || '';
    return { threatScore: ts, threatBand };
}

function checkNotFlaggedAsBot(res, metricRate, requestName) {
    const riskScore = parseFloat(res.headers['X-Bot-Risk-Score'] || '0');
    const isFlagged = riskScore >= 0.7 || res.status === 403;
    metricRate.add(isFlagged ? 1 : 0);
    if (isFlagged) {
        console.warn(`FALSE POSITIVE: ${requestName} flagged as bot (risk=${riskScore}, status=${res.status}, detectors=${res.headers['X-Bot-Detectors'] || 'none'})`);
    }
    return !isFlagged;
}

function checkThreatDetected(res, minScore, metricRate, requestName) {
    const { threatScore: ts } = parseThreatHeaders(res);
    const accurate = ts >= minScore;
    metricRate.add(accurate ? 1 : 0);
    if (ts >= 0) threatScore.add(ts);
    if (!accurate) {
        console.warn(`THREAT MISS: ${requestName} expected threat>=${minScore}, got ${ts} (band=${res.headers['X-Bot-Threat-Band'] || 'none'})`);
    }
    return accurate;
}

function trackGeneral(res) {
    errors.add(res.status >= 500 ? 1 : 0);
    const procTime = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    if (procTime > 0) detectionTimeMs.add(procTime);
}

function generateWsKey() {
    // Generate a valid 16-byte base64-encoded WebSocket key
    const bytes = new Uint8Array(16);
    for (let i = 0; i < 16; i++) {
        bytes[i] = Math.floor(Math.random() * 256);
    }
    return encoding.b64encode(bytes);
}

// Common Chrome 134 browser headers
const CHROME_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36';

const CHROME_COMMON = {
    'User-Agent': CHROME_UA,
    'Accept-Language': 'en-US,en;q=0.9',
    'Accept-Encoding': 'gzip, deflate, br',
    'Connection': 'keep-alive',
};

function withApiKey(headers) {
    return Object.assign({}, headers, { 'X-SB-Api-Key': API_KEY });
}

// ===== Stream Detection Scenarios =====

// --- SignalR Legitimate ---
// Browser-like SignalR negotiate + long-poll with Sec-Fetch-* headers
export function signalrLegitimate() {
    // Step 1: POST negotiate
    const negotiateRes = http.post(
        `${BASE}${DASHBOARD_PATH}/hub/negotiate?negotiateVersion=1`,
        null,
        {
            headers: withApiKey(Object.assign({}, CHROME_COMMON, {
                'Accept': '*/*',
                'Content-Type': 'text/plain;charset=UTF-8',
                'Sec-Fetch-Site': 'same-origin',
                'Sec-Fetch-Mode': 'cors',
                'Sec-Fetch-Dest': 'empty',
                'X-Requested-With': 'XMLHttpRequest',
                'Referer': `${BASE}${DASHBOARD_PATH}`,
            })),
            tags: { name: 'signalr_negotiate' },
        }
    );

    trackGeneral(negotiateRes);
    checkNotFlaggedAsBot(negotiateRes, signalrFalsePositives, 'signalr_negotiate');

    check(negotiateRes, {
        'signalr negotiate responds': (r) => r.status < 500,
    });

    // Step 2: GET long-poll connect (simulated with connection token if available)
    let connectionToken = 'k6-test-token';
    if (negotiateRes.status === 200) {
        try {
            const body = JSON.parse(negotiateRes.body);
            connectionToken = body.connectionToken || body.connectionId || connectionToken;
        } catch (_) {}
    }

    const pollRes = http.get(
        `${BASE}${DASHBOARD_PATH}/hub?id=${connectionToken}`,
        {
            headers: withApiKey(Object.assign({}, CHROME_COMMON, {
                'Accept': '*/*',
                'Sec-Fetch-Site': 'same-origin',
                'Sec-Fetch-Mode': 'cors',
                'Sec-Fetch-Dest': 'empty',
                'Referer': `${BASE}${DASHBOARD_PATH}`,
            })),
            tags: { name: 'signalr_longpoll' },
        }
    );

    trackGeneral(pollRes);
    checkNotFlaggedAsBot(pollRes, signalrFalsePositives, 'signalr_longpoll');

    sleep(2 + Math.random() * 3); // Simulate real polling interval
}

// --- SignalR Abuse ---
// Rapid WS upgrade storm to hub path with bot UA, missing WS key
export function signalrAbuse() {
    const res = http.get(
        `${BASE}${DASHBOARD_PATH}/hub/negotiate?negotiateVersion=1`,
        {
            headers: withApiKey({
                'User-Agent': 'python-requests/2.31.0',
                'Accept': '*/*',
                'Upgrade': 'websocket',
                'Connection': 'Upgrade',
                // Missing Sec-WebSocket-Key and Sec-WebSocket-Version (abuse indicator)
            }),
            tags: { name: 'signalr_abuse_storm' },
        }
    );

    trackGeneral(res);
    const { isBot, detectors } = parseDetectionHeaders(res);

    // Abuse should be detected — StreamAbuse or TransportProtocol signals
    const hasStreamDetection = detectors.toLowerCase().includes('stream') ||
                               detectors.toLowerCase().includes('transport') ||
                               isBot;
    streamAbuseDetected.add(hasStreamDetection ? 1 : 0);

    // No sleep — rapid-fire is the point
}

// --- WebSocket Legitimate ---
// RFC 6455 compliant upgrade (valid key, version 13, matching Origin)
export function websocketLegitimate() {
    const wsKey = generateWsKey();

    const res = http.get(`${BASE}/ws`, {
        headers: withApiKey(Object.assign({}, CHROME_COMMON, {
            'Upgrade': 'websocket',
            'Connection': 'Upgrade',
            'Sec-WebSocket-Key': wsKey,
            'Sec-WebSocket-Version': '13',
            'Origin': BASE,
            'Sec-Fetch-Mode': 'websocket',
            'Sec-Fetch-Dest': 'websocket',
            'Sec-Fetch-Site': 'same-origin',
        })),
        tags: { name: 'websocket_legitimate' },
    });

    trackGeneral(res);
    checkNotFlaggedAsBot(res, websocketFalsePositives, 'websocket_legitimate');

    check(res, {
        'ws legitimate responds': (r) => r.status < 500,
    });

    sleep(3 + Math.random() * 5); // Real WS connections are long-lived
}

// --- WebSocket Abuse ---
// Handshake storm across 6 different WS paths, no sleep
export function websocketAbuse() {
    const paths = ['/ws', '/ws/chat', '/ws/feed', '/ws/live', '/ws/data', '/ws/stream'];
    const path = paths[Math.floor(Math.random() * paths.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: withApiKey({
            'User-Agent': 'Go-http-client/2.0',
            'Upgrade': 'websocket',
            'Connection': 'Upgrade',
            // Missing proper Sec-WebSocket-Key, Sec-WebSocket-Version, Origin
            'Accept': '*/*',
        }),
        tags: { name: 'websocket_abuse_storm' },
    });

    trackGeneral(res);
    const { isBot, detectors } = parseDetectionHeaders(res);
    const hasStreamDetection = detectors.toLowerCase().includes('stream') ||
                               detectors.toLowerCase().includes('transport') ||
                               isBot;
    streamAbuseDetected.add(hasStreamDetection ? 1 : 0);

    // No sleep — rapid handshake storm
}

// --- SSE Legitimate ---
// EventSource with Cache-Control: no-cache, same-origin Sec-Fetch-*
export function sseLegitimate() {
    const res = http.get(`${BASE}${DASHBOARD_PATH}/api/stream`, {
        headers: withApiKey(Object.assign({}, CHROME_COMMON, {
            'Accept': 'text/event-stream',
            'Cache-Control': 'no-cache',
            'Sec-Fetch-Site': 'same-origin',
            'Sec-Fetch-Mode': 'cors',
            'Sec-Fetch-Dest': 'empty',
            'Referer': `${BASE}${DASHBOARD_PATH}`,
        })),
        tags: { name: 'sse_legitimate' },
    });

    trackGeneral(res);
    checkNotFlaggedAsBot(res, sseFalsePositives, 'sse_legitimate');

    check(res, {
        'sse legitimate responds': (r) => r.status < 500,
    });

    sleep(5 + Math.random() * 5); // SSE connections are long-lived
}

// --- SSE Abuse ---
// 20+ reconnects/60s with Last-Event-ID: 0 history replay, no Cache-Control
export function sseAbuse() {
    const res = http.get(`${BASE}${DASHBOARD_PATH}/api/stream`, {
        headers: withApiKey({
            'User-Agent': 'python-requests/2.31.0',
            'Accept': 'text/event-stream',
            'Last-Event-ID': '0',
            // Missing Cache-Control (abuse indicator for SSE)
        }),
        tags: { name: 'sse_abuse_reconnect' },
    });

    trackGeneral(res);
    const { isBot, detectors } = parseDetectionHeaders(res);
    const hasStreamDetection = detectors.toLowerCase().includes('stream') ||
                               detectors.toLowerCase().includes('transport') ||
                               isBot;
    streamAbuseDetected.add(hasStreamDetection ? 1 : 0);

    sleep(0.5); // Rapid reconnects but not instant (simulating ~20+/60s per VU)
}

// ===== Intent/Threat Scoring Scenarios =====

// --- Intent Probing ---
// Hit probe paths (.env, wp-admin, .git, phpmyadmin, actuator)
const PROBE_PATHS = [
    '/.env',
    '/wp-admin/',
    '/wp-login.php',
    '/.git/config',
    '/phpmyadmin/',
    '/actuator/env',
    '/server-status',
    '/backup.sql',
    '/.aws/credentials',
    '/config.php',
];

export function intentProbing() {
    const path = PROBE_PATHS[Math.floor(Math.random() * PROBE_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: withApiKey({
            'User-Agent': 'Mozilla/5.0 (compatible; Nmap Scripting Engine)',
            'Accept': '*/*',
        }),
        tags: { name: 'intent_probing' },
    });

    trackGeneral(res);
    // Expect threat score >= 0.55 (High band)
    checkThreatDetected(res, 0.55, intentThreatAccuracy, 'intent_probing');

    sleep(1 + Math.random() * 2);
}

// --- Intent Attacking ---
// SQLi/XSS/SSTI/SSRF payloads with sqlmap UA
const ATTACK_PAYLOADS = [
    { path: '/search', qs: "?q=1' UNION SELECT username,password FROM users--" },
    { path: '/products', qs: "?id=1 OR 1=1--" },
    { path: '/search', qs: '?q=<script>alert(document.cookie)</script>' },
    { path: '/profile', qs: '?name=<img onerror=alert(1) src=x>' },
    { path: '/render', qs: '?tpl={{7*7}}' },
    { path: '/api/eval', qs: '?expr=${Runtime.exec("id")}' },
    { path: '/proxy', qs: '?url=http://169.254.169.254/latest/meta-data/' },
    { path: '/fetch', qs: '?url=http://localhost:6379/' },
    { path: '/download', qs: '?file=../../../etc/passwd' },
    { path: '/ping', qs: '?host=;cat /etc/passwd' },
];

const ATTACK_UAS = [
    'sqlmap/1.7.12',
    'nikto/2.5.0',
    'Mozilla/5.0 (compatible; Nmap Scripting Engine)',
];

export function intentAttacking() {
    const attack = ATTACK_PAYLOADS[Math.floor(Math.random() * ATTACK_PAYLOADS.length)];
    const ua = ATTACK_UAS[Math.floor(Math.random() * ATTACK_UAS.length)];

    const res = http.get(`${BASE}${attack.path}${attack.qs}`, {
        headers: withApiKey({
            'User-Agent': ua,
            'Accept': '*/*',
        }),
        tags: { name: 'intent_attacking' },
    });

    trackGeneral(res);
    // Expect threat score >= 0.80 (Critical band)
    checkThreatDetected(res, 0.80, intentThreatAccuracy, 'intent_attacking');

    sleep(0.5 + Math.random());
}

// --- Intent Browsing ---
// Normal human browsing with Chrome UA, full Sec-Fetch-*
const BROWSE_PATHS = ['/', '/about', '/features', '/pricing', '/docs', '/blog', '/contact'];

export function intentBrowsing() {
    const path = BROWSE_PATHS[Math.floor(Math.random() * BROWSE_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: withApiKey(Object.assign({}, CHROME_COMMON, {
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-User': '?1',
            'Referer': 'https://www.google.com/',
            'Cookie': 'sb-theme=light; _ga=GA1.1.123456789',
            'Upgrade-Insecure-Requests': '1',
            'DNT': '1',
        })),
        tags: { name: 'intent_browsing' },
    });

    trackGeneral(res);
    // Expect threat score < 0.15 (None band)
    const { threatScore: ts } = parseThreatHeaders(res);
    const accurate = ts < 0.15 || ts < 0; // ts < 0 means header missing, acceptable
    intentThreatAccuracy.add(accurate ? 1 : 0);
    if (ts >= 0) threatScore.add(ts);
    if (!accurate) {
        console.warn(`BROWSING THREAT FP: expected threat<0.15, got ${ts} (band=${res.headers['X-Bot-Threat-Band'] || 'none'})`);
    }

    sleep(1 + Math.random() * 3);
}

// --- Threat API Verification ---
// Poll dashboard APIs, verify threat fields exist in JSON responses
const THREAT_API_ENDPOINTS = [
    `${DASHBOARD_PATH}/api/summary`,
    `${DASHBOARD_PATH}/api/detections?limit=20`,
    `${DASHBOARD_PATH}/api/topbots?count=10`,
    `${DASHBOARD_PATH}/api/signatures?limit=20`,
];

export function threatApiVerification() {
    const endpoint = THREAT_API_ENDPOINTS[Math.floor(Math.random() * THREAT_API_ENDPOINTS.length)];

    const res = http.get(`${BASE}${endpoint}`, {
        headers: withApiKey(Object.assign({}, CHROME_COMMON, {
            'Accept': 'application/json',
            'Sec-Fetch-Site': 'same-origin',
            'Sec-Fetch-Mode': 'cors',
            'Sec-Fetch-Dest': 'empty',
        })),
        tags: { name: 'threat_api_verify' },
    });

    trackGeneral(res);
    const ok = res.status === 200;
    dashboardApiOk.add(ok ? 1 : 0);

    if (ok && res.body) {
        try {
            const data = JSON.parse(res.body);
            let hasFields = false;

            if (endpoint.includes('/summary')) {
                // Summary should have averageThreatScore
                hasFields = data.averageThreatScore !== undefined ||
                            data.AverageThreatScore !== undefined;
            } else if (endpoint.includes('/detections') && Array.isArray(data)) {
                // Detections array items should have threatScore/threatBand/dominantIntent
                if (data.length > 0) {
                    const first = data[0];
                    hasFields = (first.threatScore !== undefined || first.ThreatScore !== undefined) &&
                                (first.threatBand !== undefined || first.ThreatBand !== undefined);
                } else {
                    hasFields = true; // Empty array is OK (no traffic yet)
                }
            } else if (endpoint.includes('/topbots') && Array.isArray(data)) {
                if (data.length > 0) {
                    const first = data[0];
                    hasFields = first.threatScore !== undefined || first.ThreatScore !== undefined ||
                                first.averageThreatScore !== undefined || first.AverageThreatScore !== undefined;
                } else {
                    hasFields = true;
                }
            } else if (endpoint.includes('/signatures') && Array.isArray(data)) {
                if (data.length > 0) {
                    const first = data[0];
                    hasFields = first.threatScore !== undefined || first.ThreatScore !== undefined ||
                                first.dominantIntent !== undefined || first.DominantIntent !== undefined;
                } else {
                    hasFields = true;
                }
            } else {
                hasFields = true; // Unknown endpoint shape, pass
            }

            threatFieldsPresent.add(hasFields ? 1 : 0);
        } catch (_) {
            threatFieldsPresent.add(0);
        }
    } else {
        threatFieldsPresent.add(0);
    }

    sleep(2 + Math.random() * 2);
}

// ===== Heuristic System Scenarios =====

// --- Heuristic Full Feature ---
// Rotates through traffic profiles designed to trigger maximum feature extraction
const HEURISTIC_PROFILES = [
    {
        name: 'bot_scraper',
        headers: {
            'User-Agent': 'python-requests/2.31.0',
            'Accept': '*/*',
            // Missing Accept-Language, no cookies, no Referer
        },
        path: '/blog',
    },
    {
        name: 'headless_browser',
        headers: {
            'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36',
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            // No cookies, no Referer
        },
        path: '/',
    },
    {
        name: 'curl_probe',
        headers: {
            'User-Agent': 'curl/8.4.0',
            'Accept': '*/*',
        },
        path: '/.env',
    },
    {
        name: 'api_client',
        headers: {
            'User-Agent': 'Go-http-client/2.0',
            'Accept': 'application/json',
            'Connection': 'close',
        },
        path: '/api/data',
    },
    {
        name: 'scrapy_crawler',
        headers: {
            'User-Agent': 'Scrapy/2.11.0',
            'Accept': 'text/html',
            'Accept-Encoding': 'gzip, deflate',
        },
        path: '/sitemap.xml',
    },
    {
        name: 'wget_download',
        headers: {
            'User-Agent': 'wget/1.21.4',
            'Accept': '*/*',
        },
        path: '/robots.txt',
    },
];

export function heuristicFullFeature() {
    const profile = HEURISTIC_PROFILES[Math.floor(Math.random() * HEURISTIC_PROFILES.length)];

    const res = http.get(`${BASE}${profile.path}`, {
        headers: withApiKey(profile.headers),
        tags: { name: `heuristic_${profile.name}` },
    });

    trackGeneral(res);
    const { aiRan, detectorCount, riskScore } = parseDetectionHeaders(res);

    heuristicRan.add(aiRan ? 1 : 0);
    if (detectorCount > 0) {
        heuristicFeatureCount.add(detectorCount);
        detectorsReported.add(detectorCount);
    }

    check(res, {
        'heuristic: status < 500': (r) => r.status < 500,
        'heuristic: AI ran': () => aiRan,
        'heuristic: 5+ detectors': () => detectorCount >= 5,
        'heuristic: risk > 0.5 for bot': () => riskScore > 0.5,
    });

    sleep(1 + Math.random() * 2);
}

// --- Heuristic Edge Cases ---
// Boundary conditions: empty UA, HEAD method, wildcard Accept, Connection: close
const EDGE_CASE_PROFILES = [
    {
        name: 'empty_ua',
        method: 'GET',
        headers: {
            // No User-Agent at all
            'Accept': 'text/html',
        },
        path: '/',
    },
    {
        name: 'very_short_ua',
        method: 'GET',
        headers: {
            'User-Agent': 'X',
            'Accept': '*/*',
        },
        path: '/about',
    },
    {
        name: 'head_method',
        method: 'HEAD',
        headers: {
            'User-Agent': 'curl/8.4.0',
            'Accept': '*/*',
        },
        path: '/',
    },
    {
        name: 'mixed_signals',
        method: 'GET',
        headers: {
            'User-Agent': CHROME_UA,
            'Connection': 'close',
            'Accept': '*/*',
            // Browser UA but Connection: close and no cookies — competing features
        },
        path: '/docs',
    },
    {
        name: 'wildcard_accept_no_lang',
        method: 'GET',
        headers: {
            'User-Agent': 'node-fetch/3.3.2',
            'Accept': '*/*',
            'Connection': 'close',
        },
        path: '/api/data',
    },
];

export function heuristicEdgeCases() {
    const profile = EDGE_CASE_PROFILES[Math.floor(Math.random() * EDGE_CASE_PROFILES.length)];

    let res;
    const hdrs = withApiKey(profile.headers);

    if (profile.method === 'HEAD') {
        res = http.head(`${BASE}${profile.path}`, {
            headers: hdrs,
            tags: { name: `heuristic_edge_${profile.name}` },
        });
    } else {
        res = http.get(`${BASE}${profile.path}`, {
            headers: hdrs,
            tags: { name: `heuristic_edge_${profile.name}` },
        });
    }

    trackGeneral(res);
    const { aiRan } = parseDetectionHeaders(res);

    heuristicEdgeRan.add(aiRan ? 1 : 0);

    check(res, {
        'edge: status < 500': (r) => r.status < 500,
        'edge: AI ran': () => aiRan,
    });

    sleep(1 + Math.random() * 2);
}

// --- Heuristic Human Baseline ---
// Full Chrome 134 browser simulation with ALL human indicators
export function heuristicHumanBaseline() {
    const path = BROWSE_PATHS[Math.floor(Math.random() * BROWSE_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: withApiKey({
            'User-Agent': CHROME_UA,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br, zstd',
            'Connection': 'keep-alive',
            'Referer': 'https://www.google.com/',
            'Cookie': 'sb-theme=light; _ga=GA1.1.123456789; session=abc123',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-User': '?1',
            'Upgrade-Insecure-Requests': '1',
            'Cache-Control': 'max-age=0',
            'DNT': '1',
            'Sec-Ch-Ua': '"Chromium";v="134", "Google Chrome";v="134", "Not:A-Brand";v="24"',
            'Sec-Ch-Ua-Mobile': '?0',
            'Sec-Ch-Ua-Platform': '"Windows"',
        }),
        tags: { name: 'heuristic_human_baseline' },
    });

    trackGeneral(res);
    const { isBot, riskScore, aiRan } = parseDetectionHeaders(res);

    // Human should NOT be flagged as bot
    heuristicHumanFp.add(isBot ? 1 : 0);
    if (aiRan) heuristicRan.add(1); // Also contributes to heuristic_ran metric

    check(res, {
        'human baseline: status 200': (r) => r.status === 200,
        'human baseline: not flagged as bot': () => !isBot,
        'human baseline: risk < 0.5': () => riskScore < 0.5,
    });

    if (isBot) {
        console.warn(`HUMAN FP: risk=${riskScore}, detectors=${res.headers['X-Bot-Detectors'] || 'none'}, aiRan=${aiRan}`);
    }

    sleep(2 + Math.random() * 3);
}

// ===== Cross-Cutting Scenario =====

// --- Mixed Stream + Pages ---
// Mix 6 page requests + 4 SSE requests from same VU to trigger CrossEndpointMixing
// (needs StreamRequests >= 3 AND PageRequests >= 5)
export function mixedStreamPages() {
    // Phase 1: 6 page requests (build PageRequests count)
    for (let i = 0; i < 6; i++) {
        const path = BROWSE_PATHS[Math.floor(Math.random() * BROWSE_PATHS.length)];
        const res = http.get(`${BASE}${path}`, {
            headers: withApiKey({
                'User-Agent': 'Go-http-client/2.0',
                'Accept': 'text/html',
            }),
            tags: { name: 'mixed_page' },
        });
        trackGeneral(res);
        sleep(0.2);
    }

    // Phase 2: 4 SSE requests (build StreamRequests count, triggers CrossEndpointMixing)
    for (let i = 0; i < 4; i++) {
        const res = http.get(`${BASE}${DASHBOARD_PATH}/api/stream`, {
            headers: withApiKey({
                'User-Agent': 'Go-http-client/2.0',
                'Accept': 'text/event-stream',
                'Last-Event-ID': '0',
            }),
            tags: { name: 'mixed_sse' },
        });

        trackGeneral(res);
        const { isBot, detectors } = parseDetectionHeaders(res);
        const hasStreamDetection = detectors.toLowerCase().includes('stream') ||
                                   detectors.toLowerCase().includes('transport') ||
                                   detectors.toLowerCase().includes('crossendpoint') ||
                                   isBot;
        streamAbuseDetected.add(hasStreamDetection ? 1 : 0);
        sleep(0.5);
    }

    sleep(2);
}
