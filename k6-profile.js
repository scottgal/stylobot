import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter, Rate } from 'k6/metrics';

// Custom metrics for profiling
const gatewayProcessingMs = new Trend('gateway_processing_ms', true);
const detectorCount = new Trend('detector_count');
const botRiskScore = new Trend('bot_risk_score');
const requestsWithSlowDetection = new Rate('slow_detection_rate');

const BASE = __ENV.BASE_URL || 'http://localhost:8090';

// 3 traffic profiles to exercise different code paths
const botUAs = [
    'Googlebot/2.1 (+http://www.google.com/bot.html)',
    'Mozilla/5.0 (compatible; Bingbot/2.0; +http://www.bing.com/bingbot.htm)',
    'python-requests/2.28.0',
    'curl/7.88.1',
    'Scrapy/2.8.0',
    'Go-http-client/1.1',
];

const humanUAs = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15',
    'Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0',
];

const paths = ['/', '/home', '/about', '/docs', '/contact', '/detectors', '/live-demo'];

export const options = {
    scenarios: {
        // Sustained high load to find hotspots
        sustained_load: {
            executor: 'ramping-vus',
            startVUs: 10,
            stages: [
                { duration: '15s', target: 50 },
                { duration: '30s', target: 100 },
                { duration: '15s', target: 100 },
                { duration: '10s', target: 0 },
            ],
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<500', 'p(99)<2000'],
        gateway_processing_ms: ['p(50)<50', 'p(95)<200', 'p(99)<500'],
        slow_detection_rate: ['rate<0.1'],
    },
};

export default function () {
    const isBotTraffic = Math.random() < 0.4;
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
        headers['Sec-Fetch-Dest'] = 'document';
        headers['Sec-Fetch-Mode'] = 'navigate';
        headers['Sec-Fetch-Site'] = 'none';
        headers['Sec-Fetch-User'] = '?1';
    }

    const res = http.get(`${BASE}${path}`, { headers, tags: { traffic_type: isBotTraffic ? 'bot' : 'human' } });

    // Extract processing time from response headers
    const processingHeader = res.headers['X-Bot-Processing-Ms'];
    if (processingHeader) {
        // May have multiple values (gateway + website)
        const values = processingHeader.split(',').map(v => parseFloat(v.trim()));
        const maxProcessing = Math.max(...values);
        gatewayProcessingMs.add(maxProcessing);
        requestsWithSlowDetection.add(maxProcessing > 100);
    }

    const detectorHeader = res.headers['X-Bot-Detectors'];
    if (detectorHeader) {
        const firstSet = detectorHeader.split(',');
        detectorCount.add(firstSet.length);
    }

    const riskHeader = res.headers['X-Bot-Risk-Score'];
    if (riskHeader) {
        botRiskScore.add(parseFloat(riskHeader));
    }

    check(res, {
        'status is 200': (r) => r.status === 200,
        'has processing time': (r) => r.headers['X-Bot-Processing-Ms'] !== undefined,
    });

    // Variable sleep to simulate realistic traffic
    sleep(Math.random() * 0.3);
}

export function handleSummary(data) {
    const lines = [
        '========================================',
        'PROFILING SUMMARY',
        '========================================',
        '',
    ];

    const metrics = [
        ['http_req_duration', data.metrics.http_req_duration],
        ['gateway_processing_ms', data.metrics.gateway_processing_ms],
        ['detector_count', data.metrics.detector_count],
        ['bot_risk_score', data.metrics.bot_risk_score],
    ];

    for (const [name, m] of metrics) {
        if (m && m.values) {
            lines.push(`${name}:`);
            lines.push(`  avg=${m.values.avg?.toFixed(2)}  med=${m.values.med?.toFixed(2)}  p90=${m.values['p(90)']?.toFixed(2)}  p95=${m.values['p(95)']?.toFixed(2)}  p99=${m.values['p(99)']?.toFixed(2)}  max=${m.values.max?.toFixed(2)}`);
            lines.push('');
        }
    }

    if (data.metrics.slow_detection_rate) {
        lines.push(`slow_detection_rate (>100ms): ${(data.metrics.slow_detection_rate.values.rate * 100).toFixed(1)}%`);
    }

    console.log(lines.join('\n'));
    return {};
}
