import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom metrics for evidence tracking
const totalRequests = new Counter('total_requests');
const botScenarios = new Counter('bot_scenarios');
const humanScenarios = new Counter('human_scenarios');
const detectionRate = new Rate('detection_rate');
const scenarioDuration = new Trend('scenario_duration');
const intervalTrend = new Trend('interval_ms');
const sensitivePaths = new Rate('sensitive_path_rate');
const burstRate = new Rate('burst_detected');

// Load test configuration - multiple VUs provide natural interleaving
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs (interleaved requests)
        { duration: '2m', target: 10 },   // Stay at 10 VUs
        { duration: '30s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<1000'],
        http_req_failed: ['rate<0.1'],
        'detection_rate': ['rate>0.3'],
    },
};

// Target URL (TestSite runs on 7777)
const TARGET_URL = __ENV.TARGET_URL || 'http://localhost:7777';

// Helper: Random value between min and max
function randomBetween(min, max) {
    return min + Math.random() * (max - min);
}

// Helper: Build headers based on clientProfile
function buildHeaders(baseHeaders, clientProfile) {
    let headers = {};

    if (clientProfile.headerCompleteness === 'minimal') {
        // Only User-Agent
        headers = {
            'User-Agent': clientProfile.userAgent
        };
    } else if (clientProfile.headerCompleteness === 'partial') {
        // Some headers
        headers = {
            'User-Agent': clientProfile.userAgent,
            'Accept': 'text/html,application/xhtml+xml',
            'Connection': 'keep-alive'
        };
    } else if (clientProfile.headerCompleteness === 'full') {
        // Full browser headers
        headers = {
            'User-Agent': clientProfile.userAgent,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br',
            'Connection': 'keep-alive',
            'Upgrade-Insecure-Requests': '1',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-User': '?1'
        };

        if (clientProfile.clientHintsPresent) {
            headers['Sec-CH-UA'] = '"Chromium";v="120", "Not_A Brand";v="8"';
            headers['Sec-CH-UA-Mobile'] = '?0';
            headers['Sec-CH-UA-Platform'] = '"Windows"';
        }
    }

    // Merge with request-specific headers (request headers override)
    return { ...headers, ...baseHeaders };
}


// Embedded BDF v2 signatures
const signatures = [
  {
    scenarioName: 'test-aggressive-scraper',
    scenario: 'Aggressive web scraper hitting multiple API endpoints rapidly',
    confidence: 0.92,
    isBot: true,
    clientProfile: {
      userAgent: 'python-requests/2.31.0',
      cookieMode: 'none',
      headerCompleteness: 'minimal',
      clientHintsPresent: false,
      robotsConsulted: false
    },
    timingProfile: {
      burstRequests: 10,
      delayAfterMs: { min: 20, max: 150 },
      pauseAfterBurstMs: { min: 500, max: 2000 }
    },
    requests: [
      {
        method: 'GET',
        path: '/api/users',
        headers: {'User-Agent': 'python-requests/2.31.0'},
        expectedStatusAny: [200, 403, 429],
        expectedOutcome: 'data_exfil',
        successCondition: 'any 2xx with payload > 1KB'
      },
      {
        method: 'GET',
        path: '/api/products?limit=100',
        headers: {'User-Agent': 'python-requests/2.31.0'},
        expectedStatusAny: [200, 403, 429],
        expectedOutcome: 'data_exfil',
        successCondition: 'any 2xx with payload > 10KB'
      },
      {
        method: 'HEAD',
        path: '/admin/dashboard',
        headers: {'User-Agent': 'python-requests/2.31.0'},
        expectedStatusAny: [200, 403, 404],
        expectedOutcome: 'probing',
        successCondition: 'any response'
      },
      {
        method: 'GET',
        path: '/admin/dashboard',
        headers: {'User-Agent': 'python-requests/2.31.0'},
        expectedStatusAny: [200, 403, 404],
        expectedOutcome: 'data_exfil',
        successCondition: 'any 2xx'
      },
    ],
    labels: ['Scraper', 'RobotsIgnore', 'SensitiveProbing'],
    evidence: [
      { signal: 'interval_ms_p95', op: '<', value: 200, weight: 0.35 },
      { signal: 'sensitive_path_rate', op: '>', value: 0.5, weight: 0.30 },
    ]
  },
];


// Main test function - each VU picks random scenario and replays with burst/jitter
// Multiple VUs running concurrently provide natural request interleaving
export default function() {
    const scenarioStart = Date.now();
    let lastRequestTime = Date.now();

    // Pick a random signature
    const sig = signatures[Math.floor(Math.random() * signatures.length)];

    // Track bot vs human scenarios
    if (sig.isBot) {
        botScenarios.add(1);
    } else {
        humanScenarios.add(1);
    }

    console.log(`[VU ${__VU}] Playing: ${sig.scenarioName} (confidence: ${sig.confidence})`);

    // Setup cookie jar based on cookieMode
    let jar = null;
    if (sig.clientProfile.cookieMode === 'sticky') {
        jar = http.cookieJar();
    }

    // Robots.txt consultation
    if (sig.clientProfile.robotsConsulted) {
        http.get(`${TARGET_URL}/robots.txt`);
    }

    let detectedAsBot = false;
    let requestCount = 0;

    // Replay all requests with burst/jitter timing
    for (let i = 0; i < sig.requests.length; i++) {
        const req = sig.requests[i];
        const url = `${TARGET_URL}${req.path}`;

        // Build headers based on client profile
        const headers = buildHeaders(req.headers || {}, sig.clientProfile);

        // Prepare request params
        const params = {
            headers: headers,
            tags: {
                scenario: sig.scenarioName,
                scenario_type: sig.isBot ? 'bot' : 'human',
                request_index: i,
                expected_confidence: sig.confidence
            }
        };

        // Cookie jar
        if (sig.clientProfile.cookieMode === 'none') {
            params.jar = null;
        } else if (sig.clientProfile.cookieMode === 'sticky' && jar) {
            params.jar = jar;
        }

        // Make request
        const res = http.request(req.method, url, null, params);
        totalRequests.add(1);
        requestCount++;

        // Track interval evidence
        const now = Date.now();
        intervalTrend.add(now - lastRequestTime);
        lastRequestTime = now;

        // Track sensitive path evidence
        if (req.path.includes('/admin') || req.path.includes('/api') || req.path.includes('/.')) {
            sensitivePaths.add(1);
        } else {
            sensitivePaths.add(0);
        }

        // Check response against expectedStatusAny
        if (req.expectedStatusAny && req.expectedStatusAny.length > 0) {
            check(res, {
                'status is acceptable': (r) => req.expectedStatusAny.includes(r.status)
            });
        }

        // Track bot detection
        if (res.headers['X-Bot-Detection'] === 'True' || res.status === 403) {
            detectedAsBot = true;
        }

        // Burst/jitter timing logic
        if (sig.timingProfile) {
            if (requestCount < sig.timingProfile.burstRequests) {
                // Within burst - short delay with jitter
                const jitter = randomBetween(
                    sig.timingProfile.delayAfterMs.min / 1000,
                    sig.timingProfile.delayAfterMs.max / 1000
                );
                sleep(jitter);
            } else {
                // End of burst - longer pause
                const pause = randomBetween(
                    sig.timingProfile.pauseAfterBurstMs.min / 1000,
                    sig.timingProfile.pauseAfterBurstMs.max / 1000
                );
                sleep(pause);
                requestCount = 0; // Reset burst counter
                burstRate.add(1); // Track burst pattern
            }
        }
    }

    // Record detection accuracy
    detectionRate.add(detectedAsBot ? 1 : 0);

    // Log interesting cases
    if (sig.isBot && !detectedAsBot) {
        console.log(`❌ False negative: ${sig.scenarioName} not detected (confidence: ${sig.confidence})`);
    }
    if (!sig.isBot && detectedAsBot) {
        console.log(`⚠️  False positive: ${sig.scenarioName} detected as bot (confidence: ${sig.confidence})`);
    }

    // Track scenario duration
    const duration = (Date.now() - scenarioStart) / 1000;
    scenarioDuration.add(duration);
}

// Setup
export function setup() {
    console.log('================================================================================');
    console.log('BDF v2 Signature Replay - k6 Load Test');
    console.log('================================================================================');
    console.log(`Target URL: ${TARGET_URL}`);
    console.log(`Loaded signatures: ${signatures.length}`);
    console.log(`  - Bot scenarios: ${signatures.filter(s => s.isBot).length}`);
    console.log(`  - Human scenarios: ${signatures.filter(s => !s.isBot).length}`);
    console.log('');
    console.log('Features:');
    console.log('  ✓ Burst/jitter timing from timingProfile');
    console.log('  ✓ Cookie jar modes (none/stateless/sticky)');
    console.log('  ✓ Header completeness (minimal/partial/full)');
    console.log('  ✓ Client hints support');
    console.log('  ✓ Robots.txt consultation tracking');
    console.log('  ✓ Request interleaving via concurrent VUs');
    console.log('  ✓ Evidence signal tracking');
    console.log('================================================================================');
    console.log('');
    return {};
}

// Teardown
export function teardown(data) {
    console.log('');
    console.log('================================================================================');
    console.log('Load test completed');
    console.log('================================================================================');
}

