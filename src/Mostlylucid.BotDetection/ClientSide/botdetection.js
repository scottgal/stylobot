/**
 * StyloBot client-side bot/headless detector. Served as a static file via
 * /bot-detection/script.js -- the TagHelper emits a small bootstrap that
 * sets `window.StyloBot` (token, endpoint, feature toggles) before this
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
    var cfg = (window.StyloBot || {});
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
            // Speculation Rules MUST be probed via HTMLScriptElement.supports().
            // `'speculationrules' in HTMLScriptElement.prototype` is a
            // FALSE-NEGATIVE on every shipping Chrome (verified live on Chrome
            // 149) -- it made a genuine Chrome look like it was missing a Chrome
            // feature, which is exactly the browser-consistency false-positive we
            // must not create.
            speculation: (typeof HTMLScriptElement.supports === 'function'
                && HTMLScriptElement.supports('speculationrules')) ? 1 : 0,
            storageAccess: typeof document.hasStorageAccess === 'function' ? 1 : 0
        };
    }

    // === [PRIMARY] Version-gated feature vector (browser-consistency signals) ==
    // Raw capability observations (1 = present, 0 = absent, -1 = probe errored)
    // the server-side browser-UA-consistency check consumes to verify the CLAIMED
    // browser+version (UA-CH fullVersionList) against what this engine actually
    // ships. We emit ONLY the observations here; the feature -> min-version matrix
    // and the consistency verdict live server-side (matrix as YAML data, scoring
    // in the contributor) -- never a claimed-vs-observed verdict baked into this
    // file. This is claim verification, not fingerprinting: no identity, no
    // high-entropy hashing, just "does the engine behave like the version it
    // claims to be". Each probe uses correct standards-based feature detection so
    // a real browser is never mislabelled as missing one of its own features.
    function versionFeatures() {
        var f = {};
        // Popover API           -- Chrome 114, Safari 17, Firefox 125
        try { f.popover = (typeof HTMLElement !== 'undefined'
            && typeof HTMLElement.prototype.showPopover === 'function') ? 1 : 0; }
        catch (e) { f.popover = -1; }
        // CSS :has()            -- Chrome 105, Safari 15.4, Firefox 121
        try { f.cssHas = (typeof CSS !== 'undefined' && CSS.supports
            && CSS.supports('selector(:has(*))')) ? 1 : 0; }
        catch (e) { f.cssHas = -1; }
        // Array.prototype.findLast -- Chrome 97, Safari 15.4, Firefox 104
        try { f.arrayFindLast = (typeof Array.prototype.findLast === 'function') ? 1 : 0; }
        catch (e) { f.arrayFindLast = -1; }
        // structuredClone       -- Chrome 98, Safari 15.4, Firefox 94
        try { f.structuredClone = (typeof structuredClone === 'function') ? 1 : 0; }
        catch (e) { f.structuredClone = -1; }
        // WebGPU (navigator.gpu) -- Chrome 113 desktop; absent on most stable
        // Firefox/Safari. Engine + version tell.
        try { f.webGpu = ('gpu' in navigator) ? 1 : 0; }
        catch (e) { f.webGpu = -1; }
        return f;
    }

    // === [PRIMARY] Engine-identity probes (un-spoofable by mainstream tools) ===
    // The strongest browser-consistency tells: these reveal the REAL JS engine
    // (V8 / SpiderMonkey / JavaScriptCore) regardless of the claimed UA, because
    // anti-detect browsers, Brave / Firefox-RFP / Tor, and puppeteer-stealth all
    // rewrite the surface (canvas / WebGL / UA) but run on a real, unmodified
    // engine. A claimed Safari/Firefox UA that exposes V8-only internals is a
    // definitive spoof. Raw observations only (1 present, 0 absent, -1 errored);
    // the claimed-vs-observed verdict lives server-side.
    function engineProbes() {
        var e = {};
        // Intl.v8BreakIterator: V8-only (Chromium). Present under a claimed
        // Safari/Firefox UA = real Chromium behind a spoofed UA.
        try { e.v8BreakIterator = (typeof Intl !== 'undefined' && 'v8BreakIterator' in Intl) ? 1 : 0; }
        catch (ex) { e.v8BreakIterator = -1; }
        // Error.captureStackTrace: V8-only API (absent in JSC / SpiderMonkey).
        try { e.errorCaptureStackTrace = (typeof Error.captureStackTrace === 'function') ? 1 : 0; }
        catch (ex) { e.errorCaptureStackTrace = -1; }
        // Error().stack format is engine-specific: V8 frames start "    at ...";
        // SpiderMonkey / JSC use "func@url:line". We emit the family only, never
        // the raw stack (it is our own synthetic Error -- no PII).
        try {
            var s = (new Error()).stack || '';
            e.stackStyle = /^\s*at\s/m.test(s) ? 'v8' : (/@/.test(s) ? 'spidermonkey-jsc' : 'unknown');
        } catch (ex) { e.stackStyle = 'unknown'; }
        // RegExp lookbehind: un-polyfillable syntax. Shipped Chrome 62, Firefox
        // 78, Safari 16.4. A claimed Safari < 16.4 that supports it is impossible.
        try { new RegExp('(?<=a)b'); e.regexLookbehind = 1; } catch (ex) { e.regexLookbehind = 0; }
        // File System Access (showOpenFilePicker): Chromium-only (Chrome 86).
        try { e.showOpenFilePicker = (typeof window.showOpenFilePicker === 'function') ? 1 : 0; }
        catch (ex) { e.showOpenFilePicker = -1; }
        // navigator.userAgentData: Chromium-only. Present under a non-Chromium
        // claim = spoof.
        try { e.userAgentData = ('userAgentData' in navigator && navigator.userAgentData != null) ? 1 : 0; }
        catch (ex) { e.userAgentData = -1; }
        return e;
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
        // Network hints (coarse: effectiveType only, not bandwidth).
        // connType is the connection class (wifi/cellular/ethernet/...) used by
        // InconsistencyContributor to flag mobile UA-CH paired with non-mobile
        // links (the damru / Redroid pattern: real Android Chrome inside a Linux
        // container reports "ethernet" because Network.overrideNetworkState is
        // skipped). Only Chrome on Android / Chrome OS / secure contexts expose
        // .type; empty string elsewhere is fine (the server-side check requires
        // a real value to fire).
        try {
            var conn = n.connection || n.mozConnection || n.webkitConnection;
            b.net = conn ? (conn.effectiveType || '') : '';
            b.connType = conn ? (conn.type || '') : '';
        } catch (e) { b.net = ''; b.connType = ''; }
        // Chrome family detection: window.chrome exists on Chrome / Chromium /
        // Edge / Brave / Opera; absent on Firefox / Safari / WebKit. Gates the
        // headless-Chrome "no plugins + no chrome.runtime" check server-side so
        // it doesn't false-positive on iPhone Safari (which also reports zero
        // plugins and no chrome.runtime, but those defaults are correct for it).
        try { b.hasChrome = (typeof window.chrome !== 'undefined') ? 1 : 0; } catch (e) { b.hasChrome = 0; }

        // === [PRIMARY] CDP Runtime.enable probe ==============================
        // When CDP Runtime.enable is attached, Chromium serialises console.*
        // arguments for the Console.messageAdded protocol event. We pass an
        // object whose toString() increments a counter; after console.debug
        // returns, a non-zero counter means CDP serialised our probe -- i.e.
        // CDP Runtime.enable is attached. Catches Bright Data Scraping Browser
        // (stock Chromium via remote CDP), damru's patched Playwright during
        // the Runtime.enable window, vanilla Puppeteer / Playwright. Distinct
        // from the cdpHit getter trap at the top of this file: that one fires
        // when CDP reads the .debug property descriptor (init-time); this one
        // fires when CDP calls toString on call-time arguments (call-time).
        // Both being positive is near-certain CDP; only one is suggestive.
        // -1 sentinel = probe errored; treated as no-signal server-side.
        try {
            var cdpToStringCalls = 0;
            var cdpProbe = { toString: function () { cdpToStringCalls++; return ''; } };
            console.debug(cdpProbe);
            b.cdpRuntime = cdpToStringCalls;
        } catch (e) { b.cdpRuntime = -1; }

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
        // Firefox resistFingerprinting (RFP) / Tor Browser: privacy users who
        // deliberately spoof surface signals. We emit raw markers so the
        // server-side consistency check treats them as legitimate (NOT bots) and
        // suppresses the surface-contradiction checks for them. MozAppearance is
        // a Firefox-only CSS property (engine tell); RFP forces the timezone to
        // UTC; Tor additionally letterboxes the window to round dimensions and
        // ships a frozen ESR UA.
        try {
            var isFirefox = 'MozAppearance' in (document.documentElement.style || {});
            var tzUtc = (((Intl.DateTimeFormat().resolvedOptions() || {}).timeZone) === 'UTC') ? 1 : 0;
            var iw = window.innerWidth || 0, ih = window.innerHeight || 0;
            out.firefox = isFirefox ? 1 : 0;
            out.tzUtc = tzUtc;
            out.torLetterbox = (isFirefox && tzUtc && iw > 0 && iw % 100 === 0 && ih % 100 === 0) ? 1 : 0;
        } catch (e) {}
        return out;
    }

    // === [PRIMARY] WebRTC ICE-gathering probe ================================
    // Creates an RTCPeerConnection with a STUN server, calls createOffer(),
    // and waits ~2.5s collecting ICE candidates. On a real mobile network a
    // STUN probe always produces at least one srflx (server-reflexive)
    // candidate. Absence indicates UDP egress is blocked at the OS or proxy
    // layer -- damru (iptables drop on Chrome UID), Bright Data Scraping
    // Browser (restricted egress), locked-down VMs. Server-side check is
    // gated on mobile UA-CH because desktop network policy is too variable.
    function iceProbe() {
        return new Promise(function (resolve) {
            try {
                // Empty cfg.iceStun = probe disabled (privacy-conscious operators
                // who'd rather not phone a STUN server at all). The "errored" flag
                // tells the server to treat this as no-signal, not as positive
                // evidence of UDP blocking.
                var stunUrl = c.iceStun;
                if (!stunUrl)
                    return resolve({ count: 0, srflx: 0, host: 0, errored: 1 });
                if (typeof RTCPeerConnection !== 'function')
                    return resolve({ count: 0, srflx: 0, host: 0, errored: 1 });
                var pc = new RTCPeerConnection({
                    iceServers: [{ urls: stunUrl }]
                });
                var count = 0, srflx = 0, host = 0;
                pc.onicecandidate = function (e) {
                    if (!e.candidate) return;
                    count++;
                    if (e.candidate.type === 'srflx') srflx = 1;
                    else if (e.candidate.type === 'host') host = 1;
                };
                pc.createDataChannel('p');
                pc.createOffer()
                    .then(function (offer) { return pc.setLocalDescription(offer); })
                    .catch(function () {});
                // 2s window: STUN responses on healthy networks come in <500ms;
                // 2s gives slow-network headroom while staying well inside the
                // 3s overall fingerprint-collection deadline so the rest of the
                // async pipeline (audio/legit/perms) doesn't get dropped if ICE
                // is the laggard.
                setTimeout(function () {
                    try { pc.close(); } catch (e) {}
                    resolve({ count: count, srflx: srflx, host: host, errored: 0 });
                }, 2000);
            } catch (e) {
                resolve({ count: 0, srflx: 0, host: 0, errored: 1 });
            }
        });
    }

    // === [PRIMARY] FingerprintJS BotD integration ============================
    // Dynamic-import the BotD bundle (MIT-licensed, ~12KB) when configured.
    // BotD covers the long tail of automation framework markers we'd otherwise
    // have to maintain ourselves (Selenium, Puppeteer, Playwright, PhantomJS,
    // CefSharp, Awesomium, Nightmare, plus 40+ distinctive-property
    // fingerprints). StyloBot's own probes (CDP getter trap, CDP Runtime,
    // ICE, TTS, mobile-UA / connection-type) target the modern cloak
    // ecosystem BotD doesn't address; the two are complementary.
    //
    // Returns null when disabled (no URL configured), {bot, kind, errored}
    // otherwise. The probe always passes monitoring:false to BotD so neither
    // the NPM nor the CDN telemetry path fires. License attribution lives in
    // the BotD bundle the operator serves; we don't bundle it.
    function botdProbe() {
        return new Promise(function (resolve) {
            try {
                var url = c.botdUrl;
                if (!url) return resolve(null);
                // 2s budget for the whole BotD load+detect. Slow-network
                // operators can increase the script timeout via cfg.timeout;
                // here we just want to ensure ICE+TTS+the rest don't get
                // dropped if BotD is the laggard.
                var settled = false;
                var settle = function (v) { if (!settled) { settled = true; resolve(v); } };
                setTimeout(function () { settle({ bot: 0, kind: null, errored: 1 }); }, 2000);
                import(/* @vite-ignore */ url)
                    .then(function (mod) {
                        if (!mod || typeof mod.load !== 'function')
                            return settle({ bot: 0, kind: null, errored: 1 });
                        return mod.load({ monitoring: false });
                    })
                    .then(function (detector) {
                        if (!detector) return;
                        return detector.detect();
                    })
                    .then(function (result) {
                        if (!result) return;
                        settle({ bot: result.bot ? 1 : 0, kind: result.botKind || null, errored: 0 });
                    })
                    .catch(function () { settle({ bot: 0, kind: null, errored: 1 }); });
            } catch (e) {
                resolve({ bot: 0, kind: null, errored: 1 });
            }
        });
    }

    // === [PRIMARY] speechSynthesis voice-list probe ==========================
    // Real Android Chrome populates getVoices() before the page script runs
    // (the TTS engine starts at boot). Fresh Redroid containers (damru's
    // session pattern) report 0 until first user gesture. Brief 200ms wait
    // for `voiceschanged` to handle real Android devices where the list is
    // populated asynchronously after a cold start.
    function ttsProbe() {
        return new Promise(function (resolve) {
            try {
                if (typeof speechSynthesis === 'undefined'
                    || typeof speechSynthesis.getVoices !== 'function')
                    return resolve({ count: -1 });
                var first = speechSynthesis.getVoices().length;
                if (first > 0) return resolve({ count: first });
                var settled = false;
                var settle = function (n) {
                    if (settled) return;
                    settled = true;
                    resolve({ count: n });
                };
                try {
                    speechSynthesis.onvoiceschanged = function () {
                        settle(speechSynthesis.getVoices().length);
                    };
                } catch (e) {}
                setTimeout(function () {
                    settle(speechSynthesis.getVoices().length);
                }, 200);
            } catch (e) {
                resolve({ count: -1 });
            }
        });
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
    // === [SUPPORTING] Mouse-event distribution sampling =====================
    // Samples up to N mousemove events to extract distribution features that
    // catch Kameleo Chroma's synthesised mouse traces. Kameleo modifies CDP
    // synthetic mouse events with characteristic coordinate normalisation
    // (per their published audit at kameleo.io/masking-audit): integer-only
    // coordinates (no sub-pixel drift) plus artificially regular timing.
    // Real human mouse movement has sub-pixel float coords and irregular dt.
    //
    // Privacy: we keep counts and statistical aggregates only. Raw coords
    // never leave the browser. Sample cap (50) bounds CPU + payload size.
    var mouseSampleCap = 50;
    var mouseMoveCount = 0;
    var mouseDownCount = 0;
    var mouseAllInt = 1;  // 1 = every sampled coord was integer; 0 = saw a sub-pixel
    var mouseSamples = []; // ms timestamps only (no coords)
    if (c.collectInteraction !== false) {
        try {
            var markInteracted = function () { interacted = 1; };
            window.addEventListener('mousedown', function () { interacted = 1; mouseDownCount++; }, { passive: true });
            window.addEventListener('touchstart', markInteracted, { once: true, passive: true });
            window.addEventListener('keydown', markInteracted, { once: true, passive: true });
            var sampleMove = function (e) {
                interacted = 1;
                mouseMoveCount++;
                if (mouseSamples.length < mouseSampleCap) {
                    mouseSamples.push(performance.now());
                    // Integer-coord check: Kameleo's CDP-synthesised events
                    // produce whole-pixel coordinates. Real mice drift via
                    // sub-pixel device-pixel ratios when DPR != 1.
                    if (mouseAllInt === 1
                        && (e.clientX !== Math.floor(e.clientX) || e.clientY !== Math.floor(e.clientY)))
                        mouseAllInt = 0;
                }
            };
            window.addEventListener('mousemove', sampleMove, { passive: true });
        } catch (e) {}
    }

    // Compute distribution aggregates over the captured samples.
    // dtMean, dtStddev describe inter-event timing regularity. Coefficient
    // of variation (stddev / mean) below ~0.3 indicates Kameleo-like
    // synthesised timing; real humans run > 0.5.
    function mouseStats() {
        var out = {
            count: mouseMoveCount,
            downs: mouseDownCount,
            // Sentinels: -1 means "no samples captured"; allInt is only
            // meaningful when we have samples to judge.
            allInt: mouseSamples.length > 0 ? mouseAllInt : -1,
            dtMean: -1,
            dtStddev: -1
        };
        var n = mouseSamples.length;
        if (n < 3) return out;  // need >= 3 samples for a meaningful stddev
        var deltas = [];
        for (var i = 1; i < n; i++) deltas.push(mouseSamples[i] - mouseSamples[i - 1]);
        var sum = 0;
        for (var j = 0; j < deltas.length; j++) sum += deltas[j];
        var mean = sum / deltas.length;
        var sqSum = 0;
        for (var k = 0; k < deltas.length; k++) {
            var d = deltas[k] - mean;
            sqSum += d * d;
        }
        out.dtMean = mean;
        out.dtStddev = Math.sqrt(sqSum / deltas.length);
        return out;
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
            legitimacyMarkers(),
            iceProbe(),
            ttsProbe(),
            botdProbe()
        ]);
        var asyncOrTimeout = Promise.race([asyncPart, deadline.then(function () { return null; })]);
        var asyncResult = await asyncOrTimeout || [];

        var payload = {
            t: cfg.t,
            v: '2.1.0',
            ts: Date.now(),
            cdp: cdpHit,
            stack: stackFrames(),
            ua: asyncResult[0] || null,
            perms: asyncResult[1] || null,
            bcast: asyncResult[2] || null,
            audio: asyncResult[3] || null,
            legit: asyncResult[4] || { brave: 0, lockdown: 0 },
            ice: asyncResult[5] || { count: 0, srflx: 0, host: 0, errored: 1 },
            tts: asyncResult[6] || { count: -1 },
            botd: asyncResult[7] || null,
            clamp: clampProbe(),
            triple: chromiumTriple(),
            features: versionFeatures(),
            engine: engineProbes(),
            headless: headlessMarkers(),
            touch: touchProfile(),
            basics: basics(),
            canvas: canvasHash(),
            webgl: webglRenderer(),
            tail: longTail(),
            mouse: mouseStats(),
            interacted: interacted
        };
        return payload;
    }

    // === Transport ===========================================================
    // base64 of an ArrayBuffer (for the HMAC bytes).
    function b64(buf) {
        var bytes = new Uint8Array(buf), bin = '';
        for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin);
    }

    // Bind the payload to the browser token: HMAC-SHA256(key = token, msg = body).
    // The server (which validated the single-use, IP-bound token) recomputes and
    // verifies this, so an off-browser replay of a canned payload with a captured
    // token is rejected. The token is client-held, so this proves channel
    // provenance + payload integrity, NOT value truthfulness -- the consistency
    // check does that. Returns null when SubtleCrypto is unavailable (insecure
    // context) so the beacon still sends unsigned (server verifies only when
    // present unless RequirePayloadSignature is set).
    async function signBody(json) {
        try {
            if (!cfg.t || typeof window.crypto === 'undefined'
                || !crypto.subtle || typeof TextEncoder === 'undefined') return null;
            var enc = new TextEncoder();
            var key = await crypto.subtle.importKey(
                'raw', enc.encode(cfg.t), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
            var mac = await crypto.subtle.sign('HMAC', key, enc.encode(json));
            return b64(mac);
        } catch (e) { return null; }
    }

    async function send(payload) {
        var json = JSON.stringify(payload);
        var headers = { 'Content-Type': 'application/json' };
        // Header names are operator-configurable (white-labelling); the bootstrap
        // supplies them, with StyloBot defaults. Echo the token in the header (the
        // endpoint prefers the header; the body `t` is the sendBeacon fallback since
        // sendBeacon cannot set headers).
        var tokenHeader = c.th || 'X-SB-Client-Token';
        var sigHeader = c.sh || 'X-SB-Client-Sig';
        if (cfg.t) headers[tokenHeader] = cfg.t;
        var sig = await signBody(json);
        if (sig) headers[sigHeader] = sig;
        // Prefer fetch+keepalive (gives a real response if anyone listens);
        // sendBeacon fallback for unload-time delivery. Both are
        // CSP-friendly via the 'connect-src' directive.
        try {
            if (typeof fetch === 'function') {
                fetch(endpoint, {
                    method: 'POST',
                    headers: headers,
                    body: json,
                    keepalive: true,
                    credentials: 'omit',
                    mode: 'cors'
                }).catch(function () {
                    // Fall through to sendBeacon on fetch error (unsigned: no headers)
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
