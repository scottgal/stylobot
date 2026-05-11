import { createServer } from 'node:http';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const PORT = parseInt(process.env.PORT ?? '3001', 10);
const UPSTREAM_URL = process.env.UPSTREAM_URL ?? 'http://localhost:3000';
const STYLOBOT_ENDPOINT = process.env.STYLOBOT_ENDPOINT ?? 'http://localhost:5090';

// Load proto
const PROTO_PATH = '/sdk/node/packages/node/proto/detection.proto';
const packageDef = protoLoader.loadSync(PROTO_PATH, {
  keepCase: false, longs: String, enums: String, defaults: true, oneofs: true,
});
const proto = grpc.loadPackageDefinition(packageDef);
const ServiceCtor = proto.stylobot.detection.v1.DetectionService;

// Parse endpoint: strip http:// prefix for gRPC
function grpcEndpoint(url) {
  return url.replace(/^https?:\/\//, '');
}

const grpcClient = new ServiceCtor(grpcEndpoint(STYLOBOT_ENDPOINT), grpc.credentials.createInsecure());

function detectWithSidecar(req) {
  return new Promise((resolve, reject) => {
    const headers = {};
    for (const [k, v] of Object.entries(req.headers)) {
      if (typeof v === 'string') headers[k] = v;
    }
    grpcClient.detect(
      { method: req.method, path: req.url, headers, remoteIp: req.socket.remoteAddress ?? '', protocol: 'http' },
      { deadline: new Date(Date.now() + 200) },
      (err, res) => err ? reject(err) : resolve(res),
    );
  });
}

function injectHeaders(proxyReq, detection) {
  proxyReq.setHeader('X-StyloBot-IsBot', detection.isBot ? 'true' : 'false');
  proxyReq.setHeader('X-StyloBot-Probability', String(detection.botProbability));
  proxyReq.setHeader('X-StyloBot-Confidence', String(detection.confidence));
  proxyReq.setHeader('X-StyloBot-RiskBand', detection.riskBand ?? 'RISK_BAND_UNKNOWN');
  proxyReq.setHeader('X-StyloBot-Action', detection.recommendedAction ?? 'RECOMMENDED_ACTION_ALLOW');
}

const upstreamUrl = new URL(UPSTREAM_URL);

const server = createServer(async (req, res) => {
  // Run detection
  let detection = null;
  try {
    detection = await detectWithSidecar(req);
  } catch (err) {
    console.warn('Sidecar detection failed, failing open:', err.message);
  }

  // Forward to upstream
  const options = {
    hostname: upstreamUrl.hostname,
    port: parseInt(upstreamUrl.port || '3000', 10),
    path: req.url,
    method: req.method,
    headers: { ...req.headers },
  };

  if (detection) injectHeaders({ setHeader: (k, v) => { options.headers[k] = v; } }, detection);

  const { request: httpRequest } = await import('node:http');
  const proxyReq = httpRequest(options, (proxyRes) => {
    res.writeHead(proxyRes.statusCode ?? 200, proxyRes.headers);
    proxyRes.pipe(res);
  });
  proxyReq.on('error', (err) => {
    console.error('Upstream error:', err);
    res.writeHead(502);
    res.end('Bad Gateway');
  });
  req.pipe(proxyReq);
});

server.listen(PORT, () => {
  console.log(`Node+Sidecar test app listening on port ${PORT}`);
  console.log(`Upstream: ${UPSTREAM_URL}, Sidecar: ${STYLOBOT_ENDPOINT}`);
});
