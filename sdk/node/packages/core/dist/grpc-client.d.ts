import * as grpc from '@grpc/grpc-js';
import type { Verdict, DetectRequest } from './types.js';
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
export declare function createGrpcDetectionClient(endpoint: string): grpc.Client;
export declare function grpcDetect(client: grpc.Client, req: DetectRequest, timeoutMs?: number): Promise<GrpcDetectResponse>;
export declare function grpcRenderWidget(client: grpc.Client, template: string, verdict?: Verdict, vars?: Record<string, string>, timeoutMs?: number): Promise<string>;
export declare function mapGrpcVerdict(r: GrpcDetectResponse): Verdict;
//# sourceMappingURL=grpc-client.d.ts.map