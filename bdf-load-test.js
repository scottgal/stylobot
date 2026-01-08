import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom metrics
const totalRequests = new Counter('total_requests');
const botScenarios = new Counter('bot_scenarios');
const humanScenarios = new Counter('human_scenarios');
const detectionRate = new Rate('detection_rate');
const scenarioDuration = new Trend('scenario_duration');

// Load test configuration
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs
        { duration: '2m', target: 10 },   // Stay at 10 VUs
        { duration: '30s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<1000'],     // 95% of requests < 1s
        http_req_failed: ['rate<0.1'],         // Less than 10% failures
        'detection_rate': ['rate>0.3'],        // At least 30% detected as bots
    },
};

// Target URL (TestSite runs on 7777)
const TARGET_URL = __ENV.TARGET_URL || 'http://localhost:7777';


// Embedded BDF signatures
const signatures = [
  {
    scenarioName: 'fast-ip-network-scraping',
    scenario: 'High-speed sequential requests from a single IP across multiple suspicious paths with minimal delays, indicative of automated scraping behavior.',
    confidence: 0.92,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/api/v1/items?limit=100',
        headers: {'User-Agent': 'libwww-perl/6.67'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/search?q=bot+scraper+site',
        headers: {'User-Agent': 'libwww-perl/6.67'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/robots.txt',
        headers: {'User-Agent': 'libwww-perl/6.67'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/static/analytics.js',
        headers: {'User-Agent': 'libwww-perl/6.67'},
        expectedStatus: 200,
        delayAfter: 0.01
      },
      {
        timestamp: 0.11,
        method: 'GET',
        path: '/admin/dashboard',
        headers: {'User-Agent': 'libwww-perl/6.67'},
        expectedStatus: 403,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'fast-path-sequential-scraper',
    scenario: 'High-speed sequential requests to multiple suspicious paths with minimal delays, indicative of automated scraping behavior.',
    confidence: 0.92,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/api/products?limit=100',
        headers: {'User-Agent': 'python-urllib/3.11'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/search?q=python+scraping&page=1',
        headers: {'User-Agent': 'python-urllib/3.11'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/admin/analytics?token=scraper123',
        headers: {'User-Agent': 'python-urllib/3.11'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/static/data/last_updated.json',
        headers: {'User-Agent': 'python-urllib/3.11'},
        expectedStatus: 200,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'fast_sequential_requests',
    scenario: 'High-frequency sequential GET requests with minimal delays, targeting multiple suspicious paths in rapid succession.',
    confidence: 0.92,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/api/v1/users?limit=100',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/api/v1/products?sort=price_asc',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/admin/dashboard?token=invalid',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 403,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/search?q=bot+scraper+test',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.04
      },
      {
        timestamp: 0.14,
        method: 'GET',
        path: '/api/v1/logout',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'fast_sequential_requests_with_axios',
    scenario: 'High-speed sequential requests with rapid delays and suspicious path navigation, indicative of automated scraping behavior.',
    confidence: 0.92,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/products?page=1',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/products?page=2',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/reviews?sort=recent',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/cart',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0.01
      },
      {
        timestamp: 0.11,
        method: 'GET',
        path: '/checkout',
        headers: {'User-Agent': 'axios/1.6.2'},
        expectedStatus: 200,
        delayAfter: 0
      },
    ]
  },
  {
    scenarioName: 'fast_sequential_scrape',
    scenario: 'High-speed sequential requests with suspicious path navigation and minimal delays, indicative of automated scraping',
    confidence: 0.95,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/products',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/products/123',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/reviews?page=1',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/api/related?limit=5',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'fast_sequential_scrape_robots_ignore',
    scenario: 'High-speed sequential requests with suspicious paths and no respect for robots.txt, indicative of automated scraping.',
    confidence: 0.92,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/api/large-dataset?limit=1000',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/search?q=bot+scraper+test',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/admin/export?format=json',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/static/private/data',
        headers: {'User-Agent': 'Go-http-client/2.0'},
        expectedStatus: 403,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'fast_sequential_scraping',
    scenario: 'High-speed sequential requests across suspicious paths with minimal delays, indicative of automated scraping behavior.',
    confidence: 0.95,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/api/anonymous-data?token=12345',
        headers: {'User-Agent': 'okhttp/4.12.0'},
        expectedStatus: 200,
        delayAfter: 0.05
      },
      {
        timestamp: 0.05,
        method: 'GET',
        path: '/search?q=bot+scraper+test',
        headers: {'User-Agent': 'okhttp/4.12.0'},
        expectedStatus: 200,
        delayAfter: 0.03
      },
      {
        timestamp: 0.08,
        method: 'GET',
        path: '/robots.txt',
        headers: {'User-Agent': 'okhttp/4.12.0'},
        expectedStatus: 200,
        delayAfter: 0.02
      },
      {
        timestamp: 0.1,
        method: 'GET',
        path: '/static/non-existent-file',
        headers: {'User-Agent': 'okhttp/4.12.0'},
        expectedStatus: 404,
        delayAfter: 0.01
      },
    ]
  },
  {
    scenarioName: 'human-like-firefox-navigation',
    scenario: 'Simulates a real human browsing pattern with variable delays and natural navigation through a website hierarchy (e.g., homepage → subpages → back navigation).',
    confidence: 0.98,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 12
      },
      {
        timestamp: 12,
        method: 'GET',
        path: '/products',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 18
      },
      {
        timestamp: 30,
        method: 'GET',
        path: '/products/123',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 35,
        method: 'GET',
        path: '/products/123/reviews',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 25
      },
      {
        timestamp: 60,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 10
      },
    ]
  },
  {
    scenarioName: 'human_behavior_organic_browsing',
    scenario: 'Simulates natural human browsing with variable delays and organic path navigation (e.g., starting with homepage, exploring related categories, then returning to homepage after reading)',
    confidence: 0.95,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 10
      },
      {
        timestamp: 10,
        method: 'GET',
        path: '/products?category=electronics',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 15
      },
      {
        timestamp: 25,
        method: 'GET',
        path: '/reviews?product=12345',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 20
      },
      {
        timestamp: 45,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 50,
        method: 'GET',
        path: '/newsletter-signup',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0'},
        expectedStatus: 200,
        delayAfter: 12
      },
    ]
  },
  {
    scenarioName: 'human_browsing_with_varied_delays',
    scenario: 'Simulates a real human navigating a website with organic, variable delays between requests and natural path exploration (e.g., homepage → category pages → subpages → back to homepage).',
    confidence: 0.95,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 10
      },
      {
        timestamp: 15,
        method: 'GET',
        path: '/products/electronics',
        headers: {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 18
      },
      {
        timestamp: 33,
        method: 'GET',
        path: '/products/electronics/laptops',
        headers: {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 38,
        method: 'GET',
        path: '/products/electronics/laptops/accessories',
        headers: {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 25
      },
      {
        timestamp: 63,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 0
      },
    ]
  },
  {
    scenarioName: 'human_natural_browsing_with_delayed_paths',
    scenario: 'Simulates a real human user exploring a website with organic, non-linear navigation and variable delays between requests.',
    confidence: 0.95,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/home',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 12
      },
      {
        timestamp: 12,
        method: 'GET',
        path: '/products?category=electronics',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 18
      },
      {
        timestamp: 30,
        method: 'GET',
        path: '/about',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 35,
        method: 'GET',
        path: '/blog?page=2',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 25
      },
      {
        timestamp: 60,
        method: 'GET',
        path: '/contact',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 10
      },
    ]
  },
  {
    scenarioName: 'human_navigation_with_delays',
    scenario: 'Simulates a real human browsing pattern with organic delays and natural path exploration on a website',
    confidence: 0.98,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 10
      },
      {
        timestamp: 10,
        method: 'GET',
        path: '/search?q=human+behavior+detector',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 15
      },
      {
        timestamp: 25,
        method: 'GET',
        path: '/about',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 20
      },
      {
        timestamp: 45,
        method: 'GET',
        path: '/blog/2023/09/ai-detection-patterns',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 50,
        method: 'GET',
        path: '/contact',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 12
      },
    ]
  },
  {
    scenarioName: 'human_request_timing_variation',
    scenario: 'Simulates natural human browsing with variable delays between requests and realistic path navigation (e.g., homepage → category → product details).',
    confidence: 0.98,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 10
      },
      {
        timestamp: 10,
        method: 'GET',
        path: '/electronics/category',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 18
      },
      {
        timestamp: 28,
        method: 'GET',
        path: '/electronics/category/laptops',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 33,
        method: 'GET',
        path: '/electronics/category/laptops/120-herz',
        headers: {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'},
        expectedStatus: 200,
        delayAfter: 22
      },
    ]
  },
  {
    scenarioName: 'natural_browsing_with_delays',
    scenario: 'Simulates a human user exploring a website with organic delays and natural navigation patterns, including back-and-forth browsing and random page exploration.',
    confidence: 0.98,
    isBot: true,
    requests: [
      {
        timestamp: 0,
        method: 'GET',
        path: '/',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 12
      },
      {
        timestamp: 12,
        method: 'GET',
        path: '/search?q=technology',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 5
      },
      {
        timestamp: 17,
        method: 'GET',
        path: '/products/123',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 20
      },
      {
        timestamp: 37,
        method: 'GET',
        path: '/blog',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 8
      },
      {
        timestamp: 45,
        method: 'GET',
        path: '/about',
        headers: {'User-Agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'},
        expectedStatus: 200,
        delayAfter: 15
      },
    ]
  },
];


// Main test function - each VU iteration picks a random signature and replays it
export default function() {
    const scenarioStart = Date.now();

    // Pick a random signature
    const sig = signatures[Math.floor(Math.random() * signatures.length)];

    // Track bot vs human scenarios
    if (sig.isBot) {
        botScenarios.add(1);
    } else {
        humanScenarios.add(1);
    }

    console.log(`[VU ${__VU}] Playing: ${sig.scenarioName} (confidence: ${sig.confidence})`);

    let detectedAsBot = false;

    // Replay all requests in the scenario
    for (let i = 0; i < sig.requests.length; i++) {
        const req = sig.requests[i];

        // Build full URL
        const url = `${TARGET_URL}${req.path}`;

        // Prepare headers
        const params = {
            headers: {
                'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                'Accept-Language': 'en-US,en;q=0.9',
                'Accept-Encoding': 'gzip, deflate',
                'Connection': 'keep-alive',
                ...req.headers
            },
            tags: {
                scenario: sig.scenarioName,
                scenario_type: sig.isBot ? 'bot' : 'human',
                request_index: i,
                expected_confidence: sig.confidence
            }
        };

        // Make request
        const res = http.request(req.method, url, null, params);
        totalRequests.add(1);

        // Check response
        check(res, {
            'status is expected or blocked': (r) => r.status === req.expectedStatus || r.status === 403 || r.status === 200,
            'has bot detection header': (r) => r.headers['X-Bot-Detection'] !== undefined,
        });

        // Track if detected as bot
        if (res.headers['X-Bot-Detection'] === 'True' || res.status === 403) {
            detectedAsBot = true;
        }

        // Wait before next request (as specified in signature)
        if (req.delayAfter > 0) {
            sleep(req.delayAfter);
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
    console.log('BDF Signature Replay - k6 Load Test');
    console.log('================================================================================');
    console.log(`Target URL: ${TARGET_URL}`);
    console.log(`Loaded signatures: ${signatures.length}`);
    console.log(`  - Bot scenarios: ${signatures.filter(s => s.isBot).length}`);
    console.log(`  - Human scenarios: ${signatures.filter(s => !s.isBot).length}`);
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

