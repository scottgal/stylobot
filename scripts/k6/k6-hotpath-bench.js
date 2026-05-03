import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

// Measures pure detection overhead, not LLM or downstream app.
// Run against the gateway (5080) which has the full 49-detector pipeline.

const detectionMs = new Trend('detection_ms', true);

const BASE = __ENV.BASE_URL || 'http://localhost:5080';

const UAs = [
    // Fast-path human
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15',
    // Fast-path bot (hard blocks early)
    'python-requests/2.31.0',
    'curl/8.6.0',
    'Scrapy/2.11.0 (+https://scrapy.org)',
    'Go-http-client/1.1',
    // AI crawlers
    'Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.2; +https://openai.com/gptbot',
    'ClaudeBot/1.0; +https://www.anthropic.com/claude-bot',
    // Fake human (triggers inconsistency detectors)
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
];

const paths = ['/', '/home', '/about', '/docs', '/api/summary', '/not-found-path'];

export const options = {
    scenarios: {
        ramp: {
            executor: 'ramping-vus',
            startVUs: 5,
            stages: [
                { duration: '15s', target: 50 },
                { duration: '45s', target: 100 },
                { duration: '30s', target: 200 },
                { duration: '10s', target: 0 },
            ],
        },
    },
    thresholds: {
        http_req_duration:   ['p(95)<500', 'p(99)<1000'],
        detection_ms:        ['p(50)<10',  'p(95)<100', 'p(99)<300'],
    },
};

export default function () {
    const ua  = UAs[Math.floor(Math.random() * UAs.length)];
    const path = paths[Math.floor(Math.random() * paths.length)];

    const res = http.get(`${BASE}${path}`, {
        headers: { 'User-Agent': ua },
        tags: { path },
    });

    // Record internal detection time from header
    const ms = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    if (ms > 0) detectionMs.add(ms);

    check(res, {
        'not 5xx': (r) => r.status < 500,
        'detection ran': (r) => r.headers['X-Bot-Processing-Ms'] !== undefined,
    });
}
