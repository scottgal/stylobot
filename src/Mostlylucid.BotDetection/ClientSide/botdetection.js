/**
 * StyloBot client-side bot/headless detector. Served as a static file via
 * /bot-detection/script.js -- the TagHelper emits a small bootstrap that
 * sets `window.MLBotD` (token, endpoint, feature toggles) before this
 * file loads. Keeps the served JS cacheable, CSP-friendly (nonce + self),
 * and reviewable as a plain artifact instead of an inline string spliced
 * together in C#.
 *
 * 2026 collection targets (see docs/client-side-detection-2026.md for the
 * rationale and which signals still pull weight after Chrome UA Reduction,
 * Safari ITP, Brave farbling, and Firefox ETP):
 *
 *   PRIMARY  (load-bearing in 2026)
 *     - CDP trap          : console.debug getter + Error stack foreign-frame probe
 *     - UA-CH             : getHighEntropyValues -- replaces raw UA parsing
 *     - Permissions vector: query 6, expect at least one "granted" on real users
 *     - BroadcastChannel  : echo latency, fresh-context bots show pristine values
 *     - performance.now() : clamp-residue distribution leaks the actual browser
 *     - Chromium triple   : startViewTransition + Speculation Rules + hasStorageAccess
 *     - Headless markers  : plugins.length, Notification.permission, chrome.runtime,
 *                           GPU renderer (SwiftShader / ANGLE Google Vulkan)
 *     - Touch consistency : mobile UA must have maxTouchPoints > 0 + 'ontouchstart'
 *
 *   SUPPORTING
 *     - Hardware          : cores, memory, screen, devicePixelRatio
 *     - Timezone / locale : Intl + navigator.language(s)
 *     - Audio fingerprint : OfflineAudioContext FFT hash (Chrome+Safari only;
 *                           Brave/Firefox noise it, so report-only)
 *     - Canvas hash       : same caveat -- collected but never used as a sole
 *                           verdict because Brave farbles it deliberately
 *     - WebGL parameter   : UNMASKED_RENDERER_WEBGL string
 *     - Network hints     : connection.effectiveType (coarse, non-PII)
 *     - User prefs        : prefers-color-scheme, prefers-reduced-motion
 *     - Interaction       : did the visitor interact at all (boolean)
 *
 *   LEGITIMATE-USER CLASSIFIERS (collected so server-side doesn't punish them)
 *     - navigator.brave?.isBrave() -- Brave Shields user, expect farbled signals
 *     - Lockdown Mode (iOS, WebGL+WebAudio+JIT all absent simultaneously)
 *     - Firefox RFP / Tor Browser fingerprint -- spoof the spoofers detected
 *
 *   NOT COLLECTED  (deprecated by 2026 browser changes)
 *     - Raw UA build / OS minor version  -- Chrome UA Reduction froze it
 *     - localStorage / cookie-based identity -- Safari ITP caps at 7 days,
 *                                              storage partitioning kills it
 *     - Naive navigator.webdriver as sole verdict -- patched by every modern
 *       bot framework; collected for the long tail only, never load-bearing
 *
 * Privacy contract:
 *   - No PII collected. Canvas / audio / WebGL hashes only (never raw pixels).
 *   - Fingerprint session is per-origin (storage partitioning is the rule).
 *   - Interaction tracking is boolean only (did interact: 0/1, never WHAT).
 *   - Network hints stay coarse (effectiveType, not downlink/RTT).
 */
(function () {
    'use strict';

    // === Bootstrap config (set by the TagHelper before this script loads) ====
    // {
    //   t:    signed token (required; if missing we abort silently)
    //   e:    endpoint URL  (default: /bot-detection/fingerprint)
    //   cfg: {
    //     timeout:           ms before we give up collecting          [3000]
    //     collectWebGL:      boolean                                  [true]
    //     collectCanvas:     boolean                                  [true]
    //     collectAudio:      boolean                                  [true]
    //     collectInteraction:boolean (waits 500ms after load, then posts) [true]
    //   }
    // }
    var cfg = (window.MLBotD || {});
    if (!cfg.t) return;
    var endpoint = cfg.e || '/bot-detection/fingerprint';
    var c = cfg.cfg || {};
    var timeout = c.timeout || 3000;

    // === Tiny non-cryptographic hash (used for canvas / audio / WebGL) =======
    function h(s) {
        var x = 0;
        for (var i = 0; i < s.length; i++) {
            x = ((x << 5) - x) + s.charCodeAt(i);
            x |= 0;
        }
        return x.toString(16);
    }

    // === [PRIMARY] CDP getter trap ============================================
    // Chrome DevTools Protocol-driven browsers (Puppeteer/Playwright/Selenium)
    // call console.debug when they format messages internally. A getter on
    // console.debug fires when CDP reads the function; legitimate JS code
    // accessing console.debug as a property would NOT trigger the getter on
    // simple reads in the same call frame (it's evaluated at read time).
    // Returns 1 = CDP detected, 0 = quiet.
    var cdpHit = 0;
    try {
        var dbgFn = console.debug;
        Object.defineProperty(console, 'debug', {
            get: function () { cdpHit = 1; return dbgFn; },
            configurable: true
        });
    } catch (e) {}

    // === [PRIMARY] Error-stack foreign-frame probe ===========================
    // Bot frameworks inject wrapper functions; synthesizing an Error and
    // inspecting the stack trace reveals frames like `at Object.apply` /
    // `at <anonymous>` that legitimate browser code rarely emits.
    function stackFrames() {
        try {
            var s = new Error().stack || '';
            return {
                len: s.length,
                hasObjectApply: /at Object\.apply/.test(s) ? 1 : 0,
                hasAnonymous: /at <anonymous>/.test(s) ? 1 : 0
            };
        } catch (e) {
            return { len: 0, hasObjectApply: 0, hasAnonymous: 0 };
        }
    }

    // === [PRIMARY] UA-Client Hints ===========================================
    // Replaces raw User-Agent parsing post Chrome UA Reduction. Returns the
    // high-entropy values when available (async; only on secure contexts).
    // Server-side cross-checks the architecture/platform vs the legacy UA
    // string to catch UA spoofers.
    async function uaCH() {
        try {
            var ud = navigator.userAgentData;
            if (!ud || !ud.getHighEntropyValues) return null;
            var v = await ud.getHighEntropyValues([
                'architecture', 'bitness', 'model',
                'platformVersion', 'fullVersionList', 'formFactors'
            ]);
            return {
                brands: ud.brands || [],
                mobile: ud.mobile ? 1 : 0,
                platform: ud.platform || '',
                arch: v.architecture || '',
                bits: v.bitness || '',
                model: v.model || '',
                platformVersion: v.platformVersion || '',
                fullVersionList: v.fullVersionList || [],
                formFactors: v.formFactors || []
            };
        } catch (e) { return null; }
    }

    // === [PRIMARY] Permissions-API state vector ==============================
    // Real users accumulate at least one "granted" permission (clipboard,
    // notifications, geolocation if they ever allowed it). Fresh bot
    // contexts return all-"prompt".
    async function permissionsVector() {
        try {
            if (!navigator.permissions || !navigator.permissions.query) return null;
            var names = ['clipboard-read', 'notifications', 'geolocation',
                         'camera', 'microphone', 'push'];
            var out = {};
            for (var i = 0; i < names.length; i++) {
                try {
                    var r = await navigator.permissions.query({ name: names[i] });
                    out[names[i]] = r.state;
                } catch (e) {
                    out[names[i]] = 'unsupported';
                }
            }
            return out;
        } catch (e) { return null; }
    }

    // === [PRIMARY] BroadcastChannel echo latency =============================
    // Most stealth shims don't touch BroadcastChannel; round-trip latency
    // through the channel is consistent in real browsers (~0.5-2ms) and
    // varies wildly in fresh bot contexts. Returns the median of 5 round
    // trips in ms.
    function broadcastEcho() {
        return new Promise(function (resolve) {
            try {
                if (typeof BroadcastChannel === 'undefined') return resolve(null);
                var bc = new BroadcastChannel('mlb-' + Math.random().toString(36).slice(2));
                var times = [];
                var n = 0;
                bc.onmessage = function (ev) {
                    if (ev.data && ev.data.t0) {
                        times.push(performance.now() - ev.data.t0);
                        if (n < 5) tick();
                        else { bc.close(); resolve(median(times)); }
                    }
                };
                function tick() { n++; bc.postMessage({ t0: performance.now(), i: n }); }
                tick();
                // Safety timeout: BroadcastChannel onmessage isn't called for
                // the sender's own postMessage in some sandboxed contexts.
                setTimeout(function () { try { bc.close(); } catch (e) {} resolve(null); }, 200);
            } catch (e) { resolve(null); }
        });
    }
    function median(arr) {
        if (!arr.length) return 0;
        var s = arr.slice().sort(function (a, b) { return a - b; });
        return s[Math.floor(s.length / 2)];
    }

    // === [PRIMARY] performance.now() clamp-residue probe =====================
    // Chrome clamps at 100µs, Firefox at 20µs, Safari at 1ms. Take 2000 tight
    // subtractions, bucket the modal delta. Reveals the actual browser
    // engine even when UA is spoofed.
    function clampProbe() {
        try {
            if (!performance || !performance.now) return null;
            var buckets = {};
            for (var i = 0; i < 2000; i++) {
                var a = performance.now();
                var b = performance.now();
                var d = (b - a).toFixed(3);
                buckets[d] = (buckets[d] || 0) + 1;
            }
            var modal = '', modalC = 0;
            for (var k in buckets) {
                if (buckets[k] > modalC) { modal = k; modalC = buckets[k]; }
            }
            return { modalDeltaMs: parseFloat(modal), count: modalC };
        } catch (e) { return null; }
    }

    // === [PRIMARY] Chromium-feature presence triple ==========================
    // If UA brands include Safari but `document.startViewTransition` exists,
    // the UA is spoofed (Safari doesn't ship View Transitions). Cheap UA
    // cross-check, server-side compares with the brand list.
    function chromiumTriple() {
        return {
            viewTx: typeof document.startViewTransition === 'function' ? 1 : 0,
            speculation: ('speculationrules' in HTMLScriptElement.prototype) ? 1 : 0,
            storageAccess: typeof document.hasStorageAccess === 'function' ? 1 : 0
        };
    }

    // === [PRIMARY] Headless markers ==========================================
    // No single one is enough; the server combines 3+ for a verdict.
    function headlessMarkers() {
        var m = {};
        try { m.plugins = (navigator.plugins || []).length; } catch (e) { m.plugins = -1; }
        try { m.notif = Notification && Notification.permission ? Notification.permission : 'unsupported'; }
        catch (e) { m.notif = 'unsupported'; }
        try { m.chromeRt = (typeof chrome !== 'undefined' && chrome.runtime) ? 1 : 0; }
        catch (e) { m.chromeRt = 0; }
        try { m.languages = (navigator.languages || []).length; } catch (e) { m.languages = 0; }
        try { m.connection = navigator.connection ? 1 : 0; } catch (e) { m.connection = 0; }
        return m;
    }

    // === [PRIMARY] Touch + pointer consistency ===============================
    // Mobile UA with maxTouchPoints === 0 is the single best mobile-bot tell.
    function touchProfile() {
        return {
            maxTouch: navigator.maxTouchPoints || 0,
            ontouch: ('ontouchstart' in window) ? 1 : 0,
            pointerEvt: ('PointerEvent' in window) ? 1 : 0
        };
    }

    // === [SUPPORTING] Hardware / locale / preferences ========================
    function basics() {
        var n = navigator, w = window, s = screen, b = {};
        b.tz = (Intl.DateTimeFormat().resolvedOptions() || {}).timeZone || '';
        b.lang = n.language || '';
        b.langs = (n.languages || []).slice(0, 3).join(',');
        b.platform = n.platform || '';
        b.cores = n.hardwareConcurrency || 0;
        b.mem = n.deviceMemory || 0;
        b.screen = s.width + 'x' + s.height + 'x' + s.colorDepth;
        b.avail = s.availWidth + 'x' + s.availHeight;
        b.dpr = w.devicePixelRatio || 1;
        b.outerW = w.outerWidth || 0;
        b.outerH = w.outerHeight || 0;
        b.innerW = w.innerWidth || 0;
        b.innerH = w.innerHeight || 0;
        // Coarse non-PII preferences
        try { b.dark = matchMedia('(prefers-color-scheme: dark)').matches ? 1 : 0; } catch (e) { b.dark = -1; }
        try { b.reduced = matchMedia('(prefers-reduced-motion: reduce)').matches ? 1 : 0; } catch (e) { b.reduced = -1; }
        // Network hints (coarse: effectiveType only, not bandwidth)
        try {
            var conn = n.connection || n.mozConnection || n.webkitConnection;
            b.net = conn ? (conn.effectiveType || '') : '';
        } catch (e) { b.net = ''; }
        return b;
    }

    // === [SUPPORTING] Audio fingerprint ======================================
    // OfflineAudioContext + DynamicsCompressor. FFT-implementation entropy
    // per architecture is real on Chrome+Safari; Firefox RFP and Brave
    // farble it. Server compares with the browser-vendor cross-checks.
    async function audioHash() {
        if (c.collectAudio === false) return null;
        try {
            var Ctx = window.OfflineAudioContext || window.webkitOfflineAudioContext;
            if (!Ctx) return null;
            var oac = new Ctx(1, 44100, 44100);
            var osc = oac.createOscillator();
            osc.type = 'triangle';
            osc.frequency.value = 10000;
            var comp = oac.createDynamicsCompressor();
            comp.threshold.value = -50;
            comp.knee.value = 40;
            comp.ratio.value = 12;
            comp.attack.value = 0;
            comp.release.value = 0.25;
            osc.connect(comp);
            comp.connect(oac.destination);
            osc.start(0);
            var buf = await oac.startRendering();
            var data = buf.getChannelData(0);
            var sum = 0;
            for (var i = 4500; i < 5000; i++) sum += Math.abs(data[i]);
            return h(sum.toString());
        } catch (e) { return null; }
    }

    // === [SUPPORTING] Canvas hash (Brave-aware: collected but server-side
    //     never penalises drift -- Brave farbles deliberately) ===============
    function canvasHash() {
        if (c.collectCanvas === false) return null;
        try {
            var cv = document.createElement('canvas');
            cv.width = 240; cv.height = 60;
            var ctx = cv.getContext('2d');
            if (!ctx) return null;
            ctx.textBaseline = 'top';
            ctx.font = '14px Arial';
            ctx.fillStyle = '#f60';
            ctx.fillRect(125, 1, 62, 20);
            ctx.fillStyle = '#069';
            ctx.fillText('StyloBot \u{1f6e1}\u{fe0f}', 2, 15);
            ctx.fillStyle = 'rgba(102, 204, 0, 0.7)';
            ctx.fillText('StyloBot \u{1f6e1}\u{fe0f}', 4, 17);
            return h(cv.toDataURL().slice(-256));
        } catch (e) { return null; }
    }

    // === [SUPPORTING] WebGL renderer string ==================================
    function webglRenderer() {
        if (c.collectWebGL === false) return null;
        try {
            var cv = document.createElement('canvas');
            var gl = cv.getContext('webgl') || cv.getContext('experimental-webgl');
            if (!gl) return null;
            var ext = gl.getExtension('WEBGL_debug_renderer_info');
            if (!ext) return null;
            return {
                vendor: gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) || '',
                renderer: gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) || ''
            };
        } catch (e) { return null; }
    }

    // === Legitimate-user classifiers (don't penalise these) ==================
    async function legitimacyMarkers() {
        var out = { brave: 0, lockdown: 0 };
        try {
            if (navigator.brave && typeof navigator.brave.isBrave === 'function') {
                out.brave = (await navigator.brave.isBrave()) ? 1 : 0;
            }
        } catch (e) {}
        // Lockdown Mode: WebGL + WebAudio + JIT all disabled on iOS
        try {
            var cv = document.createElement('canvas');
            var noWebGL = !(cv.getContext('webgl') || cv.getContext('experimental-webgl'));
            var noAudio = !(window.OfflineAudioContext || window.webkitOfflineAudioContext);
            var iOS = /iPad|iPhone|iPod/.test(navigator.userAgent || '');
            out.lockdown = (iOS && noWebGL && noAudio) ? 1 : 0;
        } catch (e) {}
        return out;
    }

    // === Long-tail signals (collected for completeness; not load-bearing) ====
    function longTail() {
        var t = {};
        try { t.webdriver = navigator.webdriver === true ? 1 : 0; } catch (e) { t.webdriver = -1; }
        try { t.phantom = ('callPhantom' in window || '_phantom' in window) ? 1 : 0; } catch (e) { t.phantom = 0; }
        try { t.selenium = ('webdriver' in window || '__selenium_unwrapped' in window) ? 1 : 0; } catch (e) { t.selenium = 0; }
        try { t.iframe = (window.parent !== window) ? 1 : 0; } catch (e) { t.iframe = -1; }
        return t;
    }

    // === Interaction tracking (boolean, no PII) ==============================
    var interacted = 0;
    if (c.collectInteraction !== false) {
        try {
            var mark = function () { interacted = 1; };
            window.addEventListener('mousemove', mark, { once: true, passive: true });
            window.addEventListener('mousedown', mark, { once: true, passive: true });
            window.addEventListener('touchstart', mark, { once: true, passive: true });
            window.addEventListener('keydown', mark, { once: true, passive: true });
        } catch (e) {}
    }

    // === Orchestration =======================================================
    async function collect() {
        // Run async collectors in parallel, then bolt on sync ones. Each
        // collector has its own try/catch -- a single broken probe must
        // never block the whole payload.
        var deadline = new Promise(function (res) { setTimeout(res, timeout); });
        var asyncPart = Promise.all([
            uaCH(),
            permissionsVector(),
            broadcastEcho(),
            audioHash(),
            legitimacyMarkers()
        ]);
        var asyncOrTimeout = Promise.race([asyncPart, deadline.then(function () { return null; })]);
        var asyncResult = await asyncOrTimeout || [];

        var payload = {
            t: cfg.t,
            v: '2.0.0',
            ts: Date.now(),
            cdp: cdpHit,
            stack: stackFrames(),
            ua: asyncResult[0] || null,
            perms: asyncResult[1] || null,
            bcast: asyncResult[2] || null,
            audio: asyncResult[3] || null,
            legit: asyncResult[4] || { brave: 0, lockdown: 0 },
            clamp: clampProbe(),
            triple: chromiumTriple(),
            headless: headlessMarkers(),
            touch: touchProfile(),
            basics: basics(),
            canvas: canvasHash(),
            webgl: webglRenderer(),
            tail: longTail(),
            interacted: interacted
        };
        return payload;
    }

    // === Transport ===========================================================
    function send(payload) {
        var json = JSON.stringify(payload);
        // Prefer fetch+keepalive (gives a real response if anyone listens);
        // sendBeacon fallback for unload-time delivery. Both are
        // CSP-friendly via the 'connect-src' directive.
        try {
            if (typeof fetch === 'function') {
                fetch(endpoint, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: json,
                    keepalive: true,
                    credentials: 'omit',
                    mode: 'cors'
                }).catch(function () {
                    // Fall through to sendBeacon on fetch error
                    if (navigator.sendBeacon) {
                        navigator.sendBeacon(endpoint, new Blob([json], { type: 'application/json' }));
                    }
                });
                return;
            }
        } catch (e) {}
        try {
            if (navigator.sendBeacon) {
                navigator.sendBeacon(endpoint, new Blob([json], { type: 'application/json' }));
            }
        } catch (e) {}
    }

    // === Entry point =========================================================
    // Give interaction listeners a beat to mark, then collect + send.
    var startDelay = c.collectInteraction === false ? 0 : 500;
    setTimeout(function () {
        collect().then(send).catch(function () {});
    }, startDelay);
})();
