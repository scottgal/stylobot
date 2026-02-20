import Alpine from 'alpinejs';
import ApexCharts from 'apexcharts';
import { renderWorldMap, renderCountryPin, type MapDataPoint } from './worldmap';
import { AttackArcRenderer } from './attackarcs';

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

function actionDisplayName(action: string | null | undefined): string {
    const a = (action || 'Allow').toLowerCase();
    if (a === 'allow') return 'Allow';
    if (a.includes('throttle-stealth') || a.includes('tarpit')) return 'Tar Pit';
    if (a.includes('throttle-aggressive')) return 'Hard Throttle';
    if (a.includes('throttle')) return 'Throttle';
    if (a.includes('block-hard')) return 'Hard Block';
    if (a.includes('block')) return 'Block';
    if (a.includes('challenge')) return 'Challenge';
    if (a.includes('redirect-honeypot')) return 'Honeypot';
    if (a.includes('redirect')) return 'Redirect';
    if (a.includes('log') || a.includes('shadow')) return 'Shadow';
    return action || 'Allow';
}

function actionBadgeClass(action: string | null | undefined): string {
    const a = (action || 'Allow').toLowerCase();
    if (a === 'allow') return 'status-badge-human';
    if (a.includes('throttle') || a.includes('tarpit')) return 'status-badge-warn';
    if (a.includes('block')) return 'status-badge-danger';
    if (a.includes('challenge')) return 'status-badge-info';
    if (a.includes('redirect')) return 'status-badge-warn';
    if (a.includes('log') || a.includes('shadow')) return 'status-badge-muted';
    return 'status-badge-warn';
}

const AI_RE = /\bai\b|gpt|claude|llm|chatbot|copilot|gemini|bard|anthropic|perplexity|cohere/i;
const SEARCH_RE = /googlebot|bingbot|yandexbot|baiduspider|duckduckbot|slurp|sogou|exabot|ia_archiver|archive\.org|google|bing/i;
const TOOL_RE = /semrush|ahrefs|mj12|majestic|screaming|dotbot|petalbot|bytespider|yeti|megaindex|serpstat|sistrix|curl|wget|python|go-http|java|ruby|perl|php|node-fetch|axios|scrapy|httpclient|requests|libwww|lwp|mechanize|webdriver|selenium|playwright|puppeteer|phantom|headless|chrome-lighthouse|pagespeed|gtmetrix|pingdom|uptime|monitor|datadog|newrelic|statuspage/i;

// Path-based behavioral patterns for bot type inference
const WP_PATH_RE = /wp-admin|wp-login|wp-content|wp-includes|xmlrpc\.php|wp-json|wp-cron/i;
const CONFIG_PATH_RE = /\.env|\.git|\.aws|\.ssh|\.config|\.htaccess|\.htpasswd|web\.config|appsettings|credentials|\.key|\.pem|\.bak/i;
const EXPLOIT_PATH_RE = /\/shell|\/cmd|\/eval|\/exec|cgi-bin|\/setup|phpunit|vendor\/phpunit|\/debug|\/console|actuator|\/solr|struts|\/ognl|ThinkPHP/i;
const DB_PATH_RE = /phpmyadmin|\/pma|\/mysql|\/adminer|\/dbadmin|\/sql|\/pgadmin|\/mongodb/i;
const API_PATH_RE = /\/graphql|\/swagger|\/openapi|\/api-docs|\/v1\/|\/v2\/|\/rest\//i;
const CMS_PATH_RE = /\/administrator|\/joomla|\/drupal|\/magento|\/shopify|\/typo3|\/umbraco|\/sitecore|\/craft/i;

// UA-based bot identification
const AI_UA_RE = /GPTBot|ChatGPT|CCBot|anthropic-ai|ClaudeBot|Google-Extended|PerplexityBot|Bytespider|Applebot-Extended|cohere-ai|FacebookBot|Meta-ExternalAgent/i;
const SEARCH_UA_RE = /Googlebot|bingbot|YandexBot|Baiduspider|DuckDuckBot|Slurp|Sogou|Applebot(?!-Extended)/i;
const SEO_UA_RE = /SemrushBot|AhrefsBot|MJ12bot|DotBot|PetalBot|MegaIndex|SerpstatBot|Sistrix|Screaming/i;
const MONITOR_UA_RE = /UptimeRobot|Pingdom|Site24x7|StatusCake|Datadog|NewRelic|GTmetrix|PageSpeed|Lighthouse/i;
const PYTHON_UA_RE = /python-requests|python-urllib|python-httpx|aiohttp/i;
const CURL_UA_RE = /^curl\//i;
const WGET_UA_RE = /^wget\//i;
const GO_UA_RE = /Go-http-client|golang/i;
const JAVA_UA_RE = /Java\/|Apache-HttpClient|okhttp/i;
const NODE_UA_RE = /node-fetch|axios|undici/i;
const HEADLESS_UA_RE = /HeadlessChrome|Headless|PhantomJS|Selenium|WebDriver|Playwright|Puppeteer/i;
const CRAWLER_UA_RE = /Scrapy|Nutch|Heritrix/i;

/** Infer bot name and type from behavioral signals when detection didn't provide them. */
function inferBotIdentity(v: any): { name: string | null; type: string | null } {
    // Path-based inference
    const paths = (v.paths || []).join(' ');
    if (WP_PATH_RE.test(paths)) return { name: 'WordPress Scanner', type: 'Scraper' };
    if (CONFIG_PATH_RE.test(paths)) return { name: 'Config Scanner', type: 'Scraper' };
    if (EXPLOIT_PATH_RE.test(paths)) return { name: 'Exploit Scanner', type: 'Scraper' };
    if (DB_PATH_RE.test(paths)) return { name: 'Database Scanner', type: 'Scraper' };
    if (API_PATH_RE.test(paths)) return { name: 'API Prober', type: 'Scraper' };
    if (CMS_PATH_RE.test(paths)) return { name: 'CMS Scanner', type: 'Scraper' };

    // UA-based inference
    const ua = v.userAgent || '';
    if (ua) {
        if (AI_UA_RE.test(ua)) return { name: extractUaBotName(ua) || 'AI Crawler', type: 'AiBot' };
        if (SEARCH_UA_RE.test(ua)) return { name: extractUaBotName(ua) || 'Search Bot', type: 'SearchEngine' };
        if (SEO_UA_RE.test(ua)) return { name: extractUaBotName(ua) || 'SEO Crawler', type: 'Scraper' };
        if (MONITOR_UA_RE.test(ua)) return { name: extractUaBotName(ua) || 'Monitor', type: 'MonitoringBot' };
        if (PYTHON_UA_RE.test(ua)) return { name: 'Python Bot', type: 'Scraper' };
        if (CURL_UA_RE.test(ua)) return { name: 'curl', type: 'Scraper' };
        if (WGET_UA_RE.test(ua)) return { name: 'wget', type: 'Scraper' };
        if (GO_UA_RE.test(ua)) return { name: 'Go Bot', type: 'Scraper' };
        if (JAVA_UA_RE.test(ua)) return { name: 'Java Bot', type: 'Scraper' };
        if (NODE_UA_RE.test(ua)) return { name: 'Node.js Bot', type: 'Scraper' };
        if (CRAWLER_UA_RE.test(ua)) return { name: 'Web Crawler', type: 'Scraper' };
        if (HEADLESS_UA_RE.test(ua)) return { name: 'Headless Browser', type: 'Scraper' };
    }

    // Rate-based inference
    const hits = v.hitCount || v.hits || 0;
    if (hits > 10 && v.firstSeen && v.lastSeen) {
        const secs = (new Date(v.lastSeen).getTime() - new Date(v.firstSeen).getTime()) / 1000;
        if (secs > 0) {
            const rpm = hits / secs * 60;
            if (rpm > 60) return { name: 'Aggressive Crawler', type: 'Scraper' };
            if (rpm > 20) return { name: 'Fast Crawler', type: 'Scraper' };
        }
    }

    return { name: 'Unknown Bot', type: null };
}

function extractUaBotName(ua: string): string | null {
    let m = ua.match(/compatible;\s*([A-Za-z][\w-]+)/i);
    if (m) return m[1];
    m = ua.match(/^([A-Za-z][\w-]+)\/[\d.]/i);
    if (m) return m[1];
    return null;
}

function inferBotCategory(v: any): string {
    const t = v.botType || '';
    if (t === 'AiBot') return 'ai';
    if (t === 'SearchEngine' || t === 'VerifiedBot' || t === 'GoodBot') return 'search';
    if (t === 'Scraper' || t === 'MonitoringBot' || t === 'SocialMediaBot') return 'tools';
    // Infer from name when type is missing
    const n = v.botName || '';
    if (n && AI_RE.test(n)) return 'ai';
    if (n && SEARCH_RE.test(n)) return 'search';
    if (n && TOOL_RE.test(n)) return 'tools';
    // Also check UA directly for AI bots
    const ua = v.userAgent || '';
    if (ua && AI_UA_RE.test(ua)) return 'ai';
    if (ua && SEARCH_UA_RE.test(ua)) return 'search';
    if (ua && (SEO_UA_RE.test(ua) || MONITOR_UA_RE.test(ua) || PYTHON_UA_RE.test(ua) ||
        CURL_UA_RE.test(ua) || GO_UA_RE.test(ua) || HEADLESS_UA_RE.test(ua))) return 'tools';
    return 'other';
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
        // Detector-first order: ua.family is what UserAgentContributor writes;
        // ua.browser is what EnrichFromRequest writes (when EnrichHumanSignals=true)
        const browser = sigs['ua.family'] || sigs['ua.browser'] || sigs['ua.browser_family'];
        const version = sigs['ua.family_version'] || sigs['ua.browser_version'] || sigs['ua.version'] || sigs['ua.major_version'];
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

        // Protocol — check detector signals (h2.fingerprint, h2.settings_hash) AND
        // enriched protocol signals (h2.protocol, h3.protocol) from DetectionBroadcastMiddleware
        if (sigs['h3.protocol'] || sigs['h3.version']) stats.protocols['HTTP/3'] = (stats.protocols['HTTP/3'] || 0) + 1;
        else if (sigs['h2.fingerprint'] || sigs['h2.settings_hash'] || sigs['h2.protocol']) stats.protocols['HTTP/2'] = (stats.protocols['HTTP/2'] || 0) + 1;
        else stats.protocols['HTTP/1.1'] = (stats.protocols['HTTP/1.1'] || 0) + 1;

        // TLS — tls.protocol is what TlsFingerprintContributor writes (SignalKeys.TlsProtocol)
        const tlsVer = sigs['tls.protocol'] || sigs['tls.version'] || sigs['tls.protocol_version'];
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
    { prefix: 'verifiedbot.', label: 'Verified Bot', icon: '\u2705' },
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
        tab: 'overview' as 'overview' | 'visitors' | 'clusters' | 'countries' | 'useragents',
        connected: false,
        connection: null as any,

        summary: null as any,
        yourDetection: null as any,
        yourSignature: '' as string,
        topBots: [] as any[],

        timeChart: null as ApexCharts | null,
        timeData: [] as any[],
        timeRange: 'All' as string,

        visitors: [] as any[],
        visitorFilter: 'all',
        visitorPage: 1,
        visitorPageSize: 50,
        visitorHasMore: true,
        visitorLoading: false,

        clusters: [] as any[],

        countries: [] as any[],
        countryChart: null as ApexCharts | null,
        worldMapRendered: false,
        overviewMapRendered: false,

        selectedCountry: null as string | null,
        countryDetail: null as any,
        countryDetailLoading: false,

        // WarGames attack arc renderers
        _attackArcMap: null as AttackArcRenderer | null,
        _attackArcOverview: null as AttackArcRenderer | null,
        threatView: true,

        useragents: [] as any[],
        uaFilter: 'all',
        selectedUA: null as any,
        uaChart: null as ApexCharts | null,

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
                const tsParams = this.getTimeSeriesParams();
                let tsUrl = `/_stylobot/api/timeseries?bucket=${tsParams.bucket}`;
                if (tsParams.start) tsUrl += `&start=${encodeURIComponent(tsParams.start)}`;
                if (tsParams.end) tsUrl += `&end=${encodeURIComponent(tsParams.end)}`;
                const [summaryRes, timeRes, meRes, topBotsRes, countriesRes] = await Promise.all([
                    fetch('/_stylobot/api/summary'),
                    fetch(tsUrl),
                    fetch('/_stylobot/api/me'),
                    fetch('/_stylobot/api/topbots?count=10'),
                    fetch('/_stylobot/api/countries?count=50'),
                ]);

                if (summaryRes.ok) this.summary = toCamel(await summaryRes.json());
                if (timeRes.ok) this.timeData = toCamel(await timeRes.json());
                if (meRes.ok) {
                    const raw = await meRes.text();
                    if (raw && raw !== 'null') {
                        const parsed = toCamel(JSON.parse(raw));
                        if (parsed.isBot !== undefined || parsed.botProbability !== undefined) {
                            this.yourDetection = parsed;
                            this.yourSignature = parsed.signature || '';
                        } else if (parsed.signature) {
                            this.yourSignature = parsed.signature;
                            // Signature only — visitor cache wasn't populated yet.
                            // Retry after a short delay to pick up cached detection data.
                            this.retryFetchMe();
                        }
                    }
                }
                if (topBotsRes.ok) {
                    this.topBots = toCamel(await topBotsRes.json());
                }
                if (countriesRes.ok) {
                    this.countries = toCamel(await countriesRes.json());
                }

                this.$nextTick(() => {
                    this.renderTimeChart();
                    this.renderOverviewMap();
                });
            } catch (e) {
                console.warn('[Dashboard] Failed to load overview:', e);
            }
        },

        async retryFetchMe(attempt = 0) {
            const delays = [2000, 5000];
            if (attempt >= delays.length || this.yourDetection) return;
            await new Promise(r => setTimeout(r, delays[attempt]));
            if (this.yourDetection) return; // SignalR may have populated it
            try {
                const res = await fetch('/_stylobot/api/me');
                if (res.ok) {
                    const parsed = toCamel(await res.json());
                    if (parsed.isBot !== undefined || parsed.botProbability !== undefined) {
                        this.yourDetection = parsed;
                        this.yourSignature = parsed.signature || '';
                        return;
                    }
                }
            } catch { /* ignore */ }
            this.retryFetchMe(attempt + 1);
        },

        getTimeSeriesParams(): { bucket: number; start?: string; end?: string } {
            const now = new Date();
            const rangeMap: Record<string, { hours: number; bucket: number }> = {
                '1h': { hours: 1, bucket: 60 },
                '6h': { hours: 6, bucket: 300 },
                '12h': { hours: 12, bucket: 600 },
                '24h': { hours: 24, bucket: 1800 },
                '7d': { hours: 168, bucket: 3600 },
                '30d': { hours: 720, bucket: 86400 },
            };
            const cfg = rangeMap[this.timeRange];
            if (!cfg) {
                // "All" — use a 90-day window with hourly buckets; TimescaleDB time_bucket handles this efficiently
                const start = new Date(now.getTime() - 90 * 24 * 3600000);
                return { bucket: 3600, start: start.toISOString(), end: now.toISOString() };
            }
            const start = new Date(now.getTime() - cfg.hours * 3600000);
            return { bucket: cfg.bucket, start: start.toISOString(), end: now.toISOString() };
        },

        async setTimeRange(range: string) {
            this.timeRange = range;
            const tsParams = this.getTimeSeriesParams();
            try {
                let url = `/_stylobot/api/timeseries?bucket=${tsParams.bucket}`;
                if (tsParams.start) url += `&start=${encodeURIComponent(tsParams.start)}`;
                if (tsParams.end) url += `&end=${encodeURIComponent(tsParams.end)}`;
                const timeRes = await fetch(url);
                if (timeRes.ok) {
                    this.timeData = toCamel(await timeRes.json());
                    this.$nextTick(() => this.renderTimeChart());
                }

                // Refresh top bots with time filter
                let botsUrl = '/_stylobot/api/topbots?count=10';
                if (tsParams.start) botsUrl += `&start=${encodeURIComponent(tsParams.start)}`;
                if (tsParams.end) botsUrl += `&end=${encodeURIComponent(tsParams.end)}`;
                const botsRes = await fetch(botsUrl);
                if (botsRes.ok) {
                    this.topBots = toCamel(await botsRes.json());
                }

                // Refresh countries with time filter
                await this.loadCountries();
            } catch (e) {
                console.warn('[Dashboard] Failed to update time range:', e);
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

        async loadVisitors(page?: number) {
            if (this.visitorLoading) return;
            this.visitorLoading = true;
            try {
                if (page !== undefined) this.visitorPage = page;
                const offset = (this.visitorPage - 1) * this.visitorPageSize;
                // Fetch one extra to detect if there's a next page
                const res = await fetch(`/_stylobot/api/signatures?limit=${this.visitorPageSize + 1}&offset=${offset}${this.visitorApiParams}`);
                if (res.ok) {
                    const raw = toCamel(await res.json());
                    this.visitorHasMore = raw.length > this.visitorPageSize;
                    const visitors = raw.slice(0, this.visitorPageSize);
                    for (const v of visitors) {
                        if (v.isKnownBot && !v.botName) {
                            const identity = inferBotIdentity(v);
                            if (identity.name) v.botName = identity.name;
                            if (identity.type) v.botType = identity.type;
                        }
                    }
                    this.visitors = visitors;
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load visitors:', e);
            } finally {
                this.visitorLoading = false;
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
                let url = '/_stylobot/api/countries?count=50';
                const tsParams = this.getTimeSeriesParams();
                if (this.timeRange !== 'All' && tsParams.start) {
                    url += `&start=${encodeURIComponent(tsParams.start)}`;
                    if (tsParams.end) url += `&end=${encodeURIComponent(tsParams.end)}`;
                }
                const res = await fetch(url);
                if (res.ok) {
                    this.countries = toCamel(await res.json());
                    this.$nextTick(() => {
                        this.renderCountryChart();
                        this.renderWorldMapChart();
                    });
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load countries:', e);
            }
        },

        async selectCountry(code: string) {
            if (this.selectedCountry === code) {
                this.clearCountryDetail();
                return;
            }
            this.selectedCountry = code;
            this.countryDetail = null;
            this.countryDetailLoading = true;
            try {
                let url = `/_stylobot/api/countries/${encodeURIComponent(code)}`;
                const tsParams = this.getTimeSeriesParams();
                if (this.timeRange !== 'All' && tsParams.start) {
                    url += `?start=${encodeURIComponent(tsParams.start)}`;
                    if (tsParams.end) url += `&end=${encodeURIComponent(tsParams.end)}`;
                }
                const res = await fetch(url);
                if (res.ok) {
                    this.countryDetail = toCamel(await res.json());
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load country detail:', e);
            } finally {
                this.countryDetailLoading = false;
            }
        },

        clearCountryDetail() {
            this.selectedCountry = null;
            this.countryDetail = null;
            this.countryDetailLoading = false;
        },

        async loadUserAgents() {
            try {
                const res = await fetch('/_stylobot/api/useragents');
                if (res.ok) {
                    this.useragents = toCamel(await res.json());
                }
            } catch (e) {
                console.warn('[Dashboard] Failed to load user agents:', e);
            }
        },

        get filteredUAs(): any[] {
            if (this.uaFilter === 'all') return this.useragents;
            return this.useragents.filter((ua: any) => ua.category === this.uaFilter);
        },

        get uaStats() {
            const uas = this.useragents;
            return {
                total: uas.length,
                browsers: uas.filter((u: any) => u.category === 'browser').length,
                bots: uas.filter((u: any) => u.category === 'search' || u.category === 'ai').length,
                tools: uas.filter((u: any) => u.category === 'tool').length,
            };
        },

        setUAFilter(f: string) {
            this.uaFilter = f;
            this.selectedUA = null;
            if (this.uaChart) { this.uaChart.destroy(); this.uaChart = null; }
        },

        selectUA(ua: any) {
            this.selectedUA = this.selectedUA?.family === ua.family ? null : ua;
            this.$nextTick(() => this.renderUAVersionChart());
        },

        renderUAVersionChart() {
            const el = document.getElementById('ua-version-chart');
            if (!el || !this.selectedUA) return;
            const versions = this.selectedUA.versions || {};
            const entries = Object.entries(versions)
                .sort((a: any, b: any) => b[1] - a[1])
                .slice(0, 8) as [string, number][];
            if (entries.length === 0) {
                if (this.uaChart) { this.uaChart.destroy(); this.uaChart = null; }
                return;
            }
            const c = chartColors();
            const opts: ApexCharts.ApexOptions = {
                chart: { type: 'donut', height: 200, background: c.bg, fontFamily: 'Inter, system-ui, sans-serif' },
                series: entries.map(([_, count]) => count),
                labels: entries.map(([ver]) => 'v' + ver),
                colors: [c.accent, c.human, c.bot, c.warn, c.confidence, c.uncertain, '#6366f1', '#ec4899'],
                legend: { position: 'right', labels: { colors: c.text }, fontSize: '11px' },
                tooltip: { theme: isDark() ? 'dark' : 'light' },
                dataLabels: { enabled: true, style: { fontSize: '10px' } },
                plotOptions: { pie: { donut: { size: '55%' } } },
                stroke: { show: false },
            };
            if (this.uaChart) {
                this.uaChart.updateOptions(opts);
            } else {
                this.uaChart = new ApexCharts(el, opts);
                this.uaChart.render();
            }
        },

        uaCategoryBadge(cat: string): string {
            if (cat === 'browser') return 'status-badge-human';
            if (cat === 'search') return 'status-badge-info';
            if (cat === 'ai') return 'status-badge-warn';
            if (cat === 'tool') return 'status-badge-muted';
            return 'status-badge-muted';
        },

        uaCategoryLabel(cat: string): string {
            if (cat === 'browser') return 'Browser';
            if (cat === 'search') return 'Search';
            if (cat === 'ai') return 'AI';
            if (cat === 'tool') return 'Tool';
            return 'Unknown';
        },

        topVersion(ua: any): string {
            const v = ua.versions || {};
            const entries = Object.entries(v);
            if (entries.length === 0) return '\u2014';
            entries.sort((a: any, b: any) => b[1] - a[1]);
            return 'v' + entries[0][0];
        },

        topCountry(ua: any): string {
            const c = ua.countries || {};
            const entries = Object.entries(c);
            if (entries.length === 0) return '\u2014';
            entries.sort((a: any, b: any) => b[1] - a[1]);
            return countryFlag(entries[0][0] as string) + ' ' + entries[0][0];
        },

        async switchTab(t: string) {
            this.tab = t as any;
            if (t === 'visitors') await this.loadVisitors();
            if (t === 'clusters' && this.clusters.length === 0) await this.loadClusters();
            if (t === 'countries') {
                if (this.countries.length === 0) await this.loadCountries();
                else this.$nextTick(() => { this.renderCountryChart(); this.renderWorldMapChart(); });
            }
            if (t === 'useragents' && this.useragents.length === 0) await this.loadUserAgents();
        },

        // ===== Visitor Filtering & Sorting =====

        setVisitorFilter(id: string) {
            this.visitorFilter = id;
            this.visitorPage = 1;
            this.loadVisitors();
        },

        async visitorPrev() {
            if (this.visitorPage > 1) await this.loadVisitors(this.visitorPage - 1);
        },

        async visitorNext() {
            if (this.visitorHasMore) await this.loadVisitors(this.visitorPage + 1);
        },

        get visitorApiParams(): string {
            const f = this.visitorFilter;
            if (f === 'bots') return '&isBot=true';
            if (f === 'humans') return '&isBot=false';
            return '';
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

                this.connection.on('BroadcastSignature', (raw: any) => {
                    const sig = toCamel(raw);
                    const idx = this.visitors.findIndex((v: any) => v.primarySignature === sig.primarySignature);
                    if (idx >= 0) {
                        this.visitors[idx] = { ...this.visitors[idx], ...sig };
                    }
                    // Rebuild top bots: update existing or insert new
                    this.updateTopBots(sig);

                    // Update "your detection" if this signature matches the current visitor
                    if (this.yourSignature && sig.primarySignature === this.yourSignature) {
                        this.yourDetection = {
                            signature: this.yourSignature,
                            isBot: sig.isKnownBot || sig.isBot || false,
                            botProbability: sig.botProbability || 0,
                            confidence: sig.confidence || 0,
                            riskBand: sig.riskBand || 'Unknown',
                            processingTimeMs: sig.processingTimeMs || 0,
                            narrative: sig.narrative || '',
                            topReasons: sig.topReasons || [],
                        };
                    }
                });

                this.connection.on('BroadcastClusters', (raw: any) => {
                    this.clusters = toCamel(raw);
                });

                this.connection.on('BroadcastCountries', (raw: any) => {
                    // Only auto-update when viewing "All" time range (no custom filter active)
                    if (this.timeRange === 'All') {
                        const updated = toCamel(raw);
                        // Add flag to each entry
                        for (const c of updated) {
                            if (!c.flag) c.flag = countryFlag(c.countryCode);
                        }
                        this.countries = updated;
                        this.$nextTick(() => {
                            if (this.tab === 'countries') {
                                this.renderCountryChart();
                                this.renderWorldMapChart();
                            }
                            if (this.tab === 'overview' && this.overviewMapRendered) {
                                this.renderOverviewMap();
                            }
                        });
                    }
                });

                this.connection.on('BroadcastSignatureDescriptionUpdate', (signature: string, name: string, description: string) => {
                    const idx = this.visitors.findIndex((v: any) => v.primarySignature === signature);
                    if (idx >= 0) {
                        this.visitors[idx].botName = name;
                        this.visitors[idx].description = description;
                    }
                    const botIdx = this.topBots.findIndex((b: any) => b.primarySignature === signature);
                    if (botIdx >= 0) {
                        this.topBots[botIdx].botName = name;
                        this.topBots[botIdx].description = description;
                    }
                });

                this.connection.on('BroadcastClusterDescriptionUpdate', (clusterId: string, label: string, description: string) => {
                    const idx = this.clusters.findIndex((c: any) => c.clusterId === clusterId);
                    if (idx >= 0) {
                        this.clusters[idx].label = label;
                        this.clusters[idx].description = description;
                    }
                });

                this.connection.on('BroadcastDescriptionUpdate', (requestId: string, description: string) => {
                    const det = this.recentDetections.find((d: any) => d.requestId === requestId);
                    if (det) det.description = description;
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

        updateTopBots(sig: any) {
            if (!sig.isKnownBot && !sig.isBot) return;

            // Infer identity if missing
            if (!sig.botName) {
                const identity = inferBotIdentity(sig);
                if (identity.name) sig.botName = identity.name;
                if (identity.type) sig.botType = identity.type;
            }

            const botIdx = this.topBots.findIndex((b: any) => b.primarySignature === sig.primarySignature);
            if (botIdx >= 0) {
                this.topBots[botIdx] = { ...this.topBots[botIdx], ...sig };
            } else {
                // New bot — add it
                this.topBots.push(sig);
            }
            // Re-sort by hit count and trim to top 10
            this.topBots.sort((a: any, b: any) => (b.hitCount ?? 0) - (a.hitCount ?? 0));
            if (this.topBots.length > 10) this.topBots.length = 10;
        },

        flushDetectionBatch() {
            const batch = this._detectionBatch.splice(0);
            if (batch.length === 0) return;

            // Process all accumulated detections in one pass
            for (const d of batch) {
                // Skip static assets from live feed
                if (isStaticAsset(d)) continue;

                // Infer bot identity from behavior when detection didn't provide it
                if (d.isBot && !d.botName) {
                    const identity = inferBotIdentity(d);
                    if (identity.name) d.botName = identity.name;
                    if (identity.type) d.botType = identity.type;
                }

                this.recentDetections.unshift(d);

                // Update visitor in list if exists
                const idx = this.visitors.findIndex((v: any) => v.primarySignature === d.primarySignature);
                if (idx >= 0) {
                    const existing = this.visitors[idx];
                    this.visitors[idx] = { ...existing, ...d };
                    // Re-infer if name was "Unknown Bot" and we now have more paths
                    if (this.visitors[idx].botName === 'Unknown Bot' || !this.visitors[idx].botName) {
                        const identity = inferBotIdentity(this.visitors[idx]);
                        if (identity.name && identity.name !== 'Unknown Bot') {
                            this.visitors[idx].botName = identity.name;
                            if (identity.type) this.visitors[idx].botType = identity.type;
                        }
                    }
                }

                // Update "you" if matches — create full detection from event if we only had signature
                const sigMatch = this.yourSignature && d.primarySignature === this.yourSignature;
                if (sigMatch && d.isBot !== undefined) {
                    const update = {
                        signature: this.yourSignature,
                        isBot: d.isBot ?? false,
                        botProbability: d.botProbability || 0,
                        confidence: d.confidence || 0,
                        riskBand: d.riskBand || 'Unknown',
                        processingTimeMs: d.processingTimeMs || 0,
                        narrative: d.narrative || (this.yourDetection?.narrative ?? ''),
                        topReasons: d.topReasons || (this.yourDetection?.topReasons ?? []),
                    };
                    this.yourDetection = this.yourDetection
                        ? { ...this.yourDetection, ...update }
                        : update;
                }
            }

            // Trim detections list once
            if (this.recentDetections.length > 100) {
                this.recentDetections.length = 100;
            }

            // Re-aggregate signal stats from updated detections
            this.signalStats = aggregateSignalStats(this.recentDetections);

            // Fire attack arcs for bot detections with country codes
            if (this.threatView) {
                for (const d of batch) {
                    if (d.isBot && d.countryCode && d.countryCode !== 'XX' && d.countryCode !== 'LOCAL') {
                        const risk = (d.riskBand === 'High' || d.riskBand === 'VeryHigh') ? 'high'
                            : (d.riskBand === 'Medium' || d.riskBand === 'Elevated') ? 'medium' : 'low';
                        this._attackArcMap?.fire(d.countryCode, risk);
                        this._attackArcOverview?.fire(d.countryCode, risk);
                    }
                }
            }
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

        renderWorldMapChart() {
            const el = document.getElementById('world-map');
            if (!el) return;

            const mapData: MapDataPoint[] = this.countries.map((co: any) => ({
                code: co.countryCode,
                totalCount: co.totalCount || 0,
                botRate: co.botRate || 0,
                label: co.countryName || co.countryCode,
            }));

            renderWorldMap(el, mapData, {
                dark: isDark(),
                onRegionSelected: (code: string) => this.selectCountry(code),
            });
            this.worldMapRendered = true;

            // Init attack arc overlay
            this._attackArcMap?.destroy();
            this._attackArcMap = new AttackArcRenderer(el);
        },

        renderOverviewMap() {
            const el = document.getElementById('overview-world-map');
            if (!el) return;

            const mapData: MapDataPoint[] = this.countries.map((co: any) => ({
                code: co.countryCode,
                totalCount: co.totalCount || 0,
                botRate: co.botRate || 0,
                label: co.countryName || co.countryCode,
            }));

            renderWorldMap(el, mapData, {
                height: 300,
                dark: isDark(),
                onRegionSelected: (code: string) => {
                    this.switchTab('countries');
                    this.$nextTick(() => this.selectCountry(code));
                },
            });
            this.overviewMapRendered = true;

            // Init attack arc overlay
            this._attackArcOverview?.destroy();
            this._attackArcOverview = new AttackArcRenderer(el);
        },

        onThemeChange() {
            if (this.timeChart) this.renderTimeChart();
            if (this.countryChart) this.renderCountryChart();
            if (this.worldMapRendered) this.renderWorldMapChart();
            if (this.overviewMapRendered) this.renderOverviewMap();
            if (this.uaChart) this.renderUAVersionChart();
        },

        // ===== Time range display helpers =====

        get timeRangeLabel(): string {
            const labels: Record<string, string> = {
                '1h': 'Last Hour', '6h': 'Last 6 Hours', '12h': 'Last 12 Hours',
                '24h': 'Last 24 Hours', '7d': 'Last 7 Days', '30d': 'Last 30 Days', 'All': 'All Time',
            };
            return labels[this.timeRange] || this.timeRange;
        },

        get displaySummary(): any {
            if (this.timeRange === 'All' || !this.timeData || this.timeData.length === 0) return this.summary;
            // Compute summary from time-series data for the selected range
            let totalRequests = 0, botRequests = 0, humanRequests = 0;
            for (const bucket of this.timeData) {
                totalRequests += (bucket.totalCount ?? 0);
                botRequests += (bucket.botCount ?? 0);
                humanRequests += (bucket.humanCount ?? 0);
            }
            return {
                ...this.summary,
                totalRequests,
                botRequests,
                humanRequests,
                botPercentage: totalRequests > 0 ? (botRequests / totalRequests * 100) : 0,
            };
        },

        // ===== Helpers exposed to template =====
        pct,
        riskClass,
        riskColor,
        countryFlag,
        timeAgo,
        actionBadgeClass,
        actionDisplayName,
        inferBotCategory,
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
        countryCode: '' as string,

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

            // Construct sig from detections if no signature/topbot record exists
            if (!this.sig && this.detections.length > 0) {
                const latest = this.detections[0];
                this.sig = {
                    primarySignature: this.signature,
                    hitCount: this.detections.length,
                    botProbability: latest.botProbability ?? 0,
                    confidence: latest.confidence ?? 0,
                    riskBand: latest.riskBand || 'Unknown',
                    isKnownBot: latest.isBot ?? false,
                    botType: latest.botType,
                    botName: latest.botName,
                    action: latest.action || latest.policyName,
                    lastPath: latest.path,
                    narrative: latest.narrative,
                    description: latest.description,
                    topReasons: latest.topReasons || [],
                    timestamp: latest.timestamp,
                    countryCode: latest.countryCode,
                };
            }

            // Update sig fields from most recent detection (signature cache may be stale)
            if (this.sig && this.detections.length > 0) {
                const latest = this.detections[0];
                // These always reflect the latest detection state
                this.sig.botProbability = latest.botProbability ?? this.sig.botProbability;
                this.sig.confidence = latest.confidence ?? this.sig.confidence;
                this.sig.riskBand = latest.riskBand || this.sig.riskBand;
                this.sig.isKnownBot = latest.isBot ?? this.sig.isKnownBot;
                this.sig.action = latest.action || latest.policyName || this.sig.action;
                // These fill gaps only
                if (!this.sig.botType) this.sig.botType = latest.botType;
                if (!this.sig.botName) this.sig.botName = latest.botName;
                if (!this.sig.lastPath) this.sig.lastPath = latest.path;
                if (!this.sig.narrative) this.sig.narrative = latest.narrative;
                if (!this.sig.topReasons || this.sig.topReasons.length === 0) this.sig.topReasons = latest.topReasons;
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

            // Extract country code from sig or latest detection signals
            if (this.sig?.countryCode) {
                this.countryCode = this.sig.countryCode;
            } else if (this.detections.length > 0) {
                const sigs = this.detections[0].importantSignals || {};
                this.countryCode = this.detections[0].countryCode || sigs['geo.country_code'] || sigs['geo.country'] || '';
            }

            this.loading = false;

            this.$nextTick(() => {
                this.renderProbChart();
                this.renderTimingChart();
                this.renderLocationMap();
            });

            await this.connectSignalR();

            const observer = new MutationObserver(() => this.onThemeChange());
            observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        },

        async loadSignature() {
            try {
                // Try signatures endpoint first
                const res = await fetch('/_stylobot/api/signatures?limit=1000');
                if (res.ok) {
                    const sigs = toCamel(await res.json());
                    this.sig = sigs.find((s: any) => s.primarySignature === this.signature) || null;
                }
            } catch (_) {}

            // Fallback: try topbots API (queries detections grouped by signature)
            if (!this.sig) {
                try {
                    const res = await fetch('/_stylobot/api/topbots?count=50');
                    if (res.ok) {
                        const bots = toCamel(await res.json());
                        this.sig = bots.find((b: any) => b.primarySignature === this.signature) || null;
                    }
                } catch (_) {}
            }
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

                        // Update sig with latest detection values
                        if (this.sig) {
                            this.sig.botProbability = d.botProbability ?? this.sig.botProbability;
                            this.sig.confidence = d.confidence ?? this.sig.confidence;
                            this.sig.riskBand = d.riskBand || this.sig.riskBand;
                            this.sig.isKnownBot = d.isBot ?? this.sig.isKnownBot;
                            this.sig.action = d.action || d.policyName || this.sig.action;
                            this.sig.lastPath = d.path || this.sig.lastPath;
                            this.sig.timestamp = d.timestamp || this.sig.timestamp;
                        }

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

                // Live signature updates (hit count, bot name, description)
                this.connection.on('BroadcastSignature', (raw: any) => {
                    const s = toCamel(raw);
                    if (s.primarySignature === this.signature && this.sig) {
                        this.sig.hitCount = s.hitCount ?? this.sig.hitCount;
                        this.sig.botName = s.botName || this.sig.botName;
                        this.sig.botType = s.botType || this.sig.botType;
                        this.sig.botProbability = s.botProbability ?? this.sig.botProbability;
                        this.sig.confidence = s.confidence ?? this.sig.confidence;
                        this.sig.riskBand = s.riskBand || this.sig.riskBand;
                        this.sig.isKnownBot = s.isKnownBot ?? this.sig.isKnownBot;
                    }
                });

                // Live description updates from LLM
                this.connection.on('BroadcastSignatureDescriptionUpdate', (sigId: string, name: string, description: string) => {
                    if (sigId === this.signature && this.sig) {
                        this.sig.botName = name || this.sig.botName;
                        this.sig.narrative = description || this.sig.narrative;
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
                    height: 100,
                    background: c.bg,
                    toolbar: { show: false },
                    fontFamily: 'Inter, system-ui, sans-serif',
                },
                series: [{ name: 'Detection Time', data: timings.map((v: number) => Math.max(0.1, Math.round(v * 100) / 100)) }],
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
                    logarithmic: true,
                    labels: { style: { colors: c.text, fontSize: '10px' }, formatter: (v: number) => v < 1 ? v.toFixed(1) + 'ms' : Math.round(v) + 'ms' },
                },
                grid: { borderColor: c.grid, strokeDashArray: 3 },
                tooltip: {
                    theme: isDark() ? 'dark' : 'light',
                    y: { formatter: (v: number) => v.toFixed(2) + 'ms (detection only, excludes action delays)' },
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

        renderLocationMap() {
            const el = document.getElementById('location-map');
            if (!el || !this.countryCode) return;
            renderCountryPin(el, this.countryCode, isDark());
        },

        onThemeChange() {
            if (this.probChart) this.renderProbChart();
            if (this.timingChart) this.renderTimingChart();
            this.renderLocationMap();
        },

        // ===== Bot Score Trend (computed from sparkline history) =====

        get botScoreTrend(): 'up' | 'down' | 'flat' {
            const h = this.sparklineData?.botProbabilityHistory as number[] | undefined;
            if (!h || h.length < 3) return 'flat';
            // Compare average of last 3 values vs previous 3 values for stability
            const recent = h.slice(-3);
            const prior = h.slice(-6, -3);
            if (prior.length === 0) return 'flat';
            const recentAvg = recent.reduce((a, b) => a + b, 0) / recent.length;
            const priorAvg = prior.reduce((a, b) => a + b, 0) / prior.length;
            const delta = recentAvg - priorAvg;
            if (delta > 0.03) return 'up';
            if (delta < -0.03) return 'down';
            return 'flat';
        },

        get botScoreTrendArrow(): string {
            const t = this.botScoreTrend;
            if (t === 'up') return '\u2197';   // arrow upper-right (getting more bot-like)
            if (t === 'down') return '\u2198';  // arrow lower-right (getting more human-like)
            return '\u2192';                    // arrow right (stable)
        },

        get botScoreTrendColor(): string {
            const t = this.botScoreTrend;
            if (t === 'up') return 'color: var(--sb-signal-danger, #ef4444)';
            if (t === 'down') return 'color: var(--sb-signal-pos, #22c55e)';
            return 'color: var(--sb-card-subtle)';
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
        actionBadgeClass,
        actionDisplayName,
        categorizeSignals,

        /**
         * Color-code detector contribution: green (human) → yellow (neutral) → red (bot).
         * Contribution is typically -0.5 to +0.5 range.
         */
        detectorBorderColor(contribution: number): string {
            const v = Math.max(-0.5, Math.min(0.5, contribution ?? 0));
            if (v < -0.05) {
                // Human indicator: green
                const t = Math.min(1, Math.abs(v) * 4);
                return `rgb(${Math.round(34 + (1 - t) * 200)}, ${Math.round(197 - (1 - t) * 50)}, ${Math.round(94 - (1 - t) * 30)})`;
            } else if (v > 0.05) {
                // Bot indicator: red
                const t = Math.min(1, v * 4);
                return `rgb(${Math.round(239)}, ${Math.round(68 + (1 - t) * 120)}, ${Math.round(68 + (1 - t) * 120)})`;
            }
            // Neutral: amber/yellow
            return '#eab308';
        },
    };
}

// ===== Registration =====

export function registerDashboard() {
    Alpine.data('dashboardApp', dashboardApp);
    Alpine.data('signatureDetailApp', signatureDetailApp);
}
