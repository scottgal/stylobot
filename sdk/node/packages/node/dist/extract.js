export function extractDetectRequest(req) {
    const headers = {};
    for (const [key, value] of Object.entries(req.headers)) {
        if (value !== undefined) {
            headers[key] = Array.isArray(value) ? value.join(', ') : value;
        }
    }
    const remoteIp = req.ip
        ?? (headers['x-forwarded-for']?.split(',')[0]?.trim())
        ?? req.socket?.remoteAddress
        ?? '0.0.0.0';
    const path = req.originalUrl ?? req.url ?? '/';
    const protocol = req.protocol
        ?? (headers['x-forwarded-proto']?.split(',')[0]?.trim())
        ?? (req.socket?.encrypted ? 'https' : 'http');
    return { method: req.method ?? 'GET', path, headers, remoteIp, protocol };
}
//# sourceMappingURL=extract.js.map