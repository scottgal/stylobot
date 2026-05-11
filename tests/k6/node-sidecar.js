import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';
import { requestHeaders, randomPath } from './lib/traffic-mix.js';

const stylobotHeadersPresent = new Rate('stylobot_headers_present');
const botDetected = new Counter('bots_detected');

export const options = {
  vus: 50,
  duration: '2m',
  thresholds: {
    http_req_duration: ['p(95)<500'],
    stylobot_headers_present: ['rate>0.95'],
  },
};

const APP_URL = __ENV.APP_URL || 'http://localhost:13001';

export default function () {
  const path = randomPath();
  const res = http.get(`${APP_URL}${path}`, {
    headers: requestHeaders(),
  });

  const checksPass = check(res, {
    'status 200': (r) => r.status === 200,
    'response is JSON': (r) => r.headers['Content-Type'] && r.headers['Content-Type'].includes('application/json'),
  });

  // The Node proxy injects X-StyloBot-* headers into the upstream request (not the response),
  // so res.headers won't contain them. The echo server mirrors them into body.stylobot instead.
  // assertStylobotHeaders() checks res.headers directly (correct for Caddy), so it cannot be
  // used here. Body-based detection is the right approach for this integration pattern.
  // Parse the echo response body which contains forwarded stylobot headers
  let body = null;
  try {
    body = JSON.parse(res.body);
  } catch {
    // not JSON
  }

  const hasStylobotHeaders = body &&
    body.stylobot &&
    (body.stylobot['x-stylobot-isbot'] !== undefined || body.stylobot['X-StyloBot-IsBot'] !== undefined);

  stylobotHeadersPresent.add(hasStylobotHeaders ? 1 : 0);

  if (hasStylobotHeaders) {
    const isBot = body.stylobot['x-stylobot-isbot'] === 'true' ||
      body.stylobot['X-StyloBot-IsBot'] === 'true';
    if (isBot) botDetected.add(1);
  }

  sleep(0.1);
}
