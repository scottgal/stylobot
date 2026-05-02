export class StyloBotClient {
    endpoint;
    apiKey;
    bearerToken;
    timeout;
    retries;
    constructor(options) {
        this.endpoint = options.endpoint.replace(/\/$/, '');
        this.apiKey = options.apiKey;
        this.bearerToken = options.bearerToken;
        this.timeout = options.timeout ?? 5000;
        this.retries = options.retries ?? 1;
    }
    async detect(request) { return this.post('/api/v1/detect', request); }
    async detectBatch(requests) { return this.post('/api/v1/detect/batch', requests); }
    async detections(params) { return this.get('/api/v1/detections', params); }
    async signatures(params) { return this.get('/api/v1/signatures', params); }
    async summary(params) { return this.get('/api/v1/summary', params); }
    async timeseries(params) { return this.get('/api/v1/timeseries', params); }
    async countries(params) { return this.get('/api/v1/countries', params); }
    async endpoints(params) { return this.get('/api/v1/endpoints', params); }
    async topBots(params) { return this.get('/api/v1/topbots', params); }
    async threats(params) { return this.get('/api/v1/threats', params); }
    async me() { return this.get('/api/v1/me'); }
    async renderWidgets(widgets) {
        const body = {
            widgets: Object.fromEntries(widgets.map(w => [w.widgetId, w.template ?? '']))
        };
        const html = await this.postHtml('/_stylobot/partials/render', body);
        return parseWidgetFragments(html);
    }
    verdictGlobal(verdict) {
        const data = verdict ?? {
            isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
            riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None'
        };
        return `<script>window.__sb=${JSON.stringify(data)}</script>`;
    }
    async postHtml(path, body) {
        const url = `${this.endpoint}${path}`;
        let lastError;
        for (let attempt = 0; attempt <= this.retries; attempt++) {
            try {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), this.timeout);
                const res = await fetch(url, {
                    method: 'POST',
                    headers: { ...this.headers(), 'content-type': 'application/json', 'accept': 'text/html' },
                    body: JSON.stringify(body),
                    signal: controller.signal,
                });
                clearTimeout(timer);
                if (!res.ok) {
                    const text = await res.text().catch(() => '');
                    throw new StyloBotApiError(res.status, text, url);
                }
                return await res.text();
            }
            catch (err) {
                lastError = err instanceof Error ? err : new Error(String(err));
                if (err instanceof StyloBotApiError && err.status < 500)
                    throw err;
            }
        }
        throw lastError ?? new Error('Widget render request failed');
    }
    headers() {
        const h = { 'content-type': 'application/json' };
        if (this.apiKey)
            h['x-sb-api-key'] = this.apiKey;
        if (this.bearerToken)
            h['authorization'] = `Bearer ${this.bearerToken}`;
        return h;
    }
    async request(method, path, body) {
        const url = `${this.endpoint}${path}`;
        let lastError;
        for (let attempt = 0; attempt <= this.retries; attempt++) {
            try {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), this.timeout);
                const res = await fetch(url, {
                    method, headers: this.headers(),
                    body: body ? JSON.stringify(body) : undefined,
                    signal: controller.signal,
                });
                clearTimeout(timer);
                if (!res.ok) {
                    const text = await res.text().catch(() => '');
                    throw new StyloBotApiError(res.status, text, url);
                }
                return (await res.json());
            }
            catch (err) {
                lastError = err instanceof Error ? err : new Error(String(err));
                if (err instanceof StyloBotApiError && err.status < 500)
                    throw err;
            }
        }
        throw lastError ?? new Error('Request failed');
    }
    get(path, params) {
        const qs = params ? toQueryString(params) : '';
        return this.request('GET', qs ? `${path}?${qs}` : path);
    }
    post(path, body) {
        return this.request('POST', path, body);
    }
}
export class StyloBotApiError extends Error {
    status;
    body;
    url;
    constructor(status, body, url) {
        super(`StyloBot API error ${status}: ${body.slice(0, 200)}`);
        this.name = 'StyloBotApiError';
        this.status = status;
        this.body = body;
        this.url = url;
    }
}
function toQueryString(params) {
    const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null);
    if (entries.length === 0)
        return '';
    return entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&');
}
function parseWidgetFragments(html) {
    if (typeof DOMParser !== 'undefined') {
        const doc = new DOMParser().parseFromString(html, 'text/html');
        const result = {};
        for (const el of Array.from(doc.body.children)) {
            const id = el.getAttribute('data-sb-widget');
            if (id)
                result[id] = el.outerHTML;
        }
        return result;
    }
    const result = {};
    for (const match of html.matchAll(/data-sb-widget="([^"]+)"/g)) {
        const id = match[1];
        const tagStart = html.lastIndexOf('<', match.index);
        if (tagStart !== -1)
            result[id] = extractElement(html, tagStart);
    }
    return result;
}
function extractElement(html, start) {
    const tagMatch = html.slice(start).match(/^<([a-zA-Z][^\s/>]*)/);
    if (!tagMatch)
        return '';
    const tag = tagMatch[1];
    let depth = 0;
    let i = start;
    while (i < html.length) {
        const nextOpen = html.indexOf(`<${tag}`, i + 1);
        const nextClose = html.indexOf(`</${tag}>`, i + 1);
        if (nextClose === -1)
            break;
        if (nextOpen !== -1 && nextOpen < nextClose) {
            depth++;
            i = nextOpen + 1;
        }
        else if (depth === 0)
            return html.slice(start, nextClose + tag.length + 3);
        else {
            depth--;
            i = nextClose + 1;
        }
    }
    return html.slice(start);
}
//# sourceMappingURL=client.js.map