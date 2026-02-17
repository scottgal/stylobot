/**
 * Lightweight SVG world map renderer using country centroids.
 * Equirectangular projection on a 1000x500 viewBox.
 * No external dependencies — pure SVG.
 */

// Country centroids: [x, y] in equirectangular projection (1000x500 viewBox)
// Formula: x = (lng + 180) / 360 * 1000, y = (90 - lat) / 180 * 500
const CENTROIDS: Record<string, [number, number]> = {
    // North America
    US: [228, 142], CA: [233, 94], MX: [197, 185],

    // Central America & Caribbean
    GT: [200, 204], HN: [210, 204], CR: [215, 217], PA: [223, 222],
    CU: [222, 190], JM: [227, 199], DO: [241, 199], PR: [246, 199],

    // South America
    BR: [367, 267], AR: [337, 339], CL: [314, 327], CO: [267, 220],
    VE: [260, 213], PE: [280, 255], EC: [264, 241], UY: [347, 333],
    PY: [340, 305], BO: [310, 282],

    // Western Europe
    GB: [494, 103], IE: [486, 101], FR: [506, 122], DE: [528, 108],
    NL: [514, 103], BE: [512, 109], LU: [515, 112], CH: [520, 119],
    AT: [532, 119], ES: [493, 136], PT: [484, 135], IT: [531, 130],

    // Northern Europe
    SE: [540, 78], NO: [528, 78], DK: [525, 96], FI: [556, 72],
    IS: [468, 72],

    // Eastern Europe
    PL: [542, 104], CZ: [535, 111], HU: [542, 119], RO: [551, 122],
    BG: [551, 130], UA: [563, 111], BY: [556, 101], SK: [541, 115],
    HR: [538, 124], RS: [545, 126], BA: [540, 126], ME: [542, 130],
    AL: [543, 134], MK: [546, 133], MD: [559, 118], LT: [551, 96],
    LV: [551, 92], EE: [551, 88],

    // Russia & Central Asia
    RU: [778, 78], KZ: [700, 111], UZ: [693, 131], TM: [680, 139],
    KG: [711, 131], TJ: [700, 139],

    // Middle East
    TR: [569, 139], IL: [569, 165], SA: [610, 175], AE: [633, 172],
    QA: [627, 173], KW: [615, 161], BH: [623, 168], OM: [644, 178],
    IQ: [601, 155], IR: [631, 150], JO: [575, 165], LB: [572, 158],
    SY: [577, 155], YE: [616, 195],

    // South Asia
    IN: [719, 183], PK: [683, 158], BD: [740, 180], LK: [724, 212],
    NP: [722, 168],

    // Southeast Asia
    TH: [756, 195], VN: [762, 195], MY: [758, 222], SG: [757, 228],
    ID: [778, 239], PH: [783, 200], MM: [744, 188], KH: [758, 202],
    LA: [755, 192],

    // East Asia
    CN: [792, 147], JP: [883, 147], KR: [844, 147], TW: [818, 178],
    HK: [806, 183], MO: [804, 185], MN: [778, 119],

    // Africa
    NG: [519, 217], EG: [564, 167], ZA: [567, 333], KE: [578, 244],
    GH: [497, 218], MA: [490, 159], DZ: [508, 161], TN: [525, 153],
    TZ: [575, 261], ET: [578, 220], UG: [567, 244], CM: [530, 225],
    CI: [492, 218], SN: [473, 204], AO: [540, 276], MZ: [575, 298],
    ZW: [561, 296], CD: [551, 250], SD: [563, 195], LY: [540, 167],
    RW: [563, 247], ML: [492, 199], NE: [519, 199], BF: [497, 207],
    GN: [477, 211], MW: [574, 284], ZM: [558, 284], BW: [551, 307],

    // Oceania
    AU: [869, 318], NZ: [935, 355], PG: [878, 261], FJ: [950, 293],
};

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
    highlightCode?: string;  // single country to highlight (for signature detail)
    dark?: boolean;
}

/**
 * Interpolate between green and red based on bot rate (0-1).
 * 0 = green (#22c55e), 0.5 = amber (#f59e0b), 1 = red (#ef4444)
 */
function botRateColor(rate: number, alpha: number = 0.85): string {
    const r = rate < 0.5 ? Math.round(34 + rate * 2 * (245 - 34)) : Math.round(245 + (rate - 0.5) * 2 * (239 - 245));
    const g = rate < 0.5 ? Math.round(197 + rate * 2 * (158 - 197)) : Math.round(158 + (rate - 0.5) * 2 * (68 - 158));
    const b = rate < 0.5 ? Math.round(94 + rate * 2 * (11 - 94)) : Math.round(11 + (rate - 0.5) * 2 * (68 - 11));
    return `rgba(${r},${g},${b},${alpha})`;
}

/**
 * Render a world dot map into the given container element.
 */
export function renderWorldMap(container: HTMLElement, data: MapDataPoint[], opts: MapOptions = {}): void {
    const w = opts.width ?? 1000;
    const h = opts.height ?? 500;
    const minR = opts.minRadius ?? 4;
    const maxR = opts.maxRadius ?? 22;
    const dark = opts.dark ?? false;

    const maxCount = Math.max(...data.map(d => d.totalCount), 1);

    // Build data lookup
    const dataMap = new Map<string, MapDataPoint>();
    for (const d of data) dataMap.set(d.code, d);

    // SVG construction
    const lines: string[] = [];
    lines.push(`<svg viewBox="0 0 ${w} ${h}" xmlns="http://www.w3.org/2000/svg" class="w-full h-auto" style="max-height:${h}px">`);

    // Background with subtle land mass outline (simplified continent shapes)
    const bgColor = dark ? 'rgba(148,163,184,0.04)' : 'rgba(15,23,42,0.03)';
    const gridColor = dark ? 'rgba(148,163,184,0.08)' : 'rgba(15,23,42,0.05)';
    lines.push(`<rect width="${w}" height="${h}" fill="transparent"/>`);

    // Grid lines (longitude)
    for (let lng = -120; lng <= 180; lng += 60) {
        const x = ((lng + 180) / 360) * w;
        lines.push(`<line x1="${x}" y1="0" x2="${x}" y2="${h}" stroke="${gridColor}" stroke-width="0.5"/>`);
    }
    // Grid lines (latitude)
    for (let lat = -60; lat <= 60; lat += 30) {
        const y = ((90 - lat) / 180) * h;
        lines.push(`<line x1="0" y1="${y}" x2="${w}" y2="${y}" stroke="${gridColor}" stroke-width="0.5"/>`);
    }

    // Equator
    const eqY = (90 / 180) * h;
    lines.push(`<line x1="0" y1="${eqY}" x2="${w}" y2="${eqY}" stroke="${gridColor}" stroke-width="1" stroke-dasharray="4,4"/>`);

    // Plot all known countries as faint dots first
    const faintColor = dark ? 'rgba(148,163,184,0.12)' : 'rgba(15,23,42,0.06)';
    for (const [code, [cx, cy]] of Object.entries(CENTROIDS)) {
        if (!dataMap.has(code)) {
            lines.push(`<circle cx="${cx}" cy="${cy}" r="2.5" fill="${faintColor}"/>`);
        }
    }

    // Sort data by total count ascending so largest circles render on top
    const sorted = [...data].sort((a, b) => a.totalCount - b.totalCount);

    for (const d of sorted) {
        const pos = CENTROIDS[d.code];
        if (!pos) continue;
        const [cx, cy] = pos;

        // Radius based on total count (sqrt scale)
        const scale = Math.sqrt(d.totalCount / maxCount);
        const r = minR + scale * (maxR - minR);

        const fill = botRateColor(d.botRate);
        const stroke = botRateColor(d.botRate, 1);
        const isHighlighted = opts.highlightCode === d.code;

        lines.push(`<circle cx="${cx}" cy="${cy}" r="${r}" fill="${fill}" stroke="${stroke}" stroke-width="${isHighlighted ? 2 : 0.5}" class="worldmap-dot" data-code="${d.code}">`);
        lines.push(`<title>${d.label}: ${d.totalCount} requests, ${Math.round(d.botRate * 100)}% bots</title>`);
        lines.push(`</circle>`);

        // Country code label for larger dots
        if ((opts.showLabels !== false && r > 8) || isHighlighted) {
            const textColor = dark ? 'rgba(255,255,255,0.75)' : 'rgba(0,0,0,0.65)';
            lines.push(`<text x="${cx}" y="${cy + r + 11}" text-anchor="middle" fill="${textColor}" font-size="9" font-family="Inter,system-ui,sans-serif" font-weight="600">${d.code}</text>`);
        }
    }

    // Legend
    const legendY = h - 20;
    const legendItems = [
        { rate: 0, label: 'Low bot %' },
        { rate: 0.5, label: 'Medium' },
        { rate: 1, label: 'High bot %' },
    ];
    let legendX = 20;
    for (const item of legendItems) {
        lines.push(`<circle cx="${legendX}" cy="${legendY}" r="5" fill="${botRateColor(item.rate)}"/>`);
        const labelColor = dark ? 'rgba(148,163,184,0.7)' : 'rgba(15,23,42,0.5)';
        lines.push(`<text x="${legendX + 10}" y="${legendY + 3.5}" fill="${labelColor}" font-size="9" font-family="Inter,system-ui,sans-serif">${item.label}</text>`);
        legendX += 90;
    }

    // Size legend
    legendX = w - 150;
    const sizeLabel = dark ? 'rgba(148,163,184,0.5)' : 'rgba(15,23,42,0.4)';
    lines.push(`<text x="${legendX}" y="${legendY + 3.5}" fill="${sizeLabel}" font-size="9" font-family="Inter,system-ui,sans-serif">Size = request volume</text>`);

    lines.push('</svg>');

    container.innerHTML = lines.join('');
}

/**
 * Render a small map highlighting a single country (for signature detail).
 */
export function renderCountryPin(container: HTMLElement, countryCode: string, dark: boolean = false): void {
    const pos = CENTROIDS[countryCode?.toUpperCase()];
    if (!pos) {
        container.innerHTML = `<div class="text-xs text-center py-4" style="color: var(--sb-card-subtle);">Location unknown</div>`;
        return;
    }

    renderWorldMap(container, [{
        code: countryCode.toUpperCase(),
        totalCount: 1,
        botRate: 0.5,
        label: countryCode,
    }], {
        width: 600,
        height: 300,
        minRadius: 8,
        maxRadius: 8,
        showLabels: true,
        highlightCode: countryCode.toUpperCase(),
        dark,
    });
}
