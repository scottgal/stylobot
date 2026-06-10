import type { DetectRequest, Verdict } from './types.js';
import { type GrpcClientOptions } from './grpc-client.js';
export declare class StyloBotGrpcClient {
    private readonly client;
    private readonly timeoutMs;
    constructor(endpoint: string, timeoutMs?: number, options?: GrpcClientOptions);
    detect(req: DetectRequest): Promise<Verdict>;
    renderWidget(template: string, verdict?: Verdict, vars?: Record<string, string>): Promise<string>;
    close(): void;
}
//# sourceMappingURL=grpc.d.ts.map