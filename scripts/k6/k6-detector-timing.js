// Precise hot-path timing analysis.
// Records detection_ms per UA type to isolate which path is slow.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const detectBot    = new Trend('detect_bot_ms',    true);
const detectHuman  = new Trend('detect_human_ms',  true);
const detectAI     = new Trend('detect_ai_ms',     true);
const detectCrawler = new Trend('detect_crawler_ms', true);

const BASE = __ENV.BASE_URL || 'http://localhost:5080';

const profiles = [
    {
        tag: 'human',
        ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
        metric: detectHuman,
        weight: 0.30,
    },
    {
        tag: 'bot_tool',
        ua: 'python-requests/2.31.0',
        metric: detectBot,
        weight: 0.25,
    },
    {
        tag: 'bot_curl',
        ua: 'curl/8.6.0',
        metric: detectBot,
        weight: 0.15,
    },
    {
        tag: 'ai_gpt',
        ua: 'Mozilla/5.0 AppleWebKit/537.36; compatible; GPTBot/1.2; +https://openai.com/gptbot',
        metric: detectAI,
        weight: 0.15,
    },
    {
        tag: 'crawler_scrapy',
        ua: 'Scrapy/2.11.0',
        metric: detectCrawler,
        weight: 0.15,
    },
];

// Cumulative weight table for weighted random selection
const cumulativeWeights = [];
let cum = 0;
for (const p of profiles) { cum += p.weight; cumulativeWeights.push(cum); }

function pickProfile() {
    const r = Math.random();
    for (let i = 0; i < cumulativeWeights.length; i++)
        if (r < cumulativeWeights[i]) return profiles[i];
    return profiles[profiles.length - 1];
}

export const options = {
    scenarios: {
        steady: { executor: 'constant-vus', vus: 20, duration: '60s' },
    },
    thresholds: {
        detect_bot_ms:     ['p(95)<30',  'p(99)<100'],
        detect_human_ms:   ['p(95)<50',  'p(99)<150'],
        detect_ai_ms:      ['p(95)<30',  'p(99)<100'],
        detect_crawler_ms: ['p(95)<30',  'p(99)<100'],
    },
};

export default function () {
    const profile = pickProfile();
    const res = http.get(`${BASE}/`, {
        headers: { 'User-Agent': profile.ua },
        tags: { ua_type: profile.tag },
    });

    check(res, { 'not 5xx': (r) => r.status < 500 });

    const ms = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    if (ms > 0) profile.metric.add(ms);
}
