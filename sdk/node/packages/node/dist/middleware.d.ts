import type { RequestHandler } from 'express';
import { type Verdict, type DetectResponse } from '@stylobot/core';
export interface StyloBotMiddlewareOptions {
    /**
     * `headers` mode trusts inbound `X-StyloBot-*` headers verbatim. ONLY use it
     * when this app is reachable exclusively through a StyloBot gateway that
     * strips client-supplied copies of those headers — a directly reachable app
     * lets any caller spoof `x-stylobot-isbot: false` and bypass detection.
     * If clients can reach this app directly, use `api` or `grpc` mode instead.
     */
    mode: 'headers' | 'api' | 'grpc';
    endpoint?: string;
    apiKey?: string;
    timeout?: number;
    /** Suppress the one-time stderr warning emitted by `headers` mode. */
    suppressHeaderModeWarning?: boolean;
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