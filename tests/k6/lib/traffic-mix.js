// Weighted user-agent pool: 70% human, 20% tool, 10% scanner
const USER_AGENTS = [
  // Human browsers (weight 7 each = 70% total)
  { ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36', weight: 3 },
  { ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36', weight: 2 },
  { ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4.1 Mobile/15E148 Safari/604.1', weight: 1 },
  { ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0', weight: 1 },
  // Tools (weight 2 each = 20% total)
  { ua: 'curl/8.6.0', weight: 1 },
  { ua: 'python-requests/2.31.0', weight: 1 },
  // Scanners (weight 1 each = 10% total)
  { ua: 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)', weight: 0.5 },
  { ua: 'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)', weight: 0.25 },
  { ua: 'Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)', weight: 0.25 },
];

// Build cumulative weight table for weighted random selection
const TOTAL_WEIGHT = USER_AGENTS.reduce((sum, e) => sum + e.weight, 0);
const CUMULATIVE = [];
let cumSum = 0;
for (const entry of USER_AGENTS) {
  cumSum += entry.weight;
  CUMULATIVE.push({ ua: entry.ua, cumWeight: cumSum });
}

export function randomUserAgent() {
  const rand = Math.random() * TOTAL_WEIGHT;
  for (const entry of CUMULATIVE) {
    if (rand <= entry.cumWeight) return entry.ua;
  }
  return CUMULATIVE[CUMULATIVE.length - 1].ua;
}

const PATHS = [
  '/', '/about', '/blog', '/contact', '/products',
  '/api/data', '/api/users', '/search?q=test',
  '/static/main.css', '/static/app.js',
  '/.env', '/admin/login', '/wp-admin',  // scanner paths
];

export function randomPath() {
  return PATHS[Math.floor(Math.random() * PATHS.length)];
}

export function requestHeaders(overrides = {}) {
  return {
    'User-Agent': randomUserAgent(),
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.5',
    'Connection': 'keep-alive',
    ...overrides,
  };
}

export function assertStylobotHeaders(res) {
  const checks = {};
  checks['X-StyloBot-IsBot header present'] = res.headers['x-stylobot-isbot'] !== undefined ||
    res.headers['X-StyloBot-IsBot'] !== undefined;
  checks['X-StyloBot-Probability header present'] = res.headers['x-stylobot-probability'] !== undefined ||
    res.headers['X-StyloBot-Probability'] !== undefined;
  checks['X-StyloBot-Action header present'] = res.headers['x-stylobot-action'] !== undefined ||
    res.headers['X-StyloBot-Action'] !== undefined;
  return checks;
}
