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

// liveDetection() removed — LiveDemo page now uses an iframe to the /_stylobot dashboard.
// The dashboard is fully self-contained with its own Alpine.js component.

// ===== Homepage Detection Bar Alpine Component =====

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

                // Beacon-only: server sends lightweight invalidation signals
                this.connection.on('BroadcastInvalidation', (signal: string) => {
                    if (signal === 'signature' || signal === this.signature) {
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
    Alpine.data('homeDetection', homeDetection);
}
