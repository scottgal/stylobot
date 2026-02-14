import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// ============================================================
// k6 Security & Performance Test for Stylobot
// Tests: PII leakage, admin auth, header injection, bot
// detection accuracy, response times, training API safety
// ============================================================

// Custom metrics
const piiLeaks = new Counter('pii_leaks');
const securityIssues = new Counter('security_issues');
const botDetected = new Rate('bot_detection_rate');
const humanFalsePositive = new Rate('human_false_positive_rate');
const trainingApiDuration = new Trend('training_api_duration');
const detectionDuration = new Trend('detection_duration_ms');

// Configuration
const GATEWAY_URL = __ENV.GATEWAY_URL || 'http://localhost:8090';
const ADMIN_SECRET = __ENV.ADMIN_SECRET || 'dHJ5uLmln6gVtFDyDpX1FgF9YSQNg8XzrKgF';

export const options = {
    scenarios: {
        // Security tests - run once per VU
        security_audit: {
            executor: 'per-vu-iterations',
            vus: 1,
            iterations: 1,
            exec: 'securityAudit',
            startTime: '0s',
        },
        // PII leak tests on training endpoints
        pii_audit: {
            executor: 'per-vu-iterations',
            vus: 1,
            iterations: 1,
            exec: 'piiAudit',
            startTime: '2s',
        },
        // Performance: bot traffic load
        bot_load: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '15s', target: 20 },
                { duration: '30s', target: 20 },
                { duration: '15s', target: 50 },
                { duration: '30s', target: 50 },
                { duration: '10s', target: 0 },
            ],
            exec: 'botTraffic',
            startTime: '5s',
        },
        // Performance: human traffic load
        human_load: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '15s', target: 10 },
                { duration: '30s', target: 10 },
                { duration: '15s', target: 30 },
                { duration: '30s', target: 30 },
                { duration: '10s', target: 0 },
            ],
            exec: 'humanTraffic',
            startTime: '5s',
        },
        // Training API perf under load
        training_api_load: {
            executor: 'constant-vus',
            vus: 5,
            duration: '60s',
            exec: 'trainingApiLoad',
            startTime: '10s',
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<500', 'p(99)<2000'],
        http_req_failed: ['rate<0.05'],
        'pii_leaks': ['count==0'],
        'security_issues': ['count==0'],
        'bot_detection_rate': ['rate>0.5'],
        'human_false_positive_rate': ['rate<0.15'],
        'detection_duration_ms': ['p(95)<100'],
        'training_api_duration': ['p(95)<1000'],
    },
};

// ============================================================
// PII patterns to scan for in responses
// ============================================================
const PII_PATTERNS = [
    { name: 'ipv4', regex: /\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b/ },
    { name: 'email', regex: /[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/ },
    { name: 'ua.raw_key', regex: /"ua\.raw"/ },
    { name: 'ip.address_key', regex: /"ip\.address"/ },
    { name: 'requestId', regex: /"requestId"/ },
    { name: 'query_string_in_path', regex: /"path"\s*:\s*"[^"]*\?[^"]*"/ },
    { name: 'guid_in_path', regex: /"path"\s*:\s*"[^"]*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[^"]*"/ },
];

function scanForPii(body, endpoint) {
    for (const pattern of PII_PATTERNS) {
        if (pattern.regex.test(body)) {
            console.error(`PII LEAK [${pattern.name}] in ${endpoint}`);
            piiLeaks.add(1);
        }
    }
}

// ============================================================
// Security Audit Scenario
// ============================================================
export function securityAudit() {
    group('Admin endpoint auth bypass', () => {
        // Try accessing admin without secret
        const noAuth = http.get(`${GATEWAY_URL}/admin/health`);
        check(noAuth, {
            'admin/health without auth returns 401/403': (r) => r.status === 401 || r.status === 403,
        }) || securityIssues.add(1);

        const configNoAuth = http.get(`${GATEWAY_URL}/admin/config/effective`);
        check(configNoAuth, {
            'admin/config without auth returns 401/403': (r) => r.status === 401 || r.status === 403,
        }) || securityIssues.add(1);

        const metricsNoAuth = http.get(`${GATEWAY_URL}/admin/metrics`);
        check(metricsNoAuth, {
            'admin/metrics without auth returns 401/403': (r) => r.status === 401 || r.status === 403,
        }) || securityIssues.add(1);

        // Try with wrong secret
        const wrongSecret = http.get(`${GATEWAY_URL}/admin/health`, {
            headers: { 'X-Admin-Secret': 'wrong-secret-value' },
        });
        check(wrongSecret, {
            'admin with wrong secret returns 401/403': (r) => r.status === 401 || r.status === 403,
        }) || securityIssues.add(1);

        // Try with empty secret
        const emptySecret = http.get(`${GATEWAY_URL}/admin/config/effective`, {
            headers: { 'X-Admin-Secret': '' },
        });
        check(emptySecret, {
            'admin with empty secret returns 401/403': (r) => r.status === 401 || r.status === 403,
        }) || securityIssues.add(1);
    });

    group('Path traversal on admin/fs', () => {
        const traversalPaths = [
            '/admin/fs/..%2F..%2Fetc%2Fpasswd',
            '/admin/fs/..\\..\\Windows\\System32\\config\\SAM',
            '/admin/fs/....//....//etc/passwd',
            '/admin/fs/%2e%2e%2f%2e%2e%2f',
            '/admin/fs/config/../../../etc/shadow',
        ];

        for (const path of traversalPaths) {
            const res = http.get(`${GATEWAY_URL}${path}`, {
                headers: { 'X-Admin-Secret': ADMIN_SECRET },
            });
            const passed = check(res, {
                [`path traversal blocked: ${path.substring(0, 40)}`]: (r) =>
                    r.status === 400 || r.status === 403 || r.status === 404,
            });
            if (!passed) {
                console.error(`PATH TRAVERSAL may have succeeded: ${path} -> ${res.status}`);
                securityIssues.add(1);
            }
        }
    });

    group('Header injection attacks', () => {
        // CRLF injection in headers
        const crlfRes = http.get(`${GATEWAY_URL}/`, {
            headers: {
                'User-Agent': "Mozilla/5.0\r\nX-Injected: true",
                'Accept': 'text/html',
            },
        });
        check(crlfRes, {
            'CRLF injection handled': (r) => r.status !== 500,
        }) || securityIssues.add(1);

        // Oversized User-Agent (potential buffer overflow / DoS)
        const bigUA = 'A'.repeat(10000);
        const bigUARes = http.get(`${GATEWAY_URL}/`, {
            headers: { 'User-Agent': bigUA },
        });
        check(bigUARes, {
            'oversized UA handled gracefully': (r) => r.status < 500,
        }) || securityIssues.add(1);

        // Null byte injection
        const nullRes = http.get(`${GATEWAY_URL}/`, {
            headers: { 'User-Agent': "Mozilla/5.0\x00<script>alert(1)</script>" },
        });
        check(nullRes, {
            'null byte injection handled': (r) => r.status < 500,
        }) || securityIssues.add(1);
    });

    group('XSS in detection headers', () => {
        const xssPayloads = [
            '<script>alert(1)</script>',
            '"><img src=x onerror=alert(1)>',
            "javascript:alert(document.cookie)",
        ];

        for (const payload of xssPayloads) {
            const res = http.get(`${GATEWAY_URL}/`, {
                headers: { 'User-Agent': payload },
            });
            // Check that response headers don't reflect raw XSS
            const botType = res.headers['X-Bot-Type'] || '';
            const botName = res.headers['X-Bot-Name'] || '';
            const reasons = res.headers['X-Bot-Detection-Reasons'] || '';
            const allHeaders = botType + botName + reasons;
            check(res, {
                [`XSS not reflected in headers`]: () => !allHeaders.includes('<script>') && !allHeaders.includes('onerror'),
            }) || securityIssues.add(1);
        }
    });

    group('Training endpoints access control', () => {
        // Training endpoints should be accessible (they're designed to be public)
        // but should NOT leak PII
        const sigRes = http.get(`${GATEWAY_URL}/bot-detection/training/signatures`);
        check(sigRes, {
            'training/signatures accessible': (r) => r.status === 200,
            'X-PII-Level header present': (r) => r.headers['X-Pii-Level'] === 'none',
            'X-Data-Classification header present': (r) => r.headers['X-Data-Classification'] === 'public-training-data',
        });

        const exportRes = http.get(`${GATEWAY_URL}/bot-detection/training/export`);
        check(exportRes, {
            'training/export accessible': (r) => r.status === 200,
        });
    });

    group('HTTP method abuse', () => {
        // DELETE/PUT on training endpoints
        const delRes = http.del(`${GATEWAY_URL}/bot-detection/training/signatures`);
        check(delRes, {
            'DELETE on training returns 405': (r) => r.status === 405 || r.status === 404,
        });

        const putRes = http.put(`${GATEWAY_URL}/bot-detection/training/signatures`, '{}');
        check(putRes, {
            'PUT on training returns 405': (r) => r.status === 405 || r.status === 404,
        });
    });

    group('Response header security', () => {
        const res = http.get(`${GATEWAY_URL}/`);
        check(res, {
            'no server version leaked': (r) => {
                const server = r.headers['Server'] || '';
                return !server.includes('Kestrel') && !server.includes('ASP.NET');
            },
            'X-Content-Type-Options present': (r) => r.headers['X-Content-Type-Options'] !== undefined,
        });
    });
}

// ============================================================
// PII Audit Scenario
// ============================================================
export function piiAudit() {
    group('Training signatures PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/signatures`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/signatures');
        }
    });

    group('Training export PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/export`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/export');
        }
    });

    group('Training clusters PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/clusters`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/clusters');
        }
    });

    group('Training countries PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/countries`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/countries');
        }
    });

    group('Training families PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/families`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/families');
        }
    });

    group('Training convergence PII scan', () => {
        const res = http.get(`${GATEWAY_URL}/bot-detection/training/convergence/stats`);
        if (res.status === 200) {
            scanForPii(res.body, '/training/convergence/stats');
        }
    });

    group('Signature detail PII scan', () => {
        // First get a signature from the list
        const listRes = http.get(`${GATEWAY_URL}/bot-detection/training/signatures`);
        if (listRes.status === 200) {
            try {
                const sigs = JSON.parse(listRes.body);
                if (sigs.length > 0) {
                    const sig = sigs[0].signature;
                    const detailRes = http.get(`${GATEWAY_URL}/bot-detection/training/signatures/${sig}`);
                    if (detailRes.status === 200) {
                        scanForPii(detailRes.body, `/training/signatures/${sig}`);

                        // Additional checks on detail response
                        const detail = JSON.parse(detailRes.body);
                        if (detail.requests) {
                            for (const req of detail.requests) {
                                // Verify no requestId
                                check(null, {
                                    'no requestId in detail': () => req.requestId === undefined,
                                }) || piiLeaks.add(1);

                                // Verify path is generalized (no query strings)
                                if (req.path && req.path.includes('?')) {
                                    console.error(`PII: query string in path: ${req.path}`);
                                    piiLeaks.add(1);
                                }

                                // Check UA signals for human visitors
                                if (detail.averageBotProbability < 0.5 && req.signals) {
                                    const hasUaClassification = req.signals['ua.is_bot'] !== undefined ||
                                        req.signals['ua.bot_type'] !== undefined ||
                                        req.signals['user_agent.os'] !== undefined;
                                    check(null, {
                                        'no UA classification for humans': () => !hasUaClassification,
                                    }) || piiLeaks.add(1);
                                }
                            }
                        }
                    }
                }
            } catch (e) {
                // Empty response is fine
            }
        }
    });
}

// ============================================================
// Bot Traffic Load Scenario
// ============================================================
const botUserAgents = [
    'python-requests/2.31.0',
    'Scrapy/2.11.0',
    'curl/8.4.0',
    'Go-http-client/2.0',
    'libwww-perl/6.67',
    'axios/1.6.2',
    'okhttp/4.12.0',
    'python-urllib/3.11',
    'Googlebot/2.1 (+http://www.google.com/bot.html)',
    'Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)',
    'facebookexternalhit/1.1',
    'Twitterbot/1.0',
];

const botPaths = [
    '/',
    '/robots.txt',
    '/sitemap.xml',
    '/api/v1/items?limit=100',
    '/search?q=test',
    '/admin/dashboard',
    '/products',
    '/.env',
    '/wp-admin',
    '/wp-login.php',
];

export function botTraffic() {
    const ua = botUserAgents[Math.floor(Math.random() * botUserAgents.length)];
    const path = botPaths[Math.floor(Math.random() * botPaths.length)];

    const res = http.get(`${GATEWAY_URL}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': '*/*',
        },
        tags: { scenario_type: 'bot' },
    });

    const detected = res.headers['X-Bot-Detected'] === 'true' ||
        res.headers['X-Bot-Detection'] === 'True' ||
        res.status === 403;
    botDetected.add(detected ? 1 : 0);

    // Track processing time from header
    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
    if (procTime > 0) {
        detectionDuration.add(procTime);
    }

    // Bot-like rapid requests
    sleep(Math.random() * 0.1);
}

// ============================================================
// Human Traffic Load Scenario
// ============================================================
const humanUserAgents = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1',
    'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
];

const humanPaths = [
    '/',
    '/about',
    '/products',
    '/contact',
    '/blog',
    '/search?q=laptop',
    '/products/electronics',
];

export function humanTraffic() {
    const ua = humanUserAgents[Math.floor(Math.random() * humanUserAgents.length)];
    const path = humanPaths[Math.floor(Math.random() * humanPaths.length)];

    const res = http.get(`${GATEWAY_URL}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br',
            'Connection': 'keep-alive',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-User': '?1',
            'Upgrade-Insecure-Requests': '1',
        },
        tags: { scenario_type: 'human' },
    });

    const detectedAsBot = res.headers['X-Bot-Detected'] === 'true' ||
        res.headers['X-Bot-Detection'] === 'True' ||
        res.status === 403;
    humanFalsePositive.add(detectedAsBot ? 1 : 0);

    // Track processing time
    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || '0');
    if (procTime > 0) {
        detectionDuration.add(procTime);
    }

    // Human-like delays
    sleep(Math.random() * 3 + 1);
}

// ============================================================
// Training API Load Scenario
// ============================================================
export function trainingApiLoad() {
    const endpoints = [
        '/bot-detection/training/signatures',
        '/bot-detection/training/clusters',
        '/bot-detection/training/countries',
        '/bot-detection/training/families',
        '/bot-detection/training/convergence/stats',
        '/bot-detection/training/export',
    ];

    const endpoint = endpoints[Math.floor(Math.random() * endpoints.length)];
    const start = Date.now();
    const res = http.get(`${GATEWAY_URL}${endpoint}`, {
        tags: { endpoint: endpoint },
    });
    trainingApiDuration.add(Date.now() - start);

    check(res, {
        'training API returns 200': (r) => r.status === 200,
        'training API has PII header': (r) => r.headers['X-Pii-Level'] === 'none',
    });

    sleep(0.5);
}

// ============================================================
// Setup / Teardown
// ============================================================
export function setup() {
    console.log('='.repeat(72));
    console.log('Stylobot Security & Performance Test Suite');
    console.log('='.repeat(72));
    console.log(`Gateway: ${GATEWAY_URL}`);

    // Verify gateway is reachable
    const healthRes = http.get(`${GATEWAY_URL}/admin/alive`, {
        headers: { 'X-Admin-Secret': ADMIN_SECRET },
    });
    if (healthRes.status !== 200) {
        console.warn(`Gateway health check returned ${healthRes.status} - tests may fail`);
    } else {
        console.log('Gateway is healthy');
    }

    console.log('='.repeat(72));
    return {};
}

export function teardown(data) {
    console.log('');
    console.log('='.repeat(72));
    console.log('Test suite completed');
    console.log('='.repeat(72));
}
