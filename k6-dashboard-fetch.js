/**
 * k6 Dashboard-Fetch Test
 *
 * Reproduces the same-origin fetch misclassification scenario where
 * browser fetch() calls to dashboard API endpoints were incorrectly
 * flagged as bot traffic (missing Sec-Fetch-* header awareness).
 *
 * Three scenarios:
 *   1. dashboard_page_load  — Full browser navigation headers
 *   2. same_origin_fetch    — Browser fetch() to dashboard API (the misclassified scenario)
 *   3. signalr_negotiate    — SignalR negotiate POST with same-origin headers
 *
 * Usage:
 *   k6 run k6-dashboard-fetch.js --env BASE_URL=http://localhost:5090
 *   k6 run k6-dashboard-fetch.js --env BASE_URL=http://localhost:8090
 */

import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { Rate, Counter } from 'k6/metrics';

// Custom metrics
const falsePositiveRate = new Rate('false_positives');
const falsePositiveCount = new Counter('false_positive_count');
const trueNegativeCount = new Counter('true_negative_count');

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5090';
const DASHBOARD_PATH = __ENV.DASHBOARD_PATH || '/_stylobot';

export const options = {
    scenarios: {
        dashboard_page_load: {
            executor: 'constant-vus',
            vus: 2,
            duration: '30s',
            exec: 'dashboardPageLoad',
            tags: { scenario: 'page_load' },
        },
        same_origin_fetch: {
            executor: 'constant-vus',
            vus: 5,
            duration: '30s',
            exec: 'sameOriginFetch',
            tags: { scenario: 'same_origin_fetch' },
            startTime: '5s', // stagger start so page loads establish sessions first
        },
        signalr_negotiate: {
            executor: 'constant-vus',
            vus: 2,
            duration: '30s',
            exec: 'signalrNegotiate',
            tags: { scenario: 'signalr_negotiate' },
            startTime: '5s',
        },
    },
    thresholds: {
        // Core assertion: <1% false positive rate on legitimate browser requests
        'false_positives': ['rate<0.01'],
        'http_req_duration': ['p(95)<2000'],
        'http_req_failed': ['rate<0.05'],
    },
};

/**
 * Common Chrome 134 browser headers for realistic simulation.
 */
const CHROME_COMMON_HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36',
    'Accept-Language': 'en-US,en;q=0.9',
    'Accept-Encoding': 'gzip, deflate, br',
    'Connection': 'keep-alive',
    'Cache-Control': 'no-cache',
    'Pragma': 'no-cache',
};

/**
 * Checks the X-Bot-Risk-Score response header (prefix is configurable, default "X-Bot-").
 * A risk score >= 0.7 means the request was classified as bot (false positive for browser requests).
 * Also checks if the dashboard returned 403 (hard-blocked by middleware).
 */
function checkNotFlaggedAsBot(response, requestName) {
    const riskScore = response.headers['X-Bot-Risk-Score'] || response.headers['x-bot-risk-score'];
    const riskBand = response.headers['X-Bot-Risk-Band'] || response.headers['x-bot-risk-band'];
    const score = riskScore ? parseFloat(riskScore) : 0;
    const isFlaggedAsBot = score >= 0.7 || response.status === 403;

    if (isFlaggedAsBot) {
        falsePositiveRate.add(1);
        falsePositiveCount.add(1);
        console.warn(`FALSE POSITIVE: ${requestName} flagged as bot (risk=${riskScore}, band=${riskBand}, status=${response.status})`);
    } else {
        falsePositiveRate.add(0);
        trueNegativeCount.add(1);
    }

    return !isFlaggedAsBot;
}

/**
 * Scenario 1: Dashboard page load — full browser navigation headers.
 * Simulates a user navigating directly to the dashboard URL.
 */
export function dashboardPageLoad() {
    group('Dashboard Page Load', () => {
        const res = http.get(`${BASE_URL}${DASHBOARD_PATH}`, {
            headers: Object.assign({}, CHROME_COMMON_HEADERS, {
                'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8',
                'Sec-Fetch-Site': 'none',
                'Sec-Fetch-Mode': 'navigate',
                'Sec-Fetch-Dest': 'document',
                'Sec-Fetch-User': '?1',
                'Upgrade-Insecure-Requests': '1',
            }),
            tags: { name: 'page_load_dashboard' },
        });

        check(res, {
            'page load status 200': (r) => r.status === 200,
            'page load has HTML': (r) => r.headers['Content-Type']?.includes('text/html'),
        });
        checkNotFlaggedAsBot(res, 'dashboard_page_load');

        sleep(0.5);
    });
}

/**
 * Scenario 2: Same-origin fetch — browser fetch() to dashboard API endpoints.
 * THIS IS THE SCENARIO THAT WAS MISCLASSIFIED as bot traffic.
 *
 * When a browser makes fetch() calls from the dashboard page to its own API,
 * it sends Sec-Fetch-Site: same-origin, Sec-Fetch-Mode: cors, Sec-Fetch-Dest: empty.
 * These are legitimate browser requests and must NOT be flagged as bots.
 */
export function sameOriginFetch() {
    group('Same-Origin Fetch', () => {
        // Common same-origin fetch headers (what browsers actually send)
        const fetchHeaders = Object.assign({}, CHROME_COMMON_HEADERS, {
            'Accept': 'application/json',
            'Sec-Fetch-Site': 'same-origin',
            'Sec-Fetch-Mode': 'cors',
            'Sec-Fetch-Dest': 'empty',
        });

        // Dashboard summary API
        const summaryRes = http.get(`${BASE_URL}${DASHBOARD_PATH}/api/summary`, {
            headers: fetchHeaders,
            tags: { name: 'fetch_summary' },
        });
        check(summaryRes, {
            'summary status ok': (r) => r.status === 200 || r.status === 403, // 403 if bot-blocked, which is the failure we're testing
        });
        checkNotFlaggedAsBot(summaryRes, 'fetch_summary');

        sleep(0.3);

        // Dashboard detections API
        const detectionsRes = http.get(`${BASE_URL}${DASHBOARD_PATH}/api/detections`, {
            headers: fetchHeaders,
            tags: { name: 'fetch_detections' },
        });
        check(detectionsRes, {
            'detections status ok': (r) => r.status === 200 || r.status === 403,
        });
        checkNotFlaggedAsBot(detectionsRes, 'fetch_detections');

        sleep(0.3);

        // Dashboard signatures API
        const signaturesRes = http.get(`${BASE_URL}${DASHBOARD_PATH}/api/signatures`, {
            headers: fetchHeaders,
            tags: { name: 'fetch_signatures' },
        });
        check(signaturesRes, {
            'signatures status ok': (r) => r.status === 200 || r.status === 403,
        });
        checkNotFlaggedAsBot(signaturesRes, 'fetch_signatures');

        sleep(0.3);

        // Dashboard timeseries API
        const timeseriesRes = http.get(`${BASE_URL}${DASHBOARD_PATH}/api/timeseries`, {
            headers: fetchHeaders,
            tags: { name: 'fetch_timeseries' },
        });
        check(timeseriesRes, {
            'timeseries status ok': (r) => r.status === 200 || r.status === 403,
        });
        checkNotFlaggedAsBot(timeseriesRes, 'fetch_timeseries');

        sleep(0.5);

        // Dashboard countries API
        const countriesRes = http.get(`${BASE_URL}${DASHBOARD_PATH}/api/countries`, {
            headers: fetchHeaders,
            tags: { name: 'fetch_countries' },
        });
        check(countriesRes, {
            'countries status ok': (r) => r.status === 200 || r.status === 403,
        });
        checkNotFlaggedAsBot(countriesRes, 'fetch_countries');

        sleep(0.5);
    });
}

/**
 * Scenario 3: SignalR negotiate — POST with same-origin headers.
 * SignalR's negotiate step uses a POST request with same-origin fetch metadata.
 */
export function signalrNegotiate() {
    group('SignalR Negotiate', () => {
        const negotiateRes = http.post(
            `${BASE_URL}${DASHBOARD_PATH}/hub/negotiate?negotiateVersion=1`,
            null,
            {
                headers: Object.assign({}, CHROME_COMMON_HEADERS, {
                    'Accept': '*/*',
                    'Content-Type': 'text/plain;charset=UTF-8',
                    'Sec-Fetch-Site': 'same-origin',
                    'Sec-Fetch-Mode': 'cors',
                    'Sec-Fetch-Dest': 'empty',
                    'X-Requested-With': 'XMLHttpRequest',
                }),
                tags: { name: 'signalr_negotiate' },
            }
        );

        // SignalR negotiate may return various status codes depending on config
        check(negotiateRes, {
            'negotiate responds': (r) => r.status < 500,
        });
        checkNotFlaggedAsBot(negotiateRes, 'signalr_negotiate');

        sleep(1);
    });
}
