import { StyloBotClient, parseStyloBotHeaders } from '@stylobot/core';
import { extractDetectRequest } from './extract.js';
// FastifyRequest augmentation for TypeScript consumers:
// declare module 'fastify' { interface FastifyRequest { stylobot: StyloBotResult; } }
// Add this to your own types.d.ts if you want typed access to request.stylobot.
const EMPTY_VERDICT = {
    isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
    riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None',
};
export async function styloBotPlugin(fastify, options) {
    if (options.mode === 'api') {
        if (!options.endpoint)
            throw new Error('endpoint is required for api mode');
        const client = new StyloBotClient({ endpoint: options.endpoint, apiKey: options.apiKey, timeout: options.timeout });
        fastify.decorateRequest('stylobot', null);
        fastify.addHook('preHandler', async (request) => {
            try {
                const detectReq = extractDetectRequest(request.raw);
                const response = await client.detect(detectReq);
                request.stylobot = { isBot: response.verdict.isBot, verdict: response.verdict, signals: response.signals, reasons: response.reasons, meta: response.meta };
            }
            catch {
                request.stylobot = { isBot: false, verdict: EMPTY_VERDICT, signals: {}, reasons: [], meta: null };
            }
        });
    }
    else {
        fastify.decorateRequest('stylobot', null);
        fastify.addHook('preHandler', async (request) => {
            const verdict = parseStyloBotHeaders(request.raw.headers) ?? EMPTY_VERDICT;
            request.stylobot = { isBot: verdict.isBot, verdict, signals: {}, reasons: [], meta: null };
        });
    }
}
//# sourceMappingURL=fastify.js.map