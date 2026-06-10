import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const PROTO_PATH = join(__dirname, '../proto/detection.proto');
const packageDef = protoLoader.loadSync(PROTO_PATH, {
    keepCase: false,
    longs: String,
    enums: String,
    defaults: true,
    oneofs: true,
});
const proto = grpc.loadPackageDefinition(packageDef);
function getServiceCtor() {
    const ns = proto['stylobot'];
    const det = ns['detection'];
    const v1 = det['v1'];
    return v1['DetectionService'];
}
const ServiceCtor = getServiceCtor();
const RISK_MAP = {
    RISK_BAND_UNKNOWN: 'Unknown',
    RISK_BAND_VERY_LOW: 'VeryLow',
    RISK_BAND_LOW: 'Low',
    RISK_BAND_ELEVATED: 'Elevated',
    RISK_BAND_MEDIUM: 'Medium',
    RISK_BAND_HIGH: 'High',
    RISK_BAND_VERY_HIGH: 'VeryHigh',
    RISK_BAND_VERIFIED: 'Verified',
};
const ACTION_MAP = {
    RECOMMENDED_ACTION_ALLOW: 'Allow',
    RECOMMENDED_ACTION_THROTTLE: 'Throttle',
    RECOMMENDED_ACTION_CHALLENGE: 'Challenge',
    RECOMMENDED_ACTION_BLOCK: 'Block',
};
const THREAT_MAP = {
    THREAT_BAND_NONE: 'None',
    THREAT_BAND_LOW: 'Low',
    THREAT_BAND_ELEVATED: 'Elevated',
    THREAT_BAND_HIGH: 'High',
    THREAT_BAND_CRITICAL: 'Critical',
};
export function createGrpcDetectionClient(endpoint, options) {
    const credentials = options?.tls
        ? grpc.credentials.createSsl(options.rootCerts)
        : grpc.credentials.createInsecure();
    return new ServiceCtor(endpoint, credentials);
}
export function grpcDetect(client, req, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const deadline = new Date(Date.now() + timeoutMs);
        client['detect']({
            method: req.method,
            path: req.path,
            headers: req.headers,
            remoteIp: req.remoteIp,
            protocol: req.protocol ?? 'https',
        }, { deadline }, (err, response) => {
            if (err)
                reject(err);
            else
                resolve(response);
        });
    });
}
export function grpcRenderWidget(client, template, verdict, vars, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const deadline = new Date(Date.now() + timeoutMs);
        client['renderWidget']({ template, verdict, vars: vars ?? {} }, { deadline }, (err, response) => {
            if (err)
                reject(err);
            else if (!response.success)
                reject(new Error(response.error || 'render failed'));
            else
                resolve(response.html);
        });
    });
}
export function mapGrpcVerdict(r) {
    return {
        isBot: r.isBot,
        botProbability: r.botProbability,
        confidence: r.confidence,
        botType: (r.botType || null),
        botName: r.botName || null,
        riskBand: RISK_MAP[r.riskBand] ?? 'Unknown',
        recommendedAction: ACTION_MAP[r.recommendedAction] ?? 'Allow',
        threatScore: r.threatScore,
        threatBand: THREAT_MAP[r.threatBand] ?? 'None',
    };
}
//# sourceMappingURL=grpc-client.js.map