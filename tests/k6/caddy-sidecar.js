import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';
import { requestHeaders, randomPath } from './lib/traffic-mix.js';

const blockedRequests = new Counter('blocked_requests');
const stylobotHeadersPresent = new Rate('stylobot_headers_present');

export const options = {
  vus: 100,
  duration: '2m',
  thresholds: {
    http_req_duration: ['p(95)<500'],
    // No threshold on blocked_requests: k6 synthetic traffic is inherently bot-like
    // (machine timing, minimal headers). High block rates (50-90%) are expected here.
    // The meaningful checks are latency + all responses being 200 or 403.
  },
};

const CADDY_URL = __ENV.CADDY_URL || 'http://localhost:14080';

export default function () {
  const path = randomPath();
  const res = http.get(`${CADDY_URL}${path}`, {
    headers: requestHeaders(),
  });

  check(res, {
    'status 200 or 403': (r) => r.status === 200 || r.status === 403,
  });

  if (res.status === 403) {
    blockedRequests.add(1);
  }

  // When not blocked, check that StyloBot headers were injected into the upstream echo response
  if (res.status === 200) {
    let body = null;
    try {
      body = JSON.parse(res.body);
    } catch {
      // not JSON (e.g. static files)
    }

    const hasHeaders = body &&
      body.stylobot &&
      (body.stylobot['x-stylobot-isbot'] !== undefined || body.stylobot['X-StyloBot-IsBot'] !== undefined);

    stylobotHeadersPresent.add(hasHeaders ? 1 : 0);
  }

  sleep(0.1);
}
