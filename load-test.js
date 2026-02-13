import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

// Custom metrics
const botRequests = new Counter('bot_requests');
const humanRequests = new Counter('human_requests');
const detectionRate = new Rate('detection_rate');

// Load test configuration
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs
        { duration: '1m', target: 10 },   // Stay at 10 VUs
        { duration: '10s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<500'], // 95% of requests should be below 500ms
        http_req_failed: ['rate<0.1'],    // Less than 10% requests should fail
    },
};

// Gateway URL (bot detection proxy)
const GATEWAY_URL = 'http://localhost:5080';

// Bot patterns from signatures
const botPatterns = [
    {
        path: '/',
        method: 'HEAD',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.73
    },
    {
        path: '/',
        method: 'HEAD',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.73
    },
    {
        path: '/',
        method: 'HEAD',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.73
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.80
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.80
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.70
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.70
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.78
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.78
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.78
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.78
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.70
    },
    {
        path: '/',
        method: 'HEAD',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.73
    },
    {
        path: '/',
        method: 'GET',
        userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        threatType: 'Unknown',
        expectedDetection: true,
        confidenceScore: 0.80
    },
];

// Human patterns from signatures
const humanPatterns = [
];

// Main test function - called for each VU iteration
export default function() {
    // Choose between bot and human pattern
    // If we have both: 70% human, 30% bot (realistic traffic mix)
    // If we only have bots: 100% bot
    let isBot, patterns;
    if (humanPatterns.length > 0 && botPatterns.length > 0) {
        isBot = Math.random() < 0.3;
        patterns = isBot ? botPatterns : humanPatterns;
    } else if (humanPatterns.length > 0) {
        isBot = false;
        patterns = humanPatterns;
    } else {
        isBot = true;
        patterns = botPatterns;
    }

    const pattern = patterns[Math.floor(Math.random() * patterns.length)];

    // Build request
    const url = `${GATEWAY_URL}${pattern.path}`;
    const params = {
        headers: {
            'User-Agent': pattern.userAgent,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate',
            'Connection': 'keep-alive',
        },
        tags: {
            pattern_type: isBot ? 'bot' : 'human',
            threat_type: pattern.threatType,
            expected_score: pattern.confidenceScore
        }
    };

    // Make request
    const res = http.get(url, params);

    // Check response
    const success = check(res, {
        'status is 200 or 403': (r) => r.status === 200 || r.status === 403,
        'response has bot detection header': (r) => r.headers['X-Bot-Detection'] !== undefined,
    });

    // Track metrics
    if (isBot) {
        botRequests.add(1);
    } else {
        humanRequests.add(1);
    }

    // Check if bot was detected
    const detectedAsBot = res.headers['X-Bot-Detection'] === 'True' || res.status === 403;
    detectionRate.add(detectedAsBot ? 1 : 0);

    // Log interesting cases
    if (pattern.expectedDetection && !detectedAsBot) {
        console.log(`False negative: ${pattern.threatType} not detected (score: ${pattern.confidenceScore})`);
    }
    if (!pattern.expectedDetection && detectedAsBot) {
        console.log(`False positive: Human detected as bot`);
    }

    // Realistic pacing - humans are slower, bots are faster
    sleep(isBot ? Math.random() * 0.5 : Math.random() * 2 + 1);
}

// Setup function - runs once before test
export function setup() {
    console.log('Starting load test against bot detection gateway');
    console.log(`Gateway URL: ${GATEWAY_URL}`);
    console.log(`Bot patterns: ${botPatterns.length}`);
    console.log(`Human patterns: ${humanPatterns.length}`);
    return {};
}

// Teardown function - runs once after test
export function teardown(data) {
    console.log('Load test completed');
}
