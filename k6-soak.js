// k6 Soak Test for StyloBot Demo Stack
// Tests: mixed traffic (human browsers, bots, attack payloads, API key scenarios)
// Run: k6 run k6-soak.js
//
// Profiles against the Caddy frontend on localhost (port 80)
// or set BASE_URL env var to target a different endpoint.
//
// API Key env vars (set to match docker-compose.demo.yml values):
//   DASHBOARD_API_KEY     — dashboard monitor key (default: SB-DASHBOARD-MONITOR)
//   FULL_DETECTION_KEY    — all detectors enabled, logonly (default: SB-K6-FULL-DETECTION)
//   BYPASS_KEY            — all detectors disabled, latency baseline (default: SB-K6-BYPASS)
//   NO_BEHAVIORAL_KEY     — behavioral detectors disabled (default: SB-K6-NO-BEHAVIORAL)

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const botDetected = new Rate('bot_detected');
const attackBlocked = new Rate('attack_blocked');
const detectionTime = new Trend('detection_time_ms', true);
const errorRate = new Rate('errors');
const dashboardOk = new Rate('dashboard_ok');
const fullDetectionDetectors = new Trend('full_detection_detector_count', true);
const bypassLatency = new Trend('bypass_baseline_latency_ms', true);
const noBehavioralDetectors = new Trend('no_behavioral_detector_count', true);
const detectorsReported = new Counter('detectors_reported');

const BASE = __ENV.BASE_URL || 'http://localhost';

// API keys (match docker-compose.demo.yml defaults)
const DASHBOARD_KEY = __ENV.DASHBOARD_API_KEY || 'SB-DASHBOARD-MONITOR';
const FULL_DETECTION_KEY = __ENV.FULL_DETECTION_KEY || 'SB-K6-FULL-DETECTION';
const BYPASS_KEY = __ENV.BYPASS_KEY || 'SB-K6-BYPASS';
const NO_BEHAVIORAL_KEY = __ENV.NO_BEHAVIORAL_KEY || 'SB-K6-NO-BEHAVIORAL';

export const options = {
    scenarios: {
        // Human browser traffic (steady baseline)
        human_browsing: {
            executor: 'constant-arrival-rate',
            rate: 20,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 30,
            maxVUs: 50,
            exec: 'humanBrowsing',
        },
        // Bot scraping traffic (ramp up then sustain)
        bot_scraping: {
            executor: 'ramping-arrival-rate',
            startRate: 5,
            timeUnit: '1s',
            stages: [
                { duration: '1m', target: 10 },
                { duration: '3m', target: 30 },
                { duration: '1m', target: 5 },
            ],
            preAllocatedVUs: 40,
            maxVUs: 80,
            exec: 'botScraping',
        },
        // Attack payloads (SQLi, XSS, path probes)
        attack_traffic: {
            executor: 'constant-arrival-rate',
            rate: 5,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 10,
            maxVUs: 20,
            exec: 'attackTraffic',
        },
        // Credential stuffing pattern
        credential_stuffing: {
            executor: 'constant-arrival-rate',
            rate: 3,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 5,
            maxVUs: 10,
            exec: 'credentialStuffing',
        },
        // Dashboard API polling (uses dashboard-monitor API key)
        dashboard_polling: {
            executor: 'constant-arrival-rate',
            rate: 1,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 3,
            maxVUs: 5,
            exec: 'dashboardPolling',
        },
        // Full detection — ALL detectors, verifies every detector fires
        full_detection_test: {
            executor: 'constant-arrival-rate',
            rate: 2,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 5,
            maxVUs: 10,
            exec: 'fullDetectionTest',
        },
        // Bypass baseline — ALL detectors disabled, measures raw proxy latency
        bypass_baseline: {
            executor: 'constant-arrival-rate',
            rate: 2,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 5,
            maxVUs: 10,
            exec: 'bypassBaseline',
        },
        // No-behavioral — only fingerprint/header detectors, isolates detection layers
        no_behavioral_test: {
            executor: 'constant-arrival-rate',
            rate: 2,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 5,
            maxVUs: 10,
            exec: 'noBehavioralTest',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<2000'],
        errors: ['rate<0.05'],
        dashboard_ok: ['rate>0.95'],
        bypass_baseline_latency_ms: ['p(95)<500'],  // Proxy-only should be fast
    },
};

// ===== Helpers =====

function parseDetectionHeaders(res) {
    const isBot = res.headers['X-Bot-Detection'] === 'True' ||
                  res.headers['X-Bot-Risk-Score'] > '0.5';
    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
    const detectors = res.headers['X-Bot-Detectors'] || '';
    const detectorCount = detectors ? detectors.split(',').length : 0;
    return { isBot, procTime, detectors, detectorCount };
}

// ===== Human Browser Scenarios =====

const HUMAN_UAS = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
    'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0',
];

const HUMAN_PATHS = ['/', '/about', '/features', '/pricing', '/docs', '/blog', '/contact', '/dashboard'];

export function humanBrowsing() {
    const ua = HUMAN_UAS[Math.floor(Math.random() * HUMAN_UAS.length)];
    const path = HUMAN_PATHS[Math.floor(Math.random() * HUMAN_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br',
            'Referer': 'https://www.google.com/',
            'DNT': '1',
            'Connection': 'keep-alive',
        },
        tags: { scenario: 'human' },
    });

    const { isBot, procTime } = parseDetectionHeaders(res);
    botDetected.add(isBot ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);
    if (procTime > 0) detectionTime.add(procTime);

    check(res, {
        'human: status 200': (r) => r.status === 200,
        'human: not flagged as bot': () => !isBot,
    });

    sleep(Math.random() * 0.5);
}

// ===== Bot Scraping Scenarios =====

const BOT_UAS = [
    'curl/8.4.0',
    'python-requests/2.31.0',
    'Go-http-client/2.0',
    'Scrapy/2.11.0',
    'Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)',
    'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
    'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)',
    'Mozilla/5.0 (compatible; SemrushBot/7; +http://www.semrush.com/bot.html)',
    'wget/1.21.4',
    'Java/17.0.9',
    'node-fetch/3.3.2',
    'axios/1.6.3',
];

const BOT_PATHS = ['/api/data', '/sitemap.xml', '/robots.txt', '/', '/about', '/blog', '/feed'];

export function botScraping() {
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = BOT_PATHS[Math.floor(Math.random() * BOT_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': '*/*',
        },
        tags: { scenario: 'bot' },
    });

    const { isBot, procTime } = parseDetectionHeaders(res);
    botDetected.add(isBot ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);
    if (procTime > 0) detectionTime.add(procTime);

    check(res, {
        'bot: status < 500': (r) => r.status < 500,
    });
}

// ===== Attack Traffic (HaxxorContributor targets) =====

const ATTACK_PAYLOADS = [
    // SQL injection
    { path: '/search', qs: "?q=1' UNION SELECT username,password FROM users--" },
    { path: '/products', qs: "?id=1 OR 1=1--" },
    { path: '/api/items', qs: "?sort=name;DROP TABLE users--" },
    { path: '/search', qs: "?q=admin' AND SLEEP(5)--" },
    // XSS
    { path: '/search', qs: '?q=<script>alert(1)</script>' },
    { path: '/profile', qs: '?name=<img onerror=alert(1) src=x>' },
    // Path traversal
    { path: '/download', qs: '?file=../../../etc/passwd' },
    { path: '/files', qs: '?path=..%2f..%2f..%2fetc%2fshadow' },
    // SSRF
    { path: '/proxy', qs: '?url=http://169.254.169.254/latest/meta-data/' },
    { path: '/fetch', qs: '?url=http://localhost:6379/' },
    // Command injection
    { path: '/ping', qs: '?host=;cat /etc/passwd' },
    { path: '/tools', qs: '?cmd=test|whoami' },
    // Path probes (WordPress, config, admin)
    { path: '/wp-admin/', qs: '' },
    { path: '/wp-login.php', qs: '' },
    { path: '/.env', qs: '' },
    { path: '/.git/config', qs: '' },
    { path: '/phpmyadmin/', qs: '' },
    { path: '/actuator/env', qs: '' },
    { path: '/server-status', qs: '' },
    { path: '/c99.php', qs: '' },
    { path: '/shell.php', qs: '' },
    { path: '/backup.sql', qs: '' },
    // Template injection
    { path: '/render', qs: '?tpl={{7*7}}' },
    { path: '/api/eval', qs: '?expr=${Runtime.exec("id")}' },
    // Encoding evasion
    { path: '/search', qs: '?q=%2527%2520OR%25201%253D1' },
];

const ATTACK_UAS = [
    'sqlmap/1.7.12',
    'nikto/2.5.0',
    'python-requests/2.31.0',
    'curl/8.4.0',
    'Mozilla/5.0 (compatible; Nmap Scripting Engine)',
    'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36',
];

export function attackTraffic() {
    const attack = ATTACK_PAYLOADS[Math.floor(Math.random() * ATTACK_PAYLOADS.length)];
    const ua = ATTACK_UAS[Math.floor(Math.random() * ATTACK_UAS.length)];

    const res = http.get(`${BASE}${attack.path}${attack.qs}`, {
        headers: {
            'User-Agent': ua,
            'Accept': '*/*',
        },
        tags: { scenario: 'attack' },
    });

    const isBlocked = res.status === 403 || res.status === 429;
    attackBlocked.add(isBlocked ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);

    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
    if (procTime > 0) detectionTime.add(procTime);

    check(res, {
        'attack: status < 500': (r) => r.status < 500,
    });
}

// ===== Credential Stuffing (AccountTakeoverContributor targets) =====

export function credentialStuffing() {
    const ua = 'python-requests/2.31.0';
    const loginPaths = ['/login', '/signin', '/auth', '/api/auth/login', '/wp-login.php'];
    const path = loginPaths[Math.floor(Math.random() * loginPaths.length)];

    const res = http.post(`${BASE}${path}`, JSON.stringify({
        username: `user${Math.floor(Math.random() * 10000)}@example.com`,
        password: 'password123',
    }), {
        headers: {
            'User-Agent': ua,
            'Content-Type': 'application/json',
            'Accept': 'application/json',
        },
        tags: { scenario: 'credential_stuffing' },
    });

    errorRate.add(res.status >= 500 ? 1 : 0);

    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
    if (procTime > 0) detectionTime.add(procTime);

    check(res, {
        'stuffing: status < 500': (r) => r.status < 500,
    });
}

// ===== Dashboard API Polling (uses dashboard-monitor API key) =====

const DASHBOARD_ENDPOINTS = [
    '/_stylobot/api/summary',
    '/_stylobot/api/timeseries?bucket=60',
    '/_stylobot/api/detections?limit=20',
    '/_stylobot/api/signatures?limit=20',
    '/_stylobot/api/topbots?count=10',
    '/_stylobot/api/countries?count=20',
    '/_stylobot/api/clusters',
    '/_stylobot/api/useragents',
];

export function dashboardPolling() {
    const endpoint = DASHBOARD_ENDPOINTS[Math.floor(Math.random() * DASHBOARD_ENDPOINTS.length)];

    const res = http.get(`${BASE}${endpoint}`, {
        headers: {
            'Accept': 'application/json',
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
            'X-SB-Api-Key': DASHBOARD_KEY,
        },
        tags: { scenario: 'dashboard' },
    });

    const ok = res.status === 200;
    dashboardOk.add(ok ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);

    check(res, {
        'dashboard: status 200': (r) => r.status === 200,
        'dashboard: has body': (r) => r.body && r.body.length > 2,
    });

    // Verify detection data has signals populated
    if (endpoint.includes('/detections') && ok) {
        try {
            const data = JSON.parse(res.body);
            if (Array.isArray(data) && data.length > 0) {
                const withSignals = data.filter(d => {
                    const sigs = d.ImportantSignals || d.importantSignals;
                    return sigs && Object.keys(sigs).length > 0;
                });
                check(null, {
                    'detections: >50% have signals': () => withSignals.length > data.length * 0.5,
                });
            }
        } catch (_) {}
    }

    sleep(0.5);
}

// ===== Full Detection Test (ALL detectors, API key: SB-K6-FULL-DETECTION) =====
// Uses bot-like traffic WITH the full-detection API key to verify every detector fires.
// The key sets action to logonly so we get detection headers without being blocked.

export function fullDetectionTest() {
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = HUMAN_PATHS[Math.floor(Math.random() * HUMAN_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': '*/*',
            'X-SB-Api-Key': FULL_DETECTION_KEY,
        },
        tags: { scenario: 'full_detection' },
    });

    const { isBot, procTime, detectors, detectorCount } = parseDetectionHeaders(res);
    errorRate.add(res.status >= 500 ? 1 : 0);
    if (procTime > 0) detectionTime.add(procTime);
    if (detectorCount > 0) {
        fullDetectionDetectors.add(detectorCount);
        detectorsReported.add(detectorCount);
    }

    check(res, {
        'full-detect: status < 500': (r) => r.status < 500,
        'full-detect: has detection headers': (r) => r.headers['X-Bot-Detection'] !== undefined,
        'full-detect: has detectors header': (r) => r.headers['X-Bot-Detectors'] !== undefined,
        'full-detect: multiple detectors ran': () => detectorCount >= 3,
    });

    // Log which detectors fired (visible in k6 output with --verbose)
    if (detectors && __ENV.K6_VERBOSE) {
        console.log(`Full detection: ${detectorCount} detectors: ${detectors}`);
    }
}

// ===== Bypass Baseline (ALL detectors disabled, API key: SB-K6-BYPASS) =====
// Measures raw YARP proxy latency with zero detection overhead.

export function bypassBaseline() {
    const path = HUMAN_PATHS[Math.floor(Math.random() * HUMAN_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': HUMAN_UAS[0],
            'Accept': 'text/html',
            'X-SB-Api-Key': BYPASS_KEY,
        },
        tags: { scenario: 'bypass_baseline' },
    });

    bypassLatency.add(res.timings.duration);
    errorRate.add(res.status >= 500 ? 1 : 0);

    check(res, {
        'bypass: status < 500': (r) => r.status < 500,
        'bypass: no bot detection header': (r) => r.headers['X-Bot-Detection'] === undefined,
    });
}

// ===== No-Behavioral Test (behavioral detectors disabled, API key: SB-K6-NO-BEHAVIORAL) =====
// Isolates fingerprint + header + static detectors from behavioral analysis.

export function noBehavioralTest() {
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = HUMAN_PATHS[Math.floor(Math.random() * HUMAN_PATHS.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': '*/*',
            'X-SB-Api-Key': NO_BEHAVIORAL_KEY,
        },
        tags: { scenario: 'no_behavioral' },
    });

    const { isBot, procTime, detectors, detectorCount } = parseDetectionHeaders(res);
    errorRate.add(res.status >= 500 ? 1 : 0);
    if (procTime > 0) detectionTime.add(procTime);
    if (detectorCount > 0) noBehavioralDetectors.add(detectorCount);

    // Verify behavioral detectors are NOT in the list
    const detectorList = detectors.toLowerCase();
    check(res, {
        'no-behavioral: status < 500': (r) => r.status < 500,
        'no-behavioral: no Behavioral detector': () => !detectorList.includes('behavioral'),
        'no-behavioral: no BehavioralWaveform detector': () => !detectorList.includes('behavioralwaveform'),
        'no-behavioral: has detection headers': (r) => r.headers['X-Bot-Detection'] !== undefined,
    });
}
