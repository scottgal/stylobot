import type { RequestHandler } from 'express';
export interface SbVerdictInjectorOptions {
    mode: 'gateway' | 'sidecar';
    endpoint?: string;
    apiKey?: string;
    timeout?: number;
}
export declare function sbVerdictInjector(options: SbVerdictInjectorOptions): RequestHandler;
//# sourceMappingURL=injector.d.ts.map