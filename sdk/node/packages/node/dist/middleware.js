import { StyloBotClient, StyloBotGrpcClient, parseStyloBotHeaders } from '@stylobot/core';
import { extractDetectRequest } from './extract.js';
const EMPTY_VERDICT = {
    isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
    riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None',
};
export function styloBotMiddleware(options) {
    if (options.mode === 'grpc') {
        if (!options.endpoint)
            throw new Error('endpoint is required for grpc mode');
        const grpcClient = new StyloBotGrpcClient(options.endpoint, options.timeout ?? 5000);
        return async (req, _res, next) => {
            try {
                const detectReq = extractDetectRequest(req);
                const verdict = await grpcClient.detect(detectReq);
                req.stylobot = {
                    isBot: verdict.isBot,
                    verdict,
                    signals: {},
                    reasons: [],
                    meta: null,
                };
            }
            catch {
                req.stylobot = { isBot: false, verdict: EMPTY_VERDICT, signals: {}, reasons: [], meta: null };
            }
            next();
        };
    }
    if (options.mode === 'api') {
        if (!options.endpoint)
            throw new Error('endpoint is required for api mode');
        const client = new StyloBotClient({ endpoint: options.endpoint, apiKey: options.apiKey, timeout: options.timeout });
        return async (req, res, next) => {
            try {
                const detectReq = extractDetectRequest(req);
                const response = await client.detect(detectReq);
                req.stylobot = { isBot: response.verdict.isBot, verdict: response.verdict, signals: response.signals, reasons: response.reasons, meta: response.meta };
            }
            catch {
                req.stylobot = { isBot: false, verdict: EMPTY_VERDICT, signals: {}, reasons: [], meta: null };
            }
            next();
        };
    }
    return (req, _res, next) => {
        const verdict = parseStyloBotHeaders(req.headers) ?? EMPTY_VERDICT;
        req.stylobot = { isBot: verdict.isBot, verdict, signals: {}, reasons: [], meta: null };
        next();
    };
}
//# sourceMappingURL=middleware.js.map