import Alpine from 'alpinejs';

// ===== Shared Utilities =====

function toCamel(obj: any): any {
    if (Array.isArray(obj)) return obj.map(toCamel);
    if (obj !== null && typeof obj === 'object') {
        return Object.fromEntries(
            Object.entries(obj).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), toCamel(v)])
        );
    }
    return obj;
}

function countryFlag(code: string, isPrivate: boolean): string {
    if (isPrivate || !code || code.length !== 2 || code.toUpperCase() === 'XX') return '\uD83C\uDF10';
    return String.fromCodePoint(...[...code.toUpperCase()].map(c => 0x1F1E6 + c.charCodeAt(0) - 65));
}

function themeColor(name: string): string {
    const styles = getComputedStyle(document.documentElement);
    const map: Record<string, string> = {
        positive: '--sb-signal-pos',
        warning: '--sb-signal-warn',
        danger: '--sb-signal-danger',
        accent: '--sb-accent',
        accentAlt: '--sb-accent-alt',
        muted: '--sb-card-subtle'
    };
    const key = map[name] || '--sb-card-subtle';
    return styles.getPropertyValue(key).trim() || '#6b7280';
}

function alphaColor(color: string, amount: number): string {
    return `color-mix(in oklab, ${color} ${amount}%, transparent)`;
}

async function loadSignalR(): Promise<void> {
    if (typeof (window as any).signalR !== 'undefined') return;
    await new Promise<void>((resolve, reject) => {
        const s = document.createElement('script');
        s.src = 'https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js';
        s.onload = () => resolve();
        s.onerror = reject;
        document.head.appendChild(s);
    });
}

// ===== LiveDemo Alpine Component =====

function liveDetection() {
    return {
        connection: null as any,
        connected: false,
        summary: null as any,
        summaryLoading: true,
        visitorMap: {} as Record<string, any>,
        maxVisitors: 100,
        feedLoading: true,
        feedPaused: false,
        initialLoadComplete: false,
        yourData: null as any,
        yourLoading: true,
        yourError: null as string | null,
        signature: '',
        hitCount: 1,
        justUpdated: false,
        showAllContributions: false,
        showSignals: false,
        probabilityHistory: [] as number[],
        endpointStats: {} as Record<string, { total: number; bots: number }>,
        expandedItem: null as string | null,
        selectedVisitor: null as string | null,
        filterEndpoint: null as string | null,
        visitorFilter: 'all',
        sortField: 'lastSeen',
        sortDir: 'desc',

        get feedItems() {
            let items = Object.values(this.visitorMap);
            if (this.filterEndpoint) items = items.filter((v: any) => v.paths && v.paths.includes(this.filterEndpoint));
            if (this.visitorFilter === 'humans') items = items.filter((v: any) => !v.isBot);
            else if (this.visitorFilter === 'bots') items = items.filter((v: any) => v.isBot);
            else if (this.visitorFilter === 'ai') items = items.filter((v: any) => v.isBot && (v.botType === 'AiBot' || /ai|gpt|claude|llm|chatbot|copilot|gemini|bard/i.test(v.botName || '')));
            else if (this.visitorFilter === 'tools') items = items.filter((v: any) => v.isBot && ['Scraper', 'MonitoringBot', 'SearchEngine', 'SocialMediaBot', 'VerifiedBot', 'GoodBot'].includes(v.botType));
            return this.sortItems(items).slice(0, 50);
        },

        sortItems(items: any[]) {
            const dir = this.sortDir === 'asc' ? 1 : -1;
            return items.sort((a: any, b: any) => {
                switch (this.sortField) {
                    case 'name': return dir * (a.botName || a.sig).localeCompare(b.botName || b.sig);
                    case 'hits': return dir * ((a.hits || 0) - (b.hits || 0));
                    case 'risk': {
                        const order: Record<string, number> = { 'VeryHigh': 5, 'High': 4, 'Medium': 3, 'Elevated': 3, 'Low': 2, 'VeryLow': 1 };
                        return dir * ((order[a.riskBand] || 0) - (order[b.riskBand] || 0));
                    }
                    case 'lastSeen': default: return dir * (new Date(a.lastSeen).getTime() - new Date(b.lastSeen).getTime());
                }
            });
        },

        toggleSort(field: string) {
            if (this.sortField === field) {
                this.sortDir = this.sortDir === 'asc' ? 'desc' : 'asc';
            } else {
                this.sortField = field;
                this.sortDir = 'desc';
            }
        },

        sortIcon(field: string) {
            if (this.sortField !== field) return '';
            return this.sortDir === 'asc' ? '\u25B2' : '\u25BC';
        },

        get filterCounts() {
            const all = Object.values(this.visitorMap);
            return {
                all: all.length,
                humans: all.filter((v: any) => !v.isBot).length,
                bots: all.filter((v: any) => v.isBot).length,
                ai: all.filter((v: any) => v.isBot && (v.botType === 'AiBot' || /ai|gpt|claude|llm|chatbot|copilot|gemini|bard/i.test(v.botName || ''))).length,
                tools: all.filter((v: any) => v.isBot && ['Scraper', 'MonitoringBot', 'SearchEngine', 'SocialMediaBot', 'VerifiedBot', 'GoodBot'].includes(v.botType)).length
            };
        },

        get activeDetail() { return this.selectedVisitor ? this.visitorMap[this.selectedVisitor] : null; },
        get botCount() { return this.summary?.botRequests || 0; },
        get humanCount() { return this.summary?.humanRequests || 0; },
        get totalCount() { return this.summary?.totalRequests || 0; },
        get botPct() { return this.summary?.botPercentage?.toFixed(1) || '0.0'; },
        get avgMs() { return this.summary?.averageProcessingTimeMs?.toFixed(0) || '0'; },
        get uniqueSigs() { return this.summary?.uniqueSignatures || 0; },

        get topEndpoints() {
            return Object.entries(this.endpointStats)
                .map(([path, s]: [string, any]) => ({ path, total: s.total, bots: s.bots, botPct: s.total > 0 ? ((s.bots / s.total) * 100).toFixed(0) : '0' }))
                .sort((a, b) => b.total - a.total)
                .slice(0, 8);
        },

        get sparklinePath() {
            const h = this.probabilityHistory; if (h.length < 2) return '';
            const w = 4, maxH = 40; return h.map((p: number, i: number) => (i === 0 ? 'M' : 'L') + (i * w) + ',' + ((1 - p) * maxH)).join(' ');
        },
        get sparklineWidth() { return Math.max((this.probabilityHistory.length - 1) * 4, 20); },
        get sparklineColor() {
            const h = this.probabilityHistory; if (h.length === 0) return themeColor('positive');
            const last = h[h.length - 1]; return last >= 0.7 ? themeColor('danger') : last >= 0.4 ? themeColor('warning') : themeColor('positive');
        },
        get gaugeColor() {
            if (!this.yourData) return themeColor('positive');
            const p = this.yourData.botProbability; return p >= 0.7 ? themeColor('danger') : p >= 0.4 ? themeColor('warning') : themeColor('positive');
        },
        get gaugeDash() { if (!this.yourData) return '0 251.3'; return (this.yourData.botProbability * 251.3) + ' 251.3'; },
        get topContributions() {
            if (!this.yourData?.contributions) return [];
            const s = [...this.yourData.contributions].sort((a: any, b: any) => Math.abs(b.impact) - Math.abs(a.impact));
            return this.showAllContributions ? s : s.slice(0, 5);
        },
        get signalEntries() { if (!this.yourData?.signals) return []; return Object.entries(this.yourData.signals); },
        get confidenceWord() {
            if (!this.yourData) return '';
            const c = this.yourData.confidence; return c >= 0.9 ? 'very confident' : c >= 0.7 ? 'confident' : c >= 0.5 ? 'fairly confident' : 'low confidence';
        },
        get categorizedContributions() {
            if (!this.yourData?.contributions) return {};
            const cats: Record<string, any[]> = {};
            this.yourData.contributions.forEach((c: any) => {
                const cat = c.category || 'Other';
                if (!cats[cat]) cats[cat] = [];
                cats[cat].push(c);
            });
            return cats;
        },

        detectorDocUrl(name: string) {
            const docMap: Record<string, string> = {
                'UserAgent': 'user-agent-detection', 'Header': 'header-detection', 'Ip': 'ip-detection',
                'ClientSide': 'client-side-fingerprinting', 'Behavioral': 'behavioral-analysis',
                'TlsFingerprint': 'AdvancedFingerprintingDetectors', 'Http2Fingerprint': 'AdvancedFingerprintingDetectors',
                'Http3Fingerprint': 'http3-fingerprinting', 'TcpIpFingerprint': 'AdvancedFingerprintingDetectors',
                'Heuristic': 'ai-detection', 'HeuristicLate': 'ai-detection', 'Llm': 'ai-detection',
                'BehavioralWaveform': 'advanced-behavioral-detection', 'MultiLayerCorrelation': 'AdvancedFingerprintingDetectors'
            };
            const slug = docMap[name] || 'configuration';
            return 'https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/' + slug + '.md';
        },

        categoryIcon(cat: string) {
            return ({ 'Browser': 'bx-globe', 'Network': 'bx-cloud', 'Behavioral': 'bx-pulse', 'Fingerprint': 'bx-fingerprint', 'AI': 'bx-brain', 'Reputation': 'bx-shield' } as Record<string, string>)[cat] || 'bx-analyse';
        },

        categoryColor(cat: string) {
            return ({ 'Browser': themeColor('accent'), 'Network': themeColor('warning'), 'Behavioral': themeColor('positive'), 'Fingerprint': themeColor('accentAlt'), 'AI': themeColor('danger'), 'Reputation': themeColor('muted') } as Record<string, string>)[cat] || themeColor('muted');
        },

        themeColor,
        alphaColor,

        riskLabelClass(band: string) {
            const normalized = (band || 'unknown').toLowerCase();
            return `risk-text-${normalized}`;
        },

        toCamel,

        riskColor(band: string) {
            return ({ 'VeryLow': themeColor('positive'), 'Low': themeColor('positive'), 'Elevated': themeColor('warning'), 'Medium': themeColor('warning'), 'High': themeColor('danger'), 'VeryHigh': themeColor('danger') } as Record<string, string>)[band] || themeColor('muted');
        },

        timeAgo(ts: string) {
            if (!ts) return '';
            const d = Math.floor((Date.now() - new Date(ts).getTime()) / 1000);
            if (d < 5) return 'now'; if (d < 60) return d + 's'; if (d < 3600) return Math.floor(d / 60) + 'm'; return Math.floor(d / 3600) + 'h';
        },

        truncPath(p: string, max?: number) {
            max = max || 30; if (!p) return '/'; return p.length > max ? p.substring(0, max - 3) + '...' : p;
        },

        selectVisitor(sig: string) { this.selectedVisitor = this.selectedVisitor === sig ? null : sig; },
        selectEndpoint(path: string) { this.filterEndpoint = this.filterEndpoint === path ? null : path; this.selectedVisitor = null; },
        clearFilters() { this.filterEndpoint = null; this.selectedVisitor = null; },

        async init() {
            this.fetchYourDetection();
            await Promise.all([this.fetchSummary(), this.fetchSignatures(), this.fetchDetections()]);
            this.feedLoading = false;
            this.initialLoadComplete = true;
            this.connectSignalR();
            setInterval(() => (this as any).$forceUpdate?.(), 10000);
        },

        async fetchSummary() {
            try { const r = await fetch('/_stylobot/api/summary'); if (r.ok) this.summary = toCamel(await r.json()); } catch (e) { }
            this.summaryLoading = false;
        },

        async fetchSignatures() {
            try {
                const r = await fetch('/_stylobot/api/signatures?limit=50');
                if (r.ok) {
                    const sigs = toCamel(await r.json()) || [];
                    sigs.forEach((s: any) => {
                        const sig = s.primarySignature;
                        if (!sig || this.visitorMap[sig]) return;
                        const isBot = s.isKnownBot || s.riskBand === 'High' || s.riskBand === 'VeryHigh';
                        this.visitorMap[sig] = {
                            sig, hits: s.hitCount || 1,
                            firstSeen: s.timestamp, lastSeen: s.timestamp,
                            isBot, botProbability: s.botProbability ?? null, confidence: s.confidence ?? null,
                            riskBand: s.riskBand || 'Medium', lastPath: s.lastPath ?? null, paths: [],
                            action: s.action || 'Allow', botName: s.botName || null, botType: s.botType || null,
                            narrative: s.narrative || null, description: s.description || null,
                            topReasons: s.topReasons || [], processingTimeMs: s.processingTimeMs ?? null,
                            lastRequestId: s.lastRequestId || null, hasFullData: true
                        };
                    });
                }
            } catch (e) { }
        },

        async fetchDetections() {
            try {
                const r = await fetch('/_stylobot/api/detections?limit=200');
                if (r.ok) {
                    const items = (toCamel(await r.json()) || []).reverse();
                    items.forEach((d: any) => { this.trackEndpoint(d); this.upsertVisitor(d); });
                }
            } catch (e) { }
        },

        upsertVisitor(d: any) {
            const sig = d.primarySignature || d.requestId || ('anon-' + Math.random().toString(36).slice(2, 8));
            const existing = this.visitorMap[sig];
            if (existing) {
                if (this.initialLoadComplete) existing.hits++;
                existing.lastSeen = d.timestamp || new Date().toISOString();
                existing.isBot = d.isBot;
                existing.botProbability = d.botProbability;
                existing.confidence = d.confidence;
                existing.riskBand = d.riskBand;
                existing.lastPath = d.path;
                existing.action = d.action;
                existing.narrative = d.narrative || existing.narrative;
                existing.description = d.description || existing.description;
                existing.topReasons = d.topReasons?.length ? d.topReasons : existing.topReasons;
                if (d.botName) existing.botName = d.botName;
                if (d.botType) existing.botType = d.botType;
                existing.processingTimeMs = d.processingTimeMs;
                existing.lastRequestId = d.requestId;
                existing.hasFullData = true;
                if (!existing.paths.includes(d.path)) existing.paths.push(d.path);
                if (existing.paths.length > 5) existing.paths.splice(0, existing.paths.length - 5);
            } else {
                this.visitorMap[sig] = {
                    sig, hits: 1,
                    firstSeen: d.timestamp || new Date().toISOString(),
                    lastSeen: d.timestamp || new Date().toISOString(),
                    isBot: d.isBot, botProbability: d.botProbability, confidence: d.confidence,
                    riskBand: d.riskBand, lastPath: d.path, paths: [d.path],
                    action: d.action, botName: d.botName, botType: d.botType,
                    narrative: d.narrative, description: d.description,
                    topReasons: d.topReasons || [], processingTimeMs: d.processingTimeMs,
                    lastRequestId: d.requestId, hasFullData: true
                };
                const keys = Object.keys(this.visitorMap);
                if (keys.length > this.maxVisitors) {
                    const sorted = keys.sort((a, b) => new Date(this.visitorMap[a].lastSeen).getTime() - new Date(this.visitorMap[b].lastSeen).getTime());
                    sorted.slice(0, keys.length - this.maxVisitors).forEach(k => delete this.visitorMap[k]);
                }
            }
        },

        trackEndpoint(d: any) {
            const p = d.path || '/';
            if (!this.endpointStats[p]) this.endpointStats[p] = { total: 0, bots: 0 };
            this.endpointStats[p].total++;
            if (d.isBot) this.endpointStats[p].bots++;
        },

        fetchYourDetection() {
            try {
                const el = document.getElementById('your-detection-data');
                if (el) {
                    const d = JSON.parse(el.textContent || '');
                    if (d) { this.yourData = d; this.probabilityHistory = [d.botProbability]; this.yourLoading = false; return; }
                }
            } catch (e) { }
            this.yourError = 'Detection unavailable';
            this.yourLoading = false;
        },

        async connectSignalR() {
            try {
                await loadSignalR();
                const signalR = (window as any).signalR;
                this.connection = new signalR.HubConnectionBuilder()
                    .withUrl('/_stylobot/hub')
                    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                    .configureLogging(signalR.LogLevel.Warning)
                    .build();

                this.connection.on('BroadcastDetection', (raw: any) => {
                    const d = toCamel(raw);
                    this.trackEndpoint(d);
                    if (!this.feedPaused) { this.upsertVisitor(d); }
                    if (this.signature && d.primarySignature === this.signature && this.yourData) {
                        Object.assign(this.yourData, {
                            isBot: d.isBot, botProbability: d.botProbability, confidence: d.confidence,
                            riskBand: d.riskBand, processingTimeMs: d.processingTimeMs, narrative: d.narrative
                        });
                        this.probabilityHistory.push(d.botProbability);
                        if (this.probabilityHistory.length > 60) this.probabilityHistory.splice(0, this.probabilityHistory.length - 60);
                        this.justUpdated = true;
                        setTimeout(() => this.justUpdated = false, 600);
                    }
                });

                this.connection.on('BroadcastSignature', (raw: any) => {
                    const sig = toCamel(raw);
                    if (this.signature && sig.primarySignature === this.signature) this.hitCount = sig.hitCount;
                    const visitor = this.visitorMap[sig.primarySignature];
                    if (visitor) { visitor.hits = sig.hitCount; if (sig.botName) visitor.botName = sig.botName; }
                });

                this.connection.on('BroadcastSummary', (raw: any) => { this.summary = toCamel(raw); });

                this.connection.on('BroadcastDescriptionUpdate', (requestId: string, description: string) => {
                    const match = Object.values(this.visitorMap).find((v: any) => v.lastRequestId === requestId);
                    if (match) (match as any).description = description;
                });

                this.connection.onclose(() => this.connected = false);
                this.connection.onreconnecting(() => this.connected = false);
                this.connection.onreconnected(() => this.connected = true);

                await this.connection.start();
                this.connected = true;

                const barEl = document.querySelector('.stylobot-header');
                if (barEl && (barEl as any)._x_dataStack) this.signature = (barEl as any)._x_dataStack[0].signature || '';
            } catch (e) {
                console.warn('[LiveDemo] SignalR failed:', e);
            }
        }
    };
}

// ===== Homepage Detection Alpine Component =====

function homeDetection() {
    // Read server-rendered data from JSON element
    const el = document.getElementById('home-detection-data');
    const data = el ? JSON.parse(el.textContent || '{}') : {};

    return {
        isBot: data.isBot ?? false,
        botProbability: data.botProbability ?? 0,
        confidence: data.confidence ?? 0,
        riskBand: data.riskBand ?? 'Unknown',
        processingTimeMs: data.processingTimeMs ?? 0,
        detectorCount: data.detectorCount ?? 0,
        topReason: data.topReason ?? '',

        countryCode: data.countryCode ?? '',
        countryName: data.countryName ?? '',
        city: data.city ?? '',
        countryFlag: countryFlag(data.countryCode ?? '', data.isPrivateNetwork ?? false),
        isVpn: data.isVpn ?? false,
        isHosting: data.isHosting ?? false,

        signature: data.signature ?? '',
        sigFactorCount: data.sigFactorCount ?? 0,
        hasClientFp: data.hasClientFp ?? false,
        hitCount: 1,

        tlsProtocol: data.tlsProtocol ?? '',
        httpProtocol: data.httpProtocol ?? '',

        connected: false,
        justUpdated: false,
        connection: null as any,

        async init() {
            if ((window as any).__mlbotd_done) {
                this.hasClientFp = true;
            }
            document.addEventListener('mlbotd:fingerprint', () => {
                this.hasClientFp = true;
                this.justUpdated = true;
                setTimeout(() => { this.justUpdated = false; }, 500);
            });
            await this.connect();
        },

        async connect() {
            try {
                await loadSignalR();
                const signalR = (window as any).signalR;

                this.connection = new signalR.HubConnectionBuilder()
                    .withUrl('/_stylobot/hub')
                    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                    .configureLogging(signalR.LogLevel.Warning)
                    .build();

                this.connection.on('BroadcastDetection', (raw: any) => {
                    const d = toCamel(raw);
                    if (d.primarySignature === this.signature) {
                        this.isBot = d.isBot;
                        this.botProbability = d.botProbability || 0;
                        this.confidence = d.confidence || 0;
                        this.riskBand = d.riskBand || 'Unknown';
                        this.processingTimeMs = d.processingTimeMs || 0;
                        if (d.topReasons && d.topReasons.length > 0) this.topReason = d.topReasons[0];
                        this.justUpdated = true;
                        setTimeout(() => { this.justUpdated = false; }, 500);
                    }
                });

                this.connection.on('BroadcastSignature', (raw: any) => {
                    const sig = toCamel(raw);
                    if (sig.primarySignature === this.signature) {
                        this.hitCount = sig.hitCount || this.hitCount;
                        this.sigFactorCount = sig.factorCount || this.sigFactorCount;
                        if (sig.clientSideSignature) {
                            this.hasClientFp = true;
                        }
                        this.justUpdated = true;
                        setTimeout(() => { this.justUpdated = false; }, 500);
                    }
                });

                this.connection.onclose(() => { this.connected = false; });
                this.connection.onreconnecting(() => { this.connected = false; });
                this.connection.onreconnected(() => { this.connected = true; });

                await this.connection.start();
                this.connected = true;
            } catch (err) {
                console.warn('[StyloBot Home] SignalR failed:', err);
                this.connected = false;
            }
        }
    };
}

// ===== Register Alpine Components =====

export function registerLiveDemo() {
    Alpine.data('liveDetection', liveDetection);
    Alpine.data('homeDetection', homeDetection);
}
