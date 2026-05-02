import { parseStyloBotHeaders } from '@stylobot/core';
export function sbVerdictInjector(options) {
    if (options.mode === 'sidecar') {
        if (!options.endpoint)
            throw new Error('endpoint is required for sidecar mode');
        const base = options.endpoint.replace(/\/$/, '');
        const { apiKey, timeout = 3000 } = options;
        return async (_req, res, next) => {
            let verdict = null;
            try {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), timeout);
                const headers = { accept: 'application/json' };
                if (apiKey)
                    headers['x-sb-api-key'] = apiKey;
                const r = await fetch(`${base}/_stylobot/me`, { headers, signal: controller.signal });
                clearTimeout(timer);
                if (r.ok)
                    verdict = (await r.json());
            }
            catch { /* fail open */ }
            res.locals.sbVerdict = verdict;
            res.locals.sbVerdictScript = buildVerdictScript(verdict);
            next();
        };
    }
    return (req, res, next) => {
        const verdict = parseStyloBotHeaders(req.headers);
        res.locals.sbVerdict = verdict;
        res.locals.sbVerdictScript = buildVerdictScript(verdict);
        next();
    };
}
function buildVerdictScript(verdict) {
    const data = verdict ?? {
        isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
        riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None'
    };
    return `<script>window.__sb=${JSON.stringify(data)}</script>`;
}
//# sourceMappingURL=injector.js.map