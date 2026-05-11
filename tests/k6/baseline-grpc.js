import grpc from 'k6/net/grpc';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { randomUserAgent, randomPath, requestHeaders } from './lib/traffic-mix.js';

const client = new grpc.Client();
client.load(['../../sdk/proto'], 'detection.proto');

const detectionLatency = new Trend('detection_latency_ms', true);

export const options = {
  stages: [
    { duration: '30s', target: 1 },    // warm up
    { duration: '60s', target: 10 },   // ramp to 10
    { duration: '60s', target: 50 },   // ramp to 50
    { duration: '60s', target: 100 },  // ramp to 100
    { duration: '30s', target: 0 },    // wind down
  ],
  thresholds: {
    'grpc_req_duration{method:Detect}': ['p(99)<30'],
    'detection_latency_ms': ['p(99)<10'],
  },
};

const SIDECAR_ENDPOINT = __ENV.SIDECAR_ENDPOINT || 'localhost:5090';

let connected = false;

export default function () {
  if (!connected) {
    client.connect(SIDECAR_ENDPOINT, { plaintext: true });
    connected = true;
  }

  const response = client.invoke('stylobot.detection.v1.DetectionService/Detect', {
    method: 'GET',
    path: randomPath(),
    headers: requestHeaders(),
    remote_ip: `192.168.1.${Math.floor(Math.random() * 254) + 1}`,
    protocol: 'https',
  });

  check(response, {
    'status is OK': (r) => r && r.status === grpc.StatusOK,
    'response has is_bot field': (r) => r && r.message && typeof r.message.isBot === 'boolean',
    'bot_probability in range': (r) => r && r.message && r.message.botProbability >= 0 && r.message.botProbability <= 1,
    'confidence in range': (r) => r && r.message && r.message.confidence >= 0 && r.message.confidence <= 1,
  });

  if (response && response.message && typeof response.message.processingTimeMs === 'number') {
    detectionLatency.add(response.message.processingTimeMs);
  }

  sleep(0.1);
}

export function teardown() {
  client.close();
}
