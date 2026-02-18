/**
 * World map renderer using jsvectormap.
 * Proper vector country outlines with region coloring by bot rate
 * and markers sized by request volume.
 */

import jsVectorMap from 'jsvectormap';
import 'jsvectormap/dist/maps/world';
import 'jsvectormap/dist/jsvectormap.css';

export interface MapDataPoint {
    code: string;
    totalCount: number;
    botRate: number;
    label: string;
}

export interface MapOptions {
    width?: number;
    height?: number;
    minRadius?: number;
    maxRadius?: number;
    showLabels?: boolean;
    highlightCode?: string;
    dark?: boolean;
}

// Track instances by container for cleanup (supports multiple maps on page)
const instances = new WeakMap<HTMLElement, any>();
let idCounter = 0;

// Country centroids for marker placement [lat, lng]
const CENTROIDS: Record<string, [number, number]> = {
    US: [39.8, -98.5], CA: [56.1, -106.3], MX: [23.6, -102.5],
    BR: [-14.2, -51.9], AR: [-38.4, -63.6], CL: [-35.7, -71.5], CO: [4.6, -74.3],
    GB: [55.4, -3.4], IE: [53.4, -8.2], FR: [46.2, 2.2], DE: [51.2, 10.4],
    NL: [52.1, 5.3], BE: [50.5, 4.5], CH: [46.8, 8.2], AT: [47.5, 14.6],
    ES: [40.5, -3.7], PT: [39.4, -8.2], IT: [41.9, 12.6],
    SE: [60.1, 18.6], NO: [60.5, 8.5], DK: [56.3, 9.5], FI: [61.9, 25.7],
    PL: [51.9, 19.1], CZ: [49.8, 15.5], HU: [47.2, 19.5], RO: [45.9, 25.0],
    UA: [48.4, 31.2], BG: [42.7, 25.5], GR: [39.1, 21.8],
    RU: [61.5, 105.3], KZ: [48.0, 68.0], TR: [39.0, 35.2],
    IL: [31.0, 34.9], SA: [23.9, 45.1], AE: [23.4, 53.8], IR: [32.4, 53.7],
    IN: [20.6, 79.0], PK: [30.4, 69.3], BD: [23.7, 90.4],
    TH: [15.9, 100.9], VN: [14.1, 108.3], MY: [4.2, 101.9], SG: [1.4, 103.8],
    ID: [-0.8, 113.9], PH: [12.9, 121.8],
    CN: [35.9, 104.2], JP: [36.2, 138.3], KR: [35.9, 127.8], TW: [23.7, 121.0],
    AU: [-25.3, 133.8], NZ: [-40.9, 174.9],
    NG: [9.1, 8.7], EG: [26.8, 30.8], ZA: [-30.6, 22.9], KE: [-0.0, 37.9],
    GH: [7.9, -1.0], MA: [31.8, -7.1], DZ: [28.0, 1.7], TZ: [-6.4, 34.9],
};

/**
 * 3-stop color scale: green (0) -> amber (0.5) -> red (1).
 * jsvectormap only supports 2-stop gradients internally,
 * so we compute per-region colors ourselves.
 */
function botRateHex(rate: number, dark: boolean = false): string {
    const clamped = Math.max(0, Math.min(1, rate));
    let r: number, g: number, b: number;

    // Dark mode uses muted palette, light mode uses vibrant
    if (dark) {
        if (clamped < 0.5) {
            const t = clamped * 2;
            r = Math.round(22 + t * (146 - 22));
            g = Math.round(101 + t * (64 - 101));
            b = Math.round(52 + t * (14 - 52));
        } else {
            const t = (clamped - 0.5) * 2;
            r = Math.round(146 + t * (153 - 146));
            g = Math.round(64 + t * (27 - 64));
            b = Math.round(14 + t * (27 - 14));
        }
    } else {
        if (clamped < 0.5) {
            const t = clamped * 2;
            r = Math.round(34 + t * (245 - 34));
            g = Math.round(197 + t * (158 - 197));
            b = Math.round(94 + t * (11 - 94));
        } else {
            const t = (clamped - 0.5) * 2;
            r = Math.round(245 + t * (239 - 245));
            g = Math.round(158 + t * (68 - 158));
            b = Math.round(11 + t * (68 - 11));
        }
    }
    return '#' + [r, g, b].map(c => c.toString(16).padStart(2, '0')).join('');
}

/**
 * Render a world map into the given container element using jsvectormap.
 */
export function renderWorldMap(container: HTMLElement, data: MapDataPoint[], opts: MapOptions = {}): void {
    const dark = opts.dark ?? false;

    // Destroy previous instance for this container
    const prev = instances.get(container);
    if (prev) {
        try { prev.destroy(); } catch (_) {}
        instances.delete(container);
    }
    container.innerHTML = '';

    // Always render the map — even with no data, the map outline should be visible

    // Unique ID per instance so multiple maps can coexist
    const mapId = 'jsvectormap-' + (++idCounter);
    const mapEl = document.createElement('div');
    mapEl.id = mapId;
    mapEl.style.width = '100%';
    mapEl.style.height = (opts.height ?? 440) + 'px';
    container.appendChild(mapEl);

    // Build data lookup
    const dataMap = new Map<string, MapDataPoint>();
    for (const d of data) dataMap.set(d.code, d);

    // Pre-compute per-region fill colors (3-stop scale computed client-side)
    // setAttributes expects code -> color string (not object)
    const regionAttrs: Record<string, string> = {};
    for (const d of data) {
        regionAttrs[d.code] = botRateHex(d.botRate, dark);
    }

    // Build markers sized by request volume (sqrt scale)
    const maxCount = Math.max(...data.map(d => d.totalCount), 1);
    const minR = opts.minRadius ?? 4;
    const maxR = opts.maxRadius ?? 18;

    const markers = data
        .filter(d => CENTROIDS[d.code])
        .map(d => ({
            name: d.label,
            coords: CENTROIDS[d.code],
            _botRate: d.botRate,
            _totalCount: d.totalCount,
            _scale: Math.sqrt(d.totalCount / maxCount),
        }));

    const map = new jsVectorMap({
        selector: '#' + mapId,
        map: 'world',
        zoomButtons: true,
        zoomOnScroll: true,
        zoomOnScrollSpeed: 3,
        zoomMax: 12,
        zoomMin: 1,
        zoomAnimate: true,
        showTooltip: true,
        backgroundColor: 'transparent',

        regionStyle: {
            initial: {
                fill: dark ? '#1e293b' : '#e2e8f0',
                'fill-opacity': 1,
                stroke: dark ? '#334155' : '#cbd5e1',
                'stroke-width': 0.5,
                'stroke-opacity': 1,
            },
            hover: {
                'fill-opacity': 0.85,
                cursor: 'pointer',
            },
        },

        // Apply per-region colors directly via series attributes
        // (bypasses jsvectormap's 2-stop gradient limitation)
        series: {
            regions: [{
                attribute: 'fill',
                attributes: regionAttrs,
            }],
        },

        markers,

        markerStyle: {
            initial: {
                r: 5,
                fill: dark ? '#94a3b8' : '#64748b',
                'fill-opacity': 0.7,
                stroke: dark ? '#1e293b' : '#ffffff',
                'stroke-width': 1.5,
                'stroke-opacity': 0.9,
            },
            hover: {
                'fill-opacity': 1,
                stroke: dark ? '#e2e8f0' : '#1e293b',
                cursor: 'pointer',
            },
        },

        onRegionTooltipShow(_evt: any, tooltip: any, code: string) {
            const d = dataMap.get(code);
            if (d) {
                const color = botRateHex(d.botRate);
                tooltip.text(
                    `<div style="padding:6px 10px;font-size:12px;line-height:1.5;">` +
                    `<strong>${d.label}</strong><br/>` +
                    `<span style="font-variant-numeric:tabular-nums;">${d.totalCount.toLocaleString()}</span> requests<br/>` +
                    `<span style="color:${color};font-weight:600;">${Math.round(d.botRate * 100)}%</span> bots` +
                    `</div>`,
                    true
                );
            }
        },

        onMarkerTooltipShow(_evt: any, tooltip: any, index: number) {
            const m = markers[index];
            if (m) {
                const color = botRateHex(m._botRate);
                tooltip.text(
                    `<div style="padding:6px 10px;font-size:12px;line-height:1.5;">` +
                    `<strong>${m.name}</strong><br/>` +
                    `<span style="font-variant-numeric:tabular-nums;">${m._totalCount.toLocaleString()}</span> requests<br/>` +
                    `<span style="color:${color};font-weight:600;">${Math.round(m._botRate * 100)}%</span> bots` +
                    `</div>`,
                    true
                );
            }
        },
    });

    // Style markers individually: size by volume, color by bot rate
    if (map._markers) {
        for (const [idx, marker] of Object.entries(map._markers) as [string, any][]) {
            const m = markers[Number(idx)];
            if (m && marker.element) {
                const radius = minR + m._scale * (maxR - minR);
                const color = botRateHex(m._botRate, dark);
                const el = marker.element.shape;
                if (el) {
                    el.node.setAttribute('r', String(radius));
                    el.node.setAttribute('fill', color);
                    el.node.setAttribute('fill-opacity', '0.75');
                }
            }
        }
    }

    instances.set(container, map);
}

/**
 * Render a small map highlighting and focusing on a single country.
 */
export function renderCountryPin(container: HTMLElement, countryCode: string, dark: boolean = false): void {
    if (!countryCode) {
        container.innerHTML = `<div class="text-xs text-center py-4" style="color: var(--sb-card-subtle);">Location unknown</div>`;
        return;
    }

    const code = countryCode.toUpperCase();

    renderWorldMap(container, [{
        code,
        totalCount: 1,
        botRate: 0.5,
        label: code,
    }], {
        height: 250,
        highlightCode: code,
        dark,
    });

    // Focus/zoom the map to the highlighted country
    const map = instances.get(container);
    if (map) {
        try {
            map.setFocus({ region: code, animate: true });
        } catch (_) {
            // Region code may not exist in jsvectormap's world map
        }
    }
}
