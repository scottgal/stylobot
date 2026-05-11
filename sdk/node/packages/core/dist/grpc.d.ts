import type { DetectRequest, Verdict } from './types.js';
export declare class StyloBotGrpcClient {
    private readonly client;
    private readonly timeoutMs;
    constructor(endpoint: string, timeoutMs?: number);
    detect(req: DetectRequest): Promise<Verdict>;
    renderWidget(template: string, verdict?: Verdict, vars?: Record<string, string>): Promise<string>;
    close(): void;
}
//# sourceMappingURL=grpc.d.ts.map