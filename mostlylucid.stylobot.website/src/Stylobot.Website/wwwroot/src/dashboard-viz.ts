/**
 * Dashboard Command Center visualizations.
 * Renders world map + attack arcs + ApexCharts time series.
 * Connects to SignalR for live attack arc animations.
 * Works alongside HTMX server-rendered partials for data widgets.
 */

import ApexCharts from 'apexcharts';
import { renderWorldMap, type MapDataPoint } from './worldmap';
import { AttackArcRenderer } from './attackarcs';

// ===== Utilities =====

function toCamel(obj: any): any {
    if (Array.isArray(obj)) return obj.map(toCamel);
    if (obj !== null && typeof obj === 'object') {
        return Object.fromEntries(
            Object.entries(obj).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), toCamel(v)])
        );
    }
    return obj;
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
    };
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

// ===== Fetch Helper =====

const fetchOpts: RequestInit = {
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
};

// ===== State =====

let timeChart: ApexCharts | null = null;
let attackArcs: AttackArcRenderer | null = null;
let connection: any = null;
let countries: MapDataPoint[] = [];
let mapRendered = false;

// ===== Map Rendering =====

async function fetchAndRenderMap() {
    const el = document.getElementById('command-map');
    if (!el) return;

    try {
        const res = await fetch('/_stylobot/api/countries?count=50', fetchOpts);
        if (!res.ok) return;
        const raw = toCamel(await res.json());

        countries = raw.map((co: any) => ({
            code: co.countryCode,
            totalCount: co.totalCount || 0,
            botRate: co.botRate || 0,
            label: co.countryName || co.countryCode,
        }));

        renderWorldMap(el, countries, {
            height: 420,
            dark: isDark(),
            onRegionSelected: (code: string) => {
                // Could navigate to country detail in future
                console.log('[CommandCenter] Region selected:', code);
            },
        });
        mapRendered = true;

        // Init attack arc overlay
        attackArcs?.destroy();
        attackArcs = new AttackArcRenderer(el);
    } catch (e) {
        console.warn('[CommandCenter] Failed to load map data:', e);
    }
}

// ===== Time Series Chart =====

async function fetchAndRenderTimeChart() {
    const el = document.getElementById('time-chart');
    if (!el) return;

    try {
        // Default: last 6 hours, 5-minute buckets
        const end = new Date();
        const start = new Date(end.getTime() - 6 * 3600000);
        const url = `/_stylobot/api/timeseries?bucket=300&start=${encodeURIComponent(start.toISOString())}&end=${encodeURIComponent(end.toISOString())}`;
        const res = await fetch(url, fetchOpts);
        if (!res.ok) return;

        const timeData = toCamel(await res.json());
        if (!timeData || timeData.length === 0) return;

        const c = chartColors();
        const categories = timeData.map((d: any) => {
            const t = new Date(d.timestamp || d.bucket);
            return t.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        });

        const opts: ApexCharts.ApexOptions = {
            chart: {
                type: 'area',
                height: 260,
                background: c.bg,
                toolbar: { show: false },
                fontFamily: 'Inter, system-ui, sans-serif',
                animations: { enabled: true, easing: 'easeinout', speed: 400 },
                sparkline: { enabled: false },
            },
            series: [
                { name: 'Humans', data: timeData.map((d: any) => d.humanCount ?? 0) },
                { name: 'Bots', data: timeData.map((d: any) => d.botCount ?? 0) },
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

        if (timeChart) {
            timeChart.updateOptions(opts);
        } else {
            timeChart = new ApexCharts(el, opts);
            timeChart.render();
        }
    } catch (e) {
        console.warn('[CommandCenter] Failed to load time series:', e);
    }
}

// ===== SignalR for Live Attack Arcs =====

async function connectSignalR(hubPath: string) {
    try {
        await loadSignalR();
        const signalR = (window as any).signalR;

        connection = new signalR.HubConnectionBuilder()
            .withUrl(hubPath)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        // Beacon-only: lightweight attack arc signal (countryCode + riskBand only)
        connection.on('BroadcastAttackArc', (countryCode: string, riskBand: string) => {
            if (countryCode && countryCode !== 'XX' && countryCode !== 'LOCAL') {
                const risk = (riskBand === 'High' || riskBand === 'VeryHigh') ? 'high'
                    : (riskBand === 'Medium' || riskBand === 'Elevated') ? 'medium' : 'low';
                attackArcs?.fire(countryCode, risk);
            }
        });

        // Update connection status indicator
        const statusEl = document.getElementById('sb-connection-status');
        function setStatus(state: string) {
            if (!statusEl) return;
            statusEl.className = 'w-2 h-2 rounded-full sb-' + state;
            statusEl.title = 'SignalR: ' + state;
        }

        connection.onclose(() => setStatus('disconnected'));
        connection.onreconnecting(() => setStatus('connecting'));
        connection.onreconnected(() => setStatus('connected'));

        await connection.start();
        setStatus('connected');
    } catch (err) {
        console.warn('[CommandCenter] SignalR failed:', err);
    }
}

// ===== Theme Change Handler =====

function onThemeChange() {
    if (mapRendered) {
        const el = document.getElementById('command-map');
        if (el && countries.length > 0) {
            renderWorldMap(el, countries, {
                height: 420,
                dark: isDark(),
            });
            attackArcs?.destroy();
            attackArcs = new AttackArcRenderer(el);
        }
    }
    if (timeChart) {
        fetchAndRenderTimeChart();
    }
}

// ===== Public Init =====

export async function initCommandCenter(hubPath: string = '/_stylobot/hub') {
    // Only init on pages with the command map
    if (!document.getElementById('command-map')) return;

    // Fetch data and render visualizations in parallel
    await Promise.all([
        fetchAndRenderMap(),
        fetchAndRenderTimeChart(),
    ]);

    // Connect SignalR for live attack arcs
    await connectSignalR(hubPath);

    // Watch for theme changes
    const observer = new MutationObserver(() => onThemeChange());
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
}
