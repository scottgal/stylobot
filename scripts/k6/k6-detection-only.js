// Focused detection latency test — measures StyloBot overhead only,
// not backend app time. Uses consistent UAs to exercise specific paths.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const detectionMs = new Trend('detection_ms', true);
const blockedMs   = new Trend('blocked_req_ms', true);
const allowedMs   = new Trend('allowed_req_ms', true);

const BASE = __ENV.BASE_URL || 'http://localhost:5080';

// Definitely-bot UAs (should get 403 fast via fast path)
const botUAs = [
    'python-requests/2.31.0',
    'curl/8.6.0',
    'Scrapy/2.11.0',
    'Go-http-client/1.1',
    'Mozilla/5.0 AppleWebKit/537.36; compatible; GPTBot/1.2; +https://openai.com/gptbot',
];

// Human UAs (should pass through)
const humanUAs = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15',
];

export const options = {
    scenarios: {
        // Moderate concurrency to avoid queuing effects dominating latency
        steady: {
            executor: 'constant-vus',
            vus: 30,
            duration: '45s',
        },
    },
    thresholds: {
        detection_ms: ['p(50)<5', 'p(95)<50', 'p(99)<150'],
        blocked_req_ms: ['p(95)<200', 'p(99)<500'],
    },
};

export default function () {
    // Alternate bot/human to exercise both paths
    const isBot = Math.random() < 0.7;
    const uas   = isBot ? botUAs : humanUAs;
    const ua    = uas[Math.floor(Math.random() * uas.length)];

    const start = Date.now();
    const res   = http.get(`${BASE}/`, { headers: { 'User-Agent': ua } });
    const total = Date.now() - start;

    const ms = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    if (ms > 0) detectionMs.add(ms);

    if (res.status === 403) blockedMs.add(total);
    else                    allowedMs.add(total);

    check(res, { 'not 5xx': (r) => r.status < 500 });
}
