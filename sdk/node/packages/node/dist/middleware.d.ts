import type { RequestHandler } from 'express';
import { type Verdict, type DetectResponse } from '@stylobot/core';
export interface StyloBotMiddlewareOptions {
    mode: 'headers' | 'api' | 'grpc';
    endpoint?: string;
    apiKey?: string;
    timeout?: number;
}
export interface StyloBotResult {
    isBot: boolean;
    verdict: Verdict;
    signals: Record<string, unknown>;
    reasons: DetectResponse['reasons'];
    meta: DetectResponse['meta'] | null;
}
declare global {
    namespace Express {
        interface Request {
            stylobot: StyloBotResult;
        }
    }
}
export declare function styloBotMiddleware(options: StyloBotMiddlewareOptions): RequestHandler;
//# sourceMappingURL=middleware.d.ts.map