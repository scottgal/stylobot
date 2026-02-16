import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter, Rate } from 'k6/metrics';

// Custom metrics
const processingMs = new Trend('bot_processing_ms', true);
const detectorCount = new Trend('detector_count');
const riskScore = new Trend('bot_risk_score');
const slowDetections = new Rate('slow_detection_rate');
const earlyExits = new Counter('early_exit_count');
const botDetections = new Counter('bot_detections');
const humanDetections = new Counter('human_detections');

const BASE = __ENV.BASE_URL || 'http://localhost:8090';

const botUAs = [
    'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
    'Mozilla/5.0 (compatible; Bingbot/2.0; +http://www.bing.com/bingbot.htm)',
    'python-requests/2.28.0',
    'curl/7.88.1',
    'Scrapy/2.8.0',
    'Go-http-client/1.1',
    'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)',
    'Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)',
    'CCBot/2.0 (https://commoncrawl.org/faq/)',
    'Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)',
];

const humanUAs = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15',
    'Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0',
];

const paths = ['/', '/about', '/docs', '/contact', '/pricing', '/features'];

export const options = {
    scenarios: {
        // Soak: sustained load over 2 minutes
        soak: {
            executor: 'ramping-vus',
            startVUs: 5,
            stages: [
                { duration: '10s', target: 50 },   // ramp up
                { duration: '90s', target: 50 },   // sustained
                { duration: '10s', target: 100 },  // spike
                { duration: '10s', target: 0 },    // ramp down
            ],
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<500', 'p(99)<2000'],
        bot_processing_ms: ['p(50)<50', 'p(95)<200'],
        slow_detection_rate: ['rate<0.05'],
    },
};

export default function () {
    const isBotTraffic = Math.random() < 0.35;
    const ua = isBotTraffic
        ? botUAs[Math.floor(Math.random() * botUAs.length)]
        : humanUAs[Math.floor(Math.random() * humanUAs.length)];
    const path = paths[Math.floor(Math.random() * paths.length)];

    const headers = { 'User-Agent': ua };
    if (!isBotTraffic) {
        headers['Accept'] = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8';
        headers['Accept-Language'] = 'en-US,en;q=0.9';
        headers['Accept-Encoding'] = 'gzip, deflate, br';
        headers['Connection'] = 'keep-alive';
        headers['Referer'] = `${BASE}/`;
    }

    const res = http.get(`${BASE}${path}`, {
        headers,
        tags: { traffic_type: isBotTraffic ? 'bot' : 'human' },
    });

    // Extract metrics from response headers
    const procHeader = res.headers['X-Bot-Processing-Ms'];
    if (procHeader) {
        const values = procHeader.split(',').map(v => parseFloat(v.trim()));
        const maxProc = Math.max(...values);
        processingMs.add(maxProc);
        slowDetections.add(maxProc > 100);
    }

    const detHeader = res.headers['X-Bot-Detectors'];
    if (detHeader) {
        detectorCount.add(detHeader.split(',').length);
    }

    const riskHeader = res.headers['X-Bot-Risk-Score'];
    if (riskHeader) {
        const score = parseFloat(riskHeader);
        riskScore.add(score);
        if (score > 0.5) botDetections.add(1);
        else humanDetections.add(1);
    }

    const earlyHeader = res.headers['X-Bot-Early-Exit'];
    if (earlyHeader === 'true' || earlyHeader === 'True') {
        earlyExits.add(1);
    }

    check(res, {
        'status 200 or 403': (r) => r.status === 200 || r.status === 403,
        'has processing time': (r) => r.headers['X-Bot-Processing-Ms'] !== undefined,
    });

    sleep(Math.random() * 0.2);
}

export function handleSummary(data) {
    const lines = [
        '',
        '╔══════════════════════════════════════════════════╗',
        '║           SOAK TEST RESULTS                     ║',
        '╚══════════════════════════════════════════════════╝',
        '',
    ];

    const metrics = [
        ['HTTP Request Duration', data.metrics.http_req_duration],
        ['Bot Processing Time', data.metrics.bot_processing_ms],
        ['Detector Count', data.metrics.detector_count],
        ['Risk Score', data.metrics.bot_risk_score],
    ];

    for (const [name, m] of metrics) {
        if (m && m.values) {
            lines.push(`${name}:`);
            lines.push(`  avg=${m.values.avg?.toFixed(2)}  med=${m.values.med?.toFixed(2)}  p90=${m.values['p(90)']?.toFixed(2)}  p95=${m.values['p(95)']?.toFixed(2)}  p99=${m.values['p(99)']?.toFixed(2)}  max=${m.values.max?.toFixed(2)}`);
            lines.push('');
        }
    }

    if (data.metrics.http_reqs) {
        lines.push(`Total requests: ${data.metrics.http_reqs.values.count}`);
        lines.push(`Requests/sec: ${data.metrics.http_reqs.values.rate?.toFixed(1)}`);
    }
    if (data.metrics.bot_detections) lines.push(`Bot detections: ${data.metrics.bot_detections.values.count}`);
    if (data.metrics.human_detections) lines.push(`Human detections: ${data.metrics.human_detections.values.count}`);
    if (data.metrics.early_exit_count) lines.push(`Early exits: ${data.metrics.early_exit_count.values.count}`);
    if (data.metrics.slow_detection_rate) lines.push(`Slow detection rate (>100ms): ${(data.metrics.slow_detection_rate.values.rate * 100).toFixed(1)}%`);

    console.log(lines.join('\n'));
    return {};
}
