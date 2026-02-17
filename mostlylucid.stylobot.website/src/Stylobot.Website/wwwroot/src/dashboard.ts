import Alpine from 'alpinejs';
import ApexCharts from 'apexcharts';

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

function countryFlag(code: string): string {
    if (!code || code.length !== 2 || code.toUpperCase() === 'XX') return '\uD83C\uDF10';
    return String.fromCodePoint(...[...code.toUpperCase()].map(c => 0x1F1E6 + c.charCodeAt(0) - 65));
}

function riskColor(band: string): string {
    const b = (band || '').toLowerCase();
    if (b === 'verylow' || b === 'low' || b === 'verified') return 'var(--sb-signal-pos)';
    if (b === 'medium' || b === 'elevated') return 'var(--sb-signal-warn)';
    if (b === 'high' || b === 'veryhigh') return 'var(--sb-signal-danger)';
    return 'var(--sb-card-subtle)';
}

function riskClass(band: string): string {
    const b = (band || '').toLowerCase();
    if (b === 'verylow' || b === 'low') return 'signal-positive';
    if (b === 'verified') return 'hero-accent-text';
    if (b === 'medium' || b === 'elevated') return 'signal-warning';
    if (b === 'high' || b === 'veryhigh') return 'signal-danger';
    return 'signal-muted';
}

function pct(v: number | null | undefined): string {
    if (v == null) return '\u2014';
    return Math.round(v * 100) + '%';
}

function isDark(): boolean {
    return document.documentElement.getAttribute('data-theme') === 'dark';
}

function chartColors() {
    const dark = isDark();
    return {
        bg: 'transparent',
        text: dark ? '#8ea0b5' : '#5f7187',
        grid: dark ? 'rgba(148,163,184,0.1)' : 'rgba(15,23,42,0.06)',
        human: dark ? '#86b59c' : '#15803d',
        bot: dark ? '#dc2626' : '#b91c1c',
        warn: dark ? '#d97706' : '#b45309',
        accent: dark ? '#5ba3a3' : '#0f766e',
        uncertain: dark ? '#6b7280' : '#94a3b8',
        confidence: dark ? '#a78bfa' : '#7c3aed',
    };
}

function timeAgo(iso: string): string {
    if (!iso) return '';
    const diff = Date.now() - new Date(iso).getTime();
    const sec = Math.floor(diff / 1000);
    if (sec < 60) return sec + 's ago';
    const min = Math.floor(sec / 60);
    if (min < 60) return min + 'm ago';
    const hr = Math.floor(min / 60);
    return hr + 'h ago';
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

// ===== Debounce utility =====

function debounce<T extends (...args: any[]) => void>(fn: T, ms: number): T {
    let timer: ReturnType<typeof setTimeout> | null = null;
    return ((...args: any[]) => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => fn(...args), ms);
    }) as any as T;
}

// ===== Signal Stats Aggregation =====

interface SignalStats {
    browsers: Record<string, number>;
    browserVersions: Record<string, Record<string, number>>; // browser -> {version -> count}
    protocols: Record<string, number>;
    tlsVersions: Record<string, number>;
    riskBands: Record<string, number>;
    botTypes: Record<string, number>;
}

function aggregateSignalStats(detections: any[]): SignalStats {
    const stats: SignalStats = {
        browsers: {},
        browserVersions: {},
        protocols: {},
        tlsVersions: {},
        riskBands: {},
        botTypes: {},
    };

    for (const d of detections) {
        // Risk band counts
        const rb = d.riskBand || 'Unknown';
        stats.riskBands[rb] = (stats.riskBands[rb] || 0) + 1;

        // Bot type
        if (d.botType) {
            stats.botTypes[d.botType] = (stats.botTypes[d.botType] || 0) + 1;
        }

        const sigs = d.importantSignals || {};

        // Browser from UA signals
        const browser = sigs['ua.browser'] || sigs['ua.browser_family'];
        const version = sigs['ua.browser_version'] || sigs['ua.version'] || sigs['ua.major_version'];
        if (browser) {
            const b = String(browser);
            stats.browsers[b] = (stats.browsers[b] || 0) + 1;
            if (version) {
                if (!stats.browserVersions[b]) stats.browserVersions[b] = {};
                const v = String(version);
                stats.browserVersions[b][v] = (stats.browserVersions[b][v] || 0) + 1;
            }
        } else {
            // Try to infer from ua signals
            let inferred = '';
            if (sigs['ua.is_chrome']) inferred = 'Chrome';
            else if (sigs['ua.is_firefox']) inferred = 'Firefox';
            else if (sigs['ua.is_safari']) inferred = 'Safari';
            else if (sigs['ua.is_edge']) inferred = 'Edge';
            if (inferred) {
                stats.browsers[inferred] = (stats.browsers[inferred] || 0) + 1;
                if (version) {
                    if (!stats.browserVersions[inferred]) stats.browserVersions[inferred] = {};
                    const v = String(version);
                    stats.browserVersions[inferred][v] = (stats.browserVersions[inferred][v] || 0) + 1;
                }
            }
        }

        // Protocol
        if (sigs['h3.protocol'] || sigs['h3.version']) stats.protocols['HTTP/3'] = (stats.protocols['HTTP/3'] || 0) + 1;
        else if (sigs['h2.fingerprint'] || sigs['h2.settings_hash']) stats.protocols['HTTP/2'] = (stats.protocols['HTTP/2'] || 0) + 1;
        else stats.protocols['HTTP/1.1'] = (stats.protocols['HTTP/1.1'] || 0) + 1;

        // TLS
        const tlsVer = sigs['tls.version'] || sigs['tls.protocol_version'];
        if (tlsVer) {
            const t = String(tlsVer);
            stats.tlsVersions[t] = (stats.tlsVersions[t] || 0) + 1;
        }
    }

    return stats;
}

function topN(record: Record<string, number>, n: number): [string, number][] {
    return Object.entries(record)
        .sort((a, b) => b[1] - a[1])
        .slice(0, n);
}

// ===== Static Asset Detection =====

const STATIC_EXTENSIONS = /\.(js|css|svg|png|jpg|jpeg|gif|ico|woff2?|ttf|eot|map|webp|avif)$/i;

function isStaticAsset(d: any): boolean {
    const path = d.path || d.lastPath || '';
    if (STATIC_EXTENSIONS.test(path)) return true;
    // 0% confidence + sub-0.5ms = static file middleware shortcut
    if ((d.confidence === 0 || d.confidence == null) && (d.processingTimeMs || 0) < 0.5) return true;
    return false;
}

// ===== Page Load Grouping =====

interface PageLoadGroup {
    detection: any;        // The main page request
    assetCount: number;    // Number of associated static assets
    assets: string[];      // Asset paths (for tooltip/expansion)
}

function groupPageLoads(detections: any[]): PageLoadGroup[] {
    const groups: PageLoadGroup[] = [];
    const sorted = [...detections].sort((a, b) =>
        new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()
    );

    const used = new Set<number>();

    for (let i = 0; i < sorted.length; i++) {
        if (used.has(i)) continue;
        const d = sorted[i];

        if (isStaticAsset(d)) continue; // skip orphan assets

        used.add(i);
        const ts = new Date(d.timestamp).getTime();
        const assets: string[] = [];

        // Gather static assets within 3 seconds after this page request
        for (let j = i + 1; j < sorted.length; j++) {
            if (used.has(j)) continue;
            const a = sorted[j];
            const ats = new Date(a.timestamp).getTime();
            if (ts - ats > 3000) break; // sorted desc, so earlier items are further back
            if (isStaticAsset(a)) {
                used.add(j);
                assets.push(a.path || a.lastPath || '?');
            }
        }

        groups.push({ detection: d, assetCount: assets.length, assets });
    }

    return groups;
}

// ===== Signal Categorization for Detail Pages =====

interface SignalCategory {
    label: string;
    icon: string;
    signals: [string, any][];
}

const SIGNAL_CATEGORIES: { prefix: string; label: string; icon: string }[] = [
    { prefix: 'ua.', label: 'User Agent', icon: '\uD83C\uDF10' },
    { prefix: 'header.', label: 'HTTP Headers', icon: '\uD83D\uDCE8' },
    { prefix: 'h2.', label: 'HTTP/2 Fingerprint', icon: '\u26A1' },
    { prefix: 'h3.', label: 'HTTP/3', icon: '\uD83D\uDE80' },
    { prefix: 'tls.', label: 'TLS Fingerprint', icon: '\uD83D\uDD12' },
    { prefix: 'tcp.', label: 'TCP/IP Fingerprint', icon: '\uD83D\uDD0C' },
    { prefix: 'client.', label: 'Client-Side', icon: '\uD83D\uDDA5\uFE0F' },
    { prefix: 'behavioral.', label: 'Behavioral', icon: '\uD83D\uDCC8' },
    { prefix: 'geo.', label: 'Geographic', icon: '\uD83D\uDDFA\uFE0F' },
    { prefix: 'ip.', label: 'IP Intelligence', icon: '\uD83D\uDCCD' },
    { prefix: 'cluster.', label: 'Cluster Analysis', icon: '\uD83E\uDDE9' },
    { prefix: 'reputation.', label: 'Reputation', icon: '\u2B50' },
    { prefix: 'detection.', label: 'Detection Meta', icon: '\uD83E\uDD16' },
    { prefix: 'request.', label: 'Request', icon: '\uD83D\uDCE1' },
    { prefix: 'honeypot.', label: 'Honeypot', icon: '\uD83C\uDF6F' },
    { prefix: 'similarity.', label: 'Similarity', icon: '\uD83D\uDD0D' },
    { prefix: 'ts.', label: 'TimescaleDB History', icon: '\uD83D\uDCC5' },
];

function categorizeSignals(signals: Record<string, any>): SignalCategory[] {
    const categories: SignalCategory[] = [];
    const entries = Object.entries(signals);
    const used = new Set<string>();

    for (const cat of SIGNAL_CATEGORIES) {
        const matching = entries.filter(([k]) => k.startsWith(cat.prefix));
        if (matching.length > 0) {
            categories.push({ label: cat.label, icon: cat.icon, signals: matching });
            matching.forEach(([k]) => used.add(k));
        }
    }

    // Uncategorized signals
    const uncategorized = entries.filter(([k]) => !used.has(k));
    if (uncategorized.length > 0) {
        categories.push({ label: 'Other', icon: '\u2139\uFE0F', signals: uncategorized });
    }

    return categories;
}

// ===== Dashboard App (main page) =====

function dashboardApp() {
    return {
        tab: 'overview' as 'overview' | 'visitors' | 'clusters' | 'countries',
        connected: false,
        connection: null as any,

        summary: null as any,
        yourDetection: null as any,
        topBots: [] as any[],

        timeChart: null as ApexCharts | null,
        timeData: [] as any[],

        visitors: [] as any[],
        visitorFilter: 'all',
        visitorSort: 'timestamp',
        visitorSortDir: 'desc',

        clusters: [] as any[],

        countries: [] as any[],
        countryChart: null as ApexCharts | null,

        recentDetections: [] as any[],

        // Signal stats aggregated from detections
        signalStats: null as SignalStats | null,
        selectedBrowser: '' as string,
        browserChart: null as ApexCharts | null,

        // SignalR batching: accumulate detections, flush on debounce
        _detectionBatch: [] as any[],
        _flushDetections: null as (() => void) | null,

        async init() {
            await this.loadOverview();
            await this.loadDetections();
            await this.connectSignalR();
            const observer = new MutationObserver(() => this.onThemeChange());
            observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

            // Debounced flush for batched SignalR detections (250ms window)
            this._flushDetections = debounce(() => this.flushDetectionBatch(), 250);
        },

        // ===== Data Loading =====

        async loadOverview() {
            try {
                const [summaryRes, timeRes, meRes, sigsRes] = await Promise.all([
                    fetch('/_stylobot/api/summary'),
                    fetch('/_stylobot/api/timeseries?bucket=60'),
                    fetch('/_stylobot/api/me'),
                    fetch('/_stylobot/api/signatures?limit=50'),
                ]);

                if (summaryRes.ok) this.summary = toCamel(await summaryRes.json());
                if (timeRes.ok) this.timeData = toCamel(await timeRes.json());
                if (meRes.ok) {
                    const raw = await meRes.text();
                    if (raw && raw !== 'null') {
                        this.yourDetection = toCamel(JSON.parse(raw));
                    }
                }
                if (sigsRes.ok) {
                    const sigs = toCamel(await sigsRes.json());
                    this.topBots = sigs
                        .filter((s: any) => s.isKnownBot)
                        .sort((a: any, b: any) => (b.hitCount ?? 0) - (a.hitCount ?? 0))
                        .slice(0, 10);
                }

                this.$nextTick(() => this.renderTimeChart());
            } catch (e) {
                console.warn('[Dashboard] Failed to load overview:', e);
            }
        },

        async loadDetections() {
            try {
                const res = await fetch('/_stylobot/api/detections?limit=50');
                if (res.ok) {
                    const all = toCamel(await res.json());
                    this.recentDetections = all.filter((d: any) => !isStaticAsset(d));
                    this.signalStats = aggregateSignalStats(this.recentDetections);
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load detections:', e);
            }
        },

        async loadVisitors() {
            try {
                const res = await fetch('/_stylobot/api/signatures?limit=100');
                if (res.ok) this.visitors = toCamel(await res.json());
            } catch (e) {
                console.warn('[Dashboard] Failed to load visitors:', e);
            }
        },

        async loadClusters() {
            try {
                const res = await fetch('/_stylobot/api/clusters');
                if (res.ok) this.clusters = toCamel(await res.json());
            } catch (e) {
                console.warn('[Dashboard] Failed to load clusters:', e);
            }
        },

        async loadCountries() {
            try {
                const res = await fetch('/_stylobot/api/countries?count=20');
                if (res.ok) {
                    this.countries = toCamel(await res.json());
                    this.$nextTick(() => this.renderCountryChart());
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load countries:', e);
            }
        },

        async switchTab(t: string) {
            this.tab = t as any;
            if (t === 'visitors' && this.visitors.length === 0) await this.loadVisitors();
            if (t === 'clusters' && this.clusters.length === 0) await this.loadClusters();
            if (t === 'countries' && this.countries.length === 0) await this.loadCountries();
        },

        // ===== Visitor Filtering & Sorting =====

        get filteredVisitors() {
            let list = this.visitors;
            if (this.visitorFilter === 'bots') list = list.filter((v: any) => v.isKnownBot);
            else if (this.visitorFilter === 'humans') list = list.filter((v: any) => !v.isKnownBot);
            else if (this.visitorFilter === 'ai') list = list.filter((v: any) => v.botType === 'AI');
            else if (this.visitorFilter === 'tools') list = list.filter((v: any) => v.botType === 'Tool' || v.botType === 'Scraper');

            const dir = this.visitorSortDir === 'asc' ? 1 : -1;
            const key = this.visitorSort;
            return [...list].sort((a: any, b: any) => {
                if (key === 'botProbability' || key === 'hitCount') return ((a[key] ?? 0) - (b[key] ?? 0)) * dir;
                if (key === 'timestamp') return (new Date(a[key] || 0).getTime() - new Date(b[key] || 0).getTime()) * dir;
                return String(a[key] || '').localeCompare(String(b[key] || '')) * dir;
            });
        },

        filterCount(type: string): number {
            if (type === 'all') return this.visitors.length;
            if (type === 'bots') return this.visitors.filter((v: any) => v.isKnownBot).length;
            if (type === 'humans') return this.visitors.filter((v: any) => !v.isKnownBot).length;
            if (type === 'ai') return this.visitors.filter((v: any) => v.botType === 'AI').length;
            if (type === 'tools') return this.visitors.filter((v: any) => v.botType === 'Tool' || v.botType === 'Scraper').length;
            return 0;
        },

        visitorUrl(v: any): string {
            return '/dashboard/signature/' + encodeURIComponent(v.primarySignature || '');
        },

        // ===== Signal Stats Helpers =====

        get topBrowsers(): [string, number][] {
            return this.signalStats ? topN(this.signalStats.browsers, 6) : [];
        },

        get topProtocols(): [string, number][] {
            return this.signalStats ? topN(this.signalStats.protocols, 4) : [];
        },

        get riskDistribution(): [string, number][] {
            return this.signalStats ? topN(this.signalStats.riskBands, 8) : [];
        },

        get selectedBrowserVersions(): [string, number][] {
            if (!this.signalStats || !this.selectedBrowser) return [];
            const versions = this.signalStats.browserVersions[this.selectedBrowser];
            return versions ? topN(versions, 10) : [];
        },

        selectBrowser(name: string) {
            this.selectedBrowser = this.selectedBrowser === name ? '' : name;
            this.$nextTick(() => this.renderBrowserChart());
        },

        renderBrowserChart() {
            const el = document.getElementById('browser-version-chart');
            if (!el) return;
            const data = this.selectedBrowserVersions;
            if (data.length === 0) {
                if (this.browserChart) { this.browserChart.destroy(); this.browserChart = null; }
                return;
            }
            const c = chartColors();
            const opts: ApexCharts.ApexOptions = {
                chart: { type: 'donut', height: 180, background: c.bg, fontFamily: 'Inter, system-ui, sans-serif' },
                series: data.map(([_, count]) => count),
                labels: data.map(([ver]) => 'v' + ver),
                colors: [c.accent, c.human, c.bot, c.warn, c.confidence, c.uncertain],
                legend: { position: 'right', labels: { colors: c.text }, fontSize: '11px' },
                tooltip: { theme: isDark() ? 'dark' : 'light' },
                dataLabels: { enabled: true, style: { fontSize: '10px' } },
                plotOptions: { pie: { donut: { size: '55%' } } },
                stroke: { show: false },
            };
            if (this.browserChart) {
                this.browserChart.updateOptions(opts);
            } else {
                this.browserChart = new ApexCharts(el, opts);
                this.browserChart.render();
            }
        },

        // ===== SignalR with Batching =====

        async connectSignalR() {
            try {
                await loadSignalR();
                const signalR = (window as any).signalR;

                this.connection = new signalR.HubConnectionBuilder()
                    .withUrl('/_stylobot/hub')
                    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                    .configureLogging(signalR.LogLevel.Warning)
                    .build();

                // Batch detections: accumulate during bursts, flush after 250ms quiet
                this.connection.on('BroadcastDetection', (raw: any) => {
                    this._detectionBatch.push(toCamel(raw));
                    if (this._flushDetections) this._flushDetections();
                });

                this.connection.on('BroadcastSummary', (raw: any) => {
                    this.summary = toCamel(raw);
                });

                this.connection.on('BroadcastClusters', (raw: any) => {
                    this.clusters = toCamel(raw);
                });

                this.connection.onclose(() => { this.connected = false; });
                this.connection.onreconnecting(() => { this.connected = false; });
                this.connection.onreconnected(() => { this.connected = true; });

                await this.connection.start();
                this.connected = true;
            } catch (err) {
                console.warn('[Dashboard] SignalR failed:', err);
                this.connected = false;
            }
        },

        flushDetectionBatch() {
            const batch = this._detectionBatch.splice(0);
            if (batch.length === 0) return;

            // Process all accumulated detections in one pass
            for (const d of batch) {
                // Skip static assets from live feed
                if (isStaticAsset(d)) continue;

                this.recentDetections.unshift(d);

                // Update visitor in list if exists
                const idx = this.visitors.findIndex((v: any) => v.primarySignature === d.primarySignature);
                if (idx >= 0) {
                    this.visitors[idx] = { ...this.visitors[idx], ...d };
                }

                // Update "you" if matches
                if (this.yourDetection && d.primarySignature === this.yourDetection.signature) {
                    this.yourDetection = {
                        ...this.yourDetection,
                        isBot: d.isBot,
                        botProbability: d.botProbability || 0,
                        confidence: d.confidence || 0,
                        riskBand: d.riskBand || 'Unknown',
                        processingTimeMs: d.processingTimeMs || 0,
                    };
                }
            }

            // Trim detections list once
            if (this.recentDetections.length > 100) {
                this.recentDetections.length = 100;
            }

            // Re-aggregate signal stats from updated detections
            this.signalStats = aggregateSignalStats(this.recentDetections);
        },

        // ===== Charts =====

        renderTimeChart() {
            const el = document.getElementById('time-chart');
            if (!el || this.timeData.length === 0) return;

            const c = chartColors();
            const categories = this.timeData.map((d: any) => {
                const t = new Date(d.timestamp || d.bucket);
                return t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            });

            const opts: ApexCharts.ApexOptions = {
                chart: {
                    type: 'area',
                    height: 280,
                    background: c.bg,
                    toolbar: { show: false },
                    fontFamily: 'Inter, system-ui, sans-serif',
                    animations: { enabled: true, easing: 'easeinout', speed: 400 },
                },
                series: [
                    { name: 'Humans', data: this.timeData.map((d: any) => d.humanCount ?? 0) },
                    { name: 'Bots', data: this.timeData.map((d: any) => d.botCount ?? 0) },
                ],
                colors: [c.human, c.bot],
                fill: {
                    type: 'gradient',
                    gradient: { shadeIntensity: 1, opacityFrom: 0.35, opacityTo: 0.05, stops: [0, 100] },
                },
                stroke: { curve: 'smooth', width: 2 },
                xaxis: {
                    categories,
                    labels: { style: { colors: c.text, fontSize: '10px' }, rotate: -45, rotateAlways: false },
                    axisBorder: { show: false },
                    axisTicks: { show: false },
                },
                yaxis: {
                    labels: { style: { colors: c.text, fontSize: '11px' } },
                },
                grid: { borderColor: c.grid, strokeDashArray: 3 },
                legend: {
                    position: 'top',
                    horizontalAlign: 'right',
                    labels: { colors: c.text },
                    markers: { size: 5, shape: 'circle' as any },
                },
                tooltip: { theme: isDark() ? 'dark' : 'light' },
                dataLabels: { enabled: false },
            };

            if (this.timeChart) {
                this.timeChart.updateOptions(opts);
            } else {
                this.timeChart = new ApexCharts(el, opts);
                this.timeChart.render();
            }
        },

        renderCountryChart() {
            const el = document.getElementById('country-chart');
            if (!el || this.countries.length === 0) return;

            const c = chartColors();
            const top10 = this.countries.slice(0, 10);

            const opts: ApexCharts.ApexOptions = {
                chart: {
                    type: 'bar',
                    height: 320,
                    background: c.bg,
                    toolbar: { show: false },
                    fontFamily: 'Inter, system-ui, sans-serif',
                },
                series: [{ name: 'Bot Rate', data: top10.map((co: any) => Math.round((co.botRate ?? 0) * 100)) }],
                plotOptions: {
                    bar: { horizontal: true, borderRadius: 3, barHeight: '65%' },
                },
                colors: [c.accent],
                xaxis: {
                    labels: { style: { colors: c.text, fontSize: '11px' }, formatter: (v: any) => v + '%' },
                    max: 100,
                },
                yaxis: {
                    labels: {
                        style: { colors: c.text, fontSize: '11px' },
                        formatter: (_val: any, i: any) => {
                            const co = top10[typeof i === 'number' ? i : i?.dataPointIndex ?? 0];
                            return co ? `${countryFlag(co.countryCode)} ${co.countryName || co.countryCode}` : '';
                        },
                    },
                },
                grid: { borderColor: c.grid, strokeDashArray: 3 },
                tooltip: { theme: isDark() ? 'dark' : 'light' },
                dataLabels: { enabled: false },
            };

            if (this.countryChart) {
                this.countryChart.updateOptions(opts);
            } else {
                this.countryChart = new ApexCharts(el, opts);
                this.countryChart.render();
            }
        },

        onThemeChange() {
            if (this.timeChart) this.renderTimeChart();
            if (this.countryChart) this.renderCountryChart();
        },

        // ===== Helpers exposed to template =====
        pct,
        riskClass,
        riskColor,
        countryFlag,
        timeAgo,
    };
}

// ===== Signature Detail App =====

function signatureDetailApp() {
    return {
        signature: '' as string,
        connected: false,
        connection: null as any,
        loading: true,

        sig: null as any,
        sparklineData: null as any,
        detections: [] as any[],
        _allDetections: [] as any[],
        _groupedDetections: [] as any[],

        probChart: null as ApexCharts | null,
        timingChart: null as ApexCharts | null,

        // Debounced sparkline refresh
        _refreshTimer: null as ReturnType<typeof setTimeout> | null,

        async init() {
            this.signature = (this.$el as HTMLElement).dataset.signature || '';
            if (!this.signature) return;

            await Promise.all([
                this.loadSignature(),
                this.loadSparkline(),
                this.loadDetections(),
            ]);

            // Fill missing sig fields from most recent detection
            if (this.sig && this.detections.length > 0) {
                const latest = this.detections[0];
                if (this.sig.botProbability == null) this.sig.botProbability = latest.botProbability;
                if (this.sig.confidence == null) this.sig.confidence = latest.confidence;
                if (!this.sig.botType) this.sig.botType = latest.botType;
                if (!this.sig.action) this.sig.action = latest.action || latest.policyName;
                if (!this.sig.lastPath) this.sig.lastPath = latest.path;
            }

            // Build sparkline from detections if API returned nothing
            if (!this.sparklineData && this.detections.length > 1) {
                const reversed = [...this.detections].reverse();
                this.sparklineData = {
                    botProbabilityHistory: reversed.map((d: any) => d.botProbability ?? 0),
                    confidenceHistory: reversed.map((d: any) => d.confidence ?? 0),
                    processingTimeHistory: reversed.map((d: any) => d.processingTimeMs ?? 0),
                };
            }

            this.loading = false;

            this.$nextTick(() => {
                this.renderProbChart();
                this.renderTimingChart();
            });

            await this.connectSignalR();

            const observer = new MutationObserver(() => this.onThemeChange());
            observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        },

        async loadSignature() {
            try {
                const res = await fetch('/_stylobot/api/signatures?limit=1000');
                if (res.ok) {
                    const sigs = toCamel(await res.json());
                    this.sig = sigs.find((s: any) => s.primarySignature === this.signature) || null;
                }
            } catch (_) {}
        },

        async loadSparkline() {
            try {
                const res = await fetch(`/_stylobot/api/sparkline/${encodeURIComponent(this.signature)}`);
                if (res.ok) {
                    this.sparklineData = toCamel(await res.json());
                }
            } catch (_) {}
        },

        async loadDetections() {
            try {
                const res = await fetch(`/_stylobot/api/detections?limit=50&signatureId=${encodeURIComponent(this.signature)}`);
                if (res.ok) {
                    const all = toCamel(await res.json());
                    // Keep all detections for grouping, separate page loads from assets
                    this._allDetections = all;
                    this.detections = all.filter((d: any) => !isStaticAsset(d));
                    this._groupedDetections = groupPageLoads(all);
                }
            } catch (_) {}
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
                    if (d.primarySignature === this.signature) {
                        this._allDetections.unshift(d);
                        if (this._allDetections.length > 50) this._allDetections.length = 50;

                        if (!isStaticAsset(d)) {
                            this.detections.unshift(d);
                            if (this.detections.length > 50) this.detections.length = 50;
                        }

                        // Re-group page loads
                        this._groupedDetections = groupPageLoads(this._allDetections);

                        // Debounce sparkline refresh (500ms) to avoid hammering API during bursts
                        if (this._refreshTimer) clearTimeout(this._refreshTimer);
                        this._refreshTimer = setTimeout(() => {
                            this.loadSparkline().then(() => {
                                this.$nextTick(() => {
                                    this.renderProbChart();
                                    this.renderTimingChart();
                                });
                            });
                        }, 500);
                    }
                });

                this.connection.onclose(() => { this.connected = false; });
                this.connection.onreconnecting(() => { this.connected = false; });
                this.connection.onreconnected(() => { this.connected = true; });

                await this.connection.start();
                this.connected = true;
            } catch (err) {
                console.warn('[SignatureDetail] SignalR failed:', err);
                this.connected = false;
            }
        },

        // ===== Charts =====

        renderProbChart() {
            const el = document.getElementById('prob-history-chart');
            if (!el || !this.sparklineData) return;

            const probHistory = this.sparklineData.botProbabilityHistory || [];
            const confHistory = this.sparklineData.confidenceHistory || [];
            if (probHistory.length === 0 && confHistory.length === 0) return;

            const c = chartColors();
            const len = Math.max(probHistory.length, confHistory.length);
            const categories = Array.from({ length: len }, (_, i) => String(i + 1));

            const opts: ApexCharts.ApexOptions = {
                chart: {
                    type: 'area',
                    height: 220,
                    background: c.bg,
                    toolbar: { show: false },
                    fontFamily: 'Inter, system-ui, sans-serif',
                    animations: { enabled: true, easing: 'easeinout', speed: 300 },
                },
                series: [
                    { name: 'Bot Probability', data: probHistory.map((v: number) => Math.round(v * 100)) },
                    { name: 'Confidence', data: confHistory.map((v: number) => Math.round(v * 100)) },
                ],
                colors: [c.bot, c.confidence],
                fill: {
                    type: 'gradient',
                    gradient: { shadeIntensity: 1, opacityFrom: 0.3, opacityTo: 0.05, stops: [0, 100] },
                },
                stroke: { curve: 'smooth', width: 2 },
                xaxis: {
                    categories,
                    labels: { show: false },
                    axisBorder: { show: false },
                    axisTicks: { show: false },
                },
                yaxis: {
                    min: 0,
                    max: 100,
                    labels: { style: { colors: c.text, fontSize: '10px' }, formatter: (v: number) => v + '%' },
                },
                grid: { borderColor: c.grid, strokeDashArray: 3 },
                legend: {
                    position: 'top',
                    horizontalAlign: 'right',
                    labels: { colors: c.text },
                    markers: { size: 5, shape: 'circle' as any },
                },
                tooltip: {
                    theme: isDark() ? 'dark' : 'light',
                    y: { formatter: (v: number) => v + '%' },
                },
                dataLabels: { enabled: false },
            };

            if (this.probChart) {
                this.probChart.updateOptions(opts);
            } else {
                this.probChart = new ApexCharts(el, opts);
                this.probChart.render();
            }
        },

        renderTimingChart() {
            const el = document.getElementById('timing-history-chart');
            if (!el || !this.sparklineData) return;

            const timings = this.sparklineData.processingTimeHistory || [];
            if (timings.length === 0) return;

            const c = chartColors();
            const categories = Array.from({ length: timings.length }, (_, i) => String(i + 1));

            const opts: ApexCharts.ApexOptions = {
                chart: {
                    type: 'bar',
                    height: 160,
                    background: c.bg,
                    toolbar: { show: false },
                    fontFamily: 'Inter, system-ui, sans-serif',
                },
                series: [{ name: 'Processing Time', data: timings.map((v: number) => Math.round(v * 100) / 100) }],
                colors: [c.accent],
                plotOptions: {
                    bar: { borderRadius: 2, columnWidth: '70%' },
                },
                xaxis: {
                    categories,
                    labels: { show: false },
                    axisBorder: { show: false },
                    axisTicks: { show: false },
                },
                yaxis: {
                    labels: { style: { colors: c.text, fontSize: '10px' }, formatter: (v: number) => v + 'ms' },
                },
                grid: { borderColor: c.grid, strokeDashArray: 3 },
                tooltip: {
                    theme: isDark() ? 'dark' : 'light',
                    y: { formatter: (v: number) => v + 'ms' },
                },
                dataLabels: { enabled: false },
            };

            if (this.timingChart) {
                this.timingChart.updateOptions(opts);
            } else {
                this.timingChart = new ApexCharts(el, opts);
                this.timingChart.render();
            }
        },

        onThemeChange() {
            if (this.probChart) this.renderProbChart();
            if (this.timingChart) this.renderTimingChart();
        },

        // ===== Signal Categories (computed) =====

        get signalCategories(): SignalCategory[] {
            if (this.detections.length === 0) return [];
            const latest = this.detections[0];
            if (!latest.importantSignals || Object.keys(latest.importantSignals).length === 0) return [];
            return categorizeSignals(latest.importantSignals);
        },

        // ===== Helpers =====
        pct,
        riskClass,
        riskColor,
        countryFlag,
        timeAgo,
        categorizeSignals,
    };
}

// ===== Registration =====

export function registerDashboard() {
    Alpine.data('dashboardApp', dashboardApp);
    Alpine.data('signatureDetailApp', signatureDetailApp);
}
