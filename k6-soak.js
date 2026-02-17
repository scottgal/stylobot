// k6 Soak Test for StyloBot Demo Stack
// Tests: mixed traffic (human browsers, bots, attack payloads)
// Run: k6 run k6-soak.js
//
// Profiles against the Caddy frontend on localhost (port 80)
// or set BASE_URL env var to target a different endpoint.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Custom metrics
const botDetected = new Rate('bot_detected');
const attackBlocked = new Rate('attack_blocked');
const detectionTime = new Trend('detection_time_ms', true);
const errorRate = new Rate('errors');
const dashboardOk = new Rate('dashboard_ok');

const BASE = __ENV.BASE_URL || 'http://localhost';

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
        // Dashboard API polling (verify widgets populate)
        dashboard_polling: {
            executor: 'constant-arrival-rate',
            rate: 1,
            timeUnit: '1s',
            duration: '5m',
            preAllocatedVUs: 3,
            maxVUs: 5,
            exec: 'dashboardPolling',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<2000'],
        errors: ['rate<0.05'],
        dashboard_ok: ['rate>0.95'],
    },
};

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

    const isBot = res.headers['X-Bot-Detection'] === 'True' ||
                  res.headers['X-Bot-Risk-Score'] > '0.5';
    botDetected.add(isBot ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);

    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
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

    const isBot = res.headers['X-Bot-Detection'] === 'True' ||
                  res.headers['X-Bot-Risk-Score'] > '0.5';
    botDetected.add(isBot ? 1 : 0);
    errorRate.add(res.status >= 500 ? 1 : 0);

    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
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

// ===== Dashboard API Polling =====

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
