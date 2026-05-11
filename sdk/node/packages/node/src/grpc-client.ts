import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import type { Verdict, DetectRequest, RiskBand, RecommendedAction, ThreatBand, BotType } from '@stylobot/core';

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

const proto = grpc.loadPackageDefinition(packageDef) as Record<string, unknown>;

// Navigate nested namespace: stylobot.detection.v1.DetectionService
function getServiceCtor(): grpc.ServiceClientConstructor {
  const ns = proto['stylobot'] as Record<string, unknown>;
  const det = ns['detection'] as Record<string, unknown>;
  const v1 = det['v1'] as Record<string, unknown>;
  return v1['DetectionService'] as grpc.ServiceClientConstructor;
}

const ServiceCtor = getServiceCtor();

export interface GrpcDetectResponse {
  isBot: boolean;
  botProbability: number;
  confidence: number;
  botType: string;
  botName: string;
  riskBand: string;
  recommendedAction: string;
  threatScore: number;
  threatBand: string;
  processingTimeMs: number;
  detectorsRun: number;
}

const RISK_MAP: Record<string, RiskBand> = {
  RISK_BAND_UNKNOWN: 'Unknown',
  RISK_BAND_VERY_LOW: 'VeryLow',
  RISK_BAND_LOW: 'Low',
  RISK_BAND_ELEVATED: 'Elevated',
  RISK_BAND_MEDIUM: 'Medium',
  RISK_BAND_HIGH: 'High',
  RISK_BAND_VERY_HIGH: 'VeryHigh',
  RISK_BAND_VERIFIED: 'Verified',
};

const ACTION_MAP: Record<string, RecommendedAction> = {
  RECOMMENDED_ACTION_ALLOW: 'Allow',
  RECOMMENDED_ACTION_THROTTLE: 'Throttle',
  RECOMMENDED_ACTION_CHALLENGE: 'Challenge',
  RECOMMENDED_ACTION_BLOCK: 'Block',
};

const THREAT_MAP: Record<string, ThreatBand> = {
  THREAT_BAND_NONE: 'None',
  THREAT_BAND_LOW: 'Low',
  THREAT_BAND_ELEVATED: 'Elevated',
  THREAT_BAND_HIGH: 'High',
  THREAT_BAND_CRITICAL: 'Critical',
};

export function createGrpcDetectionClient(endpoint: string): grpc.Client {
  return new ServiceCtor(endpoint, grpc.credentials.createInsecure());
}

export function grpcDetect(client: grpc.Client, req: DetectRequest): Promise<GrpcDetectResponse> {
  return new Promise((resolve, reject) => {
    const deadline = new Date(Date.now() + 5000);
    (client as unknown as Record<string, (arg0: unknown, arg1: unknown, arg2: unknown) => void>)[
      'detect'
    ](
      {
        method: req.method,
        path: req.path,
        headers: req.headers,
        remoteIp: req.remoteIp,
        protocol: req.protocol ?? 'https',
      },
      { deadline },
      (err: grpc.ServiceError | null, response: GrpcDetectResponse) => {
        if (err) reject(err);
        else resolve(response);
      },
    );
  });
}

export function mapGrpcVerdict(r: GrpcDetectResponse): Verdict {
  return {
    isBot: r.isBot,
    botProbability: r.botProbability,
    confidence: r.confidence,
    botType: (r.botType || null) as BotType | null,
    botName: r.botName || null,
    riskBand: RISK_MAP[r.riskBand] ?? 'Unknown',
    recommendedAction: ACTION_MAP[r.recommendedAction] ?? 'Allow',
    threatScore: r.threatScore,
    threatBand: THREAT_MAP[r.threatBand] ?? 'None',
  };
}
