import type { DetectRequest } from '@stylobot/core';
import type { IncomingMessage } from 'node:http';
export declare function extractDetectRequest(req: IncomingMessage & {
    ip?: string;
    originalUrl?: string;
    protocol?: string;
}): DetectRequest;
//# sourceMappingURL=extract.d.ts.map