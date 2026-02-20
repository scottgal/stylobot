/**
 * WarGames-style attack arc renderer.
 * Draws animated ballistic missile arcs from attacking countries
 * to a server location, with glowing trails and impact effects.
 *
 * Uses a canvas overlay on top of the jsvectormap world map.
 * Coordinates use Miller cylindrical projection calibrated to match jsvectormap's world map.
 */

// Server location: Germany
const SERVER_LAT = 51.2;
const SERVER_LNG = 10.4;

// Country centroids [lat, lng] — same as worldmap.ts
const CENTROIDS: Record<string, [number, number]> = {
    US: [39.8, -98.5], CA: [56.1, -106.3], MX: [23.6, -102.5],
    BR: [-14.2, -51.9], AR: [-38.4, -63.6], CL: [-35.7, -71.5], CO: [4.6, -74.3],
    PE: [-9.2, -75.0], VE: [6.4, -66.6], EC: [-1.8, -78.2],
    GB: [55.4, -3.4], IE: [53.4, -8.2], FR: [46.2, 2.2], DE: [51.2, 10.4],
    NL: [52.1, 5.3], BE: [50.5, 4.5], CH: [46.8, 8.2], AT: [47.5, 14.6],
    ES: [40.5, -3.7], PT: [39.4, -8.2], IT: [41.9, 12.6], LU: [49.8, 6.1],
    SE: [60.1, 18.6], NO: [60.5, 8.5], DK: [56.3, 9.5], FI: [61.9, 25.7],
    PL: [51.9, 19.1], CZ: [49.8, 15.5], HU: [47.2, 19.5], RO: [45.9, 25.0],
    UA: [48.4, 31.2], BG: [42.7, 25.5], GR: [39.1, 21.8], HR: [45.1, 15.2],
    RS: [44.0, 21.0], SK: [48.7, 19.7], SI: [46.2, 14.8], LT: [55.2, 23.9],
    LV: [56.9, 24.1], EE: [58.6, 25.0], BA: [43.9, 17.7], MD: [47.4, 28.4],
    RU: [61.5, 105.3], KZ: [48.0, 68.0], TR: [39.0, 35.2],
    IL: [31.0, 34.9], SA: [23.9, 45.1], AE: [23.4, 53.8], IR: [32.4, 53.7],
    QA: [25.4, 51.2], KW: [29.3, 47.5], BH: [26.1, 50.6], OM: [21.5, 55.9],
    IN: [20.6, 79.0], PK: [30.4, 69.3], BD: [23.7, 90.4], LK: [7.9, 80.8],
    TH: [15.9, 100.9], VN: [14.1, 108.3], MY: [4.2, 101.9], SG: [1.4, 103.8],
    ID: [-0.8, 113.9], PH: [12.9, 121.8], MM: [19.8, 96.7], KH: [12.6, 104.9],
    CN: [35.9, 104.2], JP: [36.2, 138.3], KR: [35.9, 127.8], TW: [23.7, 121.0],
    HK: [22.3, 114.2], MO: [22.2, 113.5], MN: [46.9, 103.8],
    AU: [-25.3, 133.8], NZ: [-40.9, 174.9],
    NG: [9.1, 8.7], EG: [26.8, 30.8], ZA: [-30.6, 22.9], KE: [-0.0, 37.9],
    GH: [7.9, -1.0], MA: [31.8, -7.1], DZ: [28.0, 1.7], TZ: [-6.4, 34.9],
    ET: [9.1, 40.5], UG: [-1.4, 32.3], SN: [14.5, -14.5], CI: [7.5, -5.5],
};

// Miller cylindrical projection (matches jsvectormap's world map)
function millerY(lat: number): number {
    const phi = lat * Math.PI / 180;
    return -1.25 * Math.log(Math.tan(Math.PI / 4 + 0.4 * phi));
}

// jsvectormap world map bounds (calibrated)
const LNG_MIN = -169, LNG_MAX = 191;
const LAT_TOP = 83, LAT_BOT = -60;
const Y_MIN = millerY(LAT_TOP), Y_MAX = millerY(LAT_BOT);

function project(lat: number, lng: number, w: number, h: number): { x: number; y: number } {
    return {
        x: ((lng - LNG_MIN) / (LNG_MAX - LNG_MIN)) * w,
        y: ((millerY(lat) - Y_MIN) / (Y_MAX - Y_MIN)) * h,
    };
}

interface ActiveArc {
    fromX: number;
    fromY: number;
    toX: number;
    toY: number;
    cpX: number;   // bezier control point
    cpY: number;
    startTime: number;
    duration: number;
    color: string;
}

// Quadratic bezier point
function bezier(t: number, p0: number, p1: number, p2: number): number {
    return (1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t ** 2 * p2;
}

// Ease-out cubic for smooth deceleration
function easeOutCubic(t: number): number {
    return 1 - (1 - t) ** 3;
}

export class AttackArcRenderer {
    private canvas: HTMLCanvasElement;
    private ctx: CanvasRenderingContext2D;
    private arcs: ActiveArc[] = [];
    private animating = false;
    private containerEl: HTMLElement;
    private mapWidth = 0;
    private mapHeight = 0;

    // Throttle: max 1 arc per country per 3s
    private lastFired = new Map<string, number>();
    private static readonly THROTTLE_MS = 3000;
    private static readonly MAX_ARCS = 20;
    private static readonly ARC_DURATION = 2200;       // ms for dot to travel
    private static readonly FADE_DURATION = 2000;       // ms for arc to fade after impact
    private static readonly TRAIL_SEGMENTS = 60;

    // Arc colors by risk level
    private static readonly COLORS = {
        high: 'rgba(239, 68, 68,',      // red
        medium: 'rgba(245, 158, 11,',    // amber
        low: 'rgba(168, 85, 247,',       // purple (reconnaissance)
    };

    constructor(container: HTMLElement) {
        this.containerEl = container;

        this.canvas = document.createElement('canvas');
        this.canvas.style.position = 'absolute';
        this.canvas.style.top = '0';
        this.canvas.style.left = '0';
        this.canvas.style.width = '100%';
        this.canvas.style.height = '100%';
        this.canvas.style.pointerEvents = 'none';
        this.canvas.style.zIndex = '10';

        // Ensure container is positioned
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';

        container.appendChild(this.canvas);
        this.ctx = this.canvas.getContext('2d')!;

        this.resize();

        // Track container resize
        const ro = new ResizeObserver(() => this.resize());
        ro.observe(container);
    }

    private resize() {
        const rect = this.containerEl.getBoundingClientRect();
        // Find the actual map element inside the container (the jsvectormap div)
        const mapEl = this.containerEl.querySelector('[id^="jsvectormap-"]') as HTMLElement | null;
        const mapRect = mapEl ? mapEl.getBoundingClientRect() : rect;

        const dpr = window.devicePixelRatio || 1;
        this.canvas.width = Math.round(rect.width * dpr);
        this.canvas.height = Math.round(rect.height * dpr);
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        // Store the map area dimensions for projection
        this.mapWidth = mapRect.width;
        this.mapHeight = mapRect.height;
    }

    /**
     * Fire an attack arc from a country to the server.
     * @param countryCode 2-letter ISO code
     * @param risk 'high' | 'medium' | 'low' — determines arc color
     */
    fire(countryCode: string, risk: 'high' | 'medium' | 'low' = 'high') {
        const code = countryCode.toUpperCase();
        if (code === 'DE') return; // Don't fire from server to itself

        const centroid = CENTROIDS[code];
        if (!centroid) return;

        // Throttle per country
        const now = performance.now();
        const last = this.lastFired.get(code) ?? 0;
        if (now - last < AttackArcRenderer.THROTTLE_MS) return;
        this.lastFired.set(code, now);

        // Cap max simultaneous arcs
        if (this.arcs.length >= AttackArcRenderer.MAX_ARCS) {
            this.arcs.shift();
        }

        const from = project(centroid[0], centroid[1], this.mapWidth, this.mapHeight);
        const to = project(SERVER_LAT, SERVER_LNG, this.mapWidth, this.mapHeight);

        // Control point: midpoint raised by distance * factor for parabolic arc
        const midX = (from.x + to.x) / 2;
        const midY = (from.y + to.y) / 2;
        const dist = Math.sqrt((to.x - from.x) ** 2 + (to.y - from.y) ** 2);
        const arcHeight = Math.min(dist * 0.45, this.mapHeight * 0.35);

        const colorBase = AttackArcRenderer.COLORS[risk] || AttackArcRenderer.COLORS.high;

        this.arcs.push({
            fromX: from.x,
            fromY: from.y,
            toX: to.x,
            toY: to.y,
            cpX: midX,
            cpY: midY - arcHeight,
            startTime: now,
            duration: AttackArcRenderer.ARC_DURATION,
            color: colorBase,
        });

        if (!this.animating) this.startAnimation();
    }

    private startAnimation() {
        this.animating = true;
        const loop = () => {
            const now = performance.now();
            const totalLife = AttackArcRenderer.ARC_DURATION + AttackArcRenderer.FADE_DURATION;
            this.arcs = this.arcs.filter(a => now - a.startTime < totalLife);

            if (this.arcs.length === 0) {
                this.animating = false;
                this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
                return;
            }

            this.render(now);
            requestAnimationFrame(loop);
        };
        requestAnimationFrame(loop);
    }

    private render(now: number) {
        const w = this.mapWidth;
        const h = this.mapHeight;
        this.ctx.clearRect(0, 0, w, h);

        // Draw server target (pulsing circle at DE)
        const server = project(SERVER_LAT, SERVER_LNG, w, h);
        this.drawServerTarget(server.x, server.y, now);

        for (const arc of this.arcs) {
            const elapsed = now - arc.startTime;
            const rawProgress = Math.min(elapsed / arc.duration, 1);
            const progress = easeOutCubic(rawProgress);
            const pastImpact = elapsed > arc.duration;
            const fadeAlpha = pastImpact
                ? 1 - (elapsed - arc.duration) / AttackArcRenderer.FADE_DURATION
                : 1;

            if (fadeAlpha <= 0) continue;

            this.drawArc(arc, progress, fadeAlpha);
        }
    }

    private drawServerTarget(x: number, y: number, now: number) {
        const ctx = this.ctx;
        const pulse = 0.5 + 0.5 * Math.sin(now * 0.003);

        // Outer pulsing ring
        ctx.beginPath();
        ctx.arc(x, y, 8 + pulse * 4, 0, Math.PI * 2);
        ctx.strokeStyle = `rgba(34, 197, 94, ${0.3 + pulse * 0.2})`;
        ctx.lineWidth = 1;
        ctx.stroke();

        // Inner ring
        ctx.beginPath();
        ctx.arc(x, y, 4, 0, Math.PI * 2);
        ctx.strokeStyle = 'rgba(34, 197, 94, 0.6)';
        ctx.lineWidth = 1.5;
        ctx.stroke();

        // Core dot
        ctx.beginPath();
        ctx.arc(x, y, 2, 0, Math.PI * 2);
        ctx.fillStyle = `rgba(34, 197, 94, ${0.7 + pulse * 0.3})`;
        ctx.fill();
    }

    private drawArc(arc: ActiveArc, progress: number, alpha: number) {
        const ctx = this.ctx;
        const { fromX, fromY, toX, toY, cpX, cpY, color } = arc;
        const segs = AttackArcRenderer.TRAIL_SEGMENTS;

        // === 1. Draw trail (fading gradient along the arc path up to current position) ===
        const trailEnd = Math.floor(progress * segs);
        if (trailEnd > 1) {
            for (let i = 1; i <= trailEnd; i++) {
                const t0 = (i - 1) / segs;
                const t1 = i / segs;
                // Fade: segments closer to the dot are brighter
                const segAlpha = (i / trailEnd) * 0.6 * alpha;

                const x0 = bezier(t0, fromX, cpX, toX);
                const y0 = bezier(t0, fromY, cpY, toY);
                const x1 = bezier(t1, fromX, cpX, toX);
                const y1 = bezier(t1, fromY, cpY, toY);

                ctx.beginPath();
                ctx.moveTo(x0, y0);
                ctx.lineTo(x1, y1);
                ctx.strokeStyle = color + segAlpha + ')';
                ctx.lineWidth = 1.5;
                ctx.stroke();
            }
        }

        // === 2. Glowing dot at current position ===
        if (progress < 1) {
            const dotX = bezier(progress, fromX, cpX, toX);
            const dotY = bezier(progress, fromY, cpY, toY);

            // Outer glow
            const glow = ctx.createRadialGradient(dotX, dotY, 0, dotX, dotY, 10);
            glow.addColorStop(0, `rgba(255, 220, 80, ${0.9 * alpha})`);
            glow.addColorStop(0.4, color + (0.6 * alpha) + ')');
            glow.addColorStop(1, color + '0)');
            ctx.beginPath();
            ctx.arc(dotX, dotY, 10, 0, Math.PI * 2);
            ctx.fillStyle = glow;
            ctx.fill();

            // Core bright dot
            ctx.beginPath();
            ctx.arc(dotX, dotY, 2.5, 0, Math.PI * 2);
            ctx.fillStyle = `rgba(255, 255, 230, ${alpha})`;
            ctx.fill();
        }

        // === 3. Source origin marker ===
        ctx.beginPath();
        ctx.arc(fromX, fromY, 3, 0, Math.PI * 2);
        ctx.fillStyle = color + (0.5 * alpha) + ')';
        ctx.fill();

        // === 4. Impact effect at destination ===
        if (progress >= 1) {
            const impactT = 1 - alpha;  // 0 at impact, 1 at fully faded
            const radius = 5 + impactT * 30;

            // Expanding shockwave ring
            ctx.beginPath();
            ctx.arc(toX, toY, radius, 0, Math.PI * 2);
            ctx.strokeStyle = color + (0.5 * (1 - impactT)) + ')';
            ctx.lineWidth = 2 - impactT;
            ctx.stroke();

            // Second inner ring
            if (impactT < 0.5) {
                const r2 = 3 + impactT * 15;
                ctx.beginPath();
                ctx.arc(toX, toY, r2, 0, Math.PI * 2);
                ctx.strokeStyle = `rgba(255, 220, 80, ${0.4 * (1 - impactT * 2)})`;
                ctx.lineWidth = 1;
                ctx.stroke();
            }

            // Impact flash
            if (impactT < 0.15) {
                const flashAlpha = (0.15 - impactT) / 0.15;
                ctx.beginPath();
                ctx.arc(toX, toY, 4, 0, Math.PI * 2);
                ctx.fillStyle = `rgba(255, 255, 200, ${flashAlpha * 0.8})`;
                ctx.fill();
            }
        }
    }

    /** Remove the canvas and stop animations. */
    destroy() {
        this.arcs = [];
        this.animating = false;
        this.canvas.remove();
    }
}
