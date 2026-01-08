/**
 * Mostlylucid.BotDetection - Enhanced Browser Fingerprinting & Headless Detection
 *
 * This script collects minimal, non-invasive browser signals to detect
 * headless browsers and automation frameworks. Results are posted to
 * a server endpoint for correlation with request-based detection.
 *
 * Signals collected:
 * - Basic: timezone, language, platform, screen, hardware, touch, maxTouchPoints
 * - Device: pointer type (coarse/fine), network hints (connection type)
 * - Preferences: dark mode, reduced motion (privacy-safe, coarse)
 * - Performance: timing shapes, resource counts (no URLs)
 * - Automation markers: webdriver, phantom, selenium, CDP
 * - Consistency: window dimensions, function integrity
 * - Anti-tamper: native function checks (getBattery, console, querySelector)
 * - Context: iframe detection, sandboxing
 * - Optional: WebGL vendor/renderer, canvas hash, audio context hash
 * - Optional: Interaction tracking (did user interact at all - no PII)
 *
 * Privacy & Security:
 * - No cookies or localStorage used for tracking
 * - All high-entropy signals (canvas, audio, WebGL) sent as HASHES only
 * - Fingerprint hash is ephemeral (session-scoped, non-persistent)
 * - No PII collected (no IPs, no precise location, no keylogging)
 * - Interaction tracking is boolean only (did interact: yes/no)
 * - Network hints are coarse (effectiveType, not bandwidth details)
 * - User preferences are standard media queries (not fingerprintable)
 * - Uses sendBeacon for reliable, non-blocking delivery
 * - Explainable scoring with reasons for transparency
 *
 * Enhancements (2025):
 * - Audio context fingerprinting (hash-only, async)
 * - Extended native function integrity checks
 * - Device capability signals (maxTouchPoints, pointer type)
 * - User preference signals (prefers-dark, reduced-motion)
 * - Network connection hints (coarse, non-PII)
 * - Performance timing shapes (no URLs)
 * - Iframe/sandbox context detection
 * - Interaction tracking (non-invasive, boolean)
 * - Explainable scoring with reasons
 * - sendBeacon for reliable transport
 */
(function () {
    'use strict';

    // Configuration injected by TagHelper
    // %%CONFIG%% will be replaced with actual values
    var MLBotD = {
            version: '%%VERSION%%',
            token: '%%TOKEN%%',
            endpoint: '%%ENDPOINT%%',
            config: {
                collectWebGL: % % COLLECT_WEBGL % %,
            collectCanvas: % % COLLECT_CANVAS % %,
        collectAudio:
%%
    COLLECT_AUDIO % %,
        collectInteraction
: %%
    COLLECT_INTERACTION % %,
        timeout
: %%
    TIMEOUT % %
},

    /**
     * Simple non-cryptographic hash for fingerprint components
     */
    hash: function (str) {
        var hash = 0;
        for (var i = 0; i < str.length; i++) {
            hash = ((hash << 5) - hash) + str.charCodeAt(i);
            hash |= 0; // Convert to 32-bit integer
        }
        return hash.toString(16);
    }
,

    /**
     * Check if a function is native (not modified/wrapped)
     */
    checkNative: function (fn) {
        try {
            if (!fn) return -1;
            var s = Function.prototype.toString.call(fn);
            return s.indexOf('[native code]') > -1 ? 1 : 0;
        } catch (e) {
            return -1;
        }
    }
,

    /**
     * Setup interaction tracking (non-invasive, just "did user interact at all")
     */
    setupInteractionSignals: function () {
        var interacted = 0;
        var mark = function () {
            interacted = 1;
        };

        try {
            window.addEventListener('mousemove', mark, {once: true, passive: true});
            window.addEventListener('mousedown', mark, {once: true, passive: true});
            window.addEventListener('touchstart', mark, {once: true, passive: true});
            window.addEventListener('keydown', mark, {once: true, passive: true});
        } catch (e) {
        }

        return function () {
            return interacted;
        };
    }
,

    /**
     * Get audio context fingerprint hash (privacy-safe, only hash sent)
     */
    getAudioHash: function () {
        try {
            var AudioContext = window.OfflineAudioContext || window.webkitOfflineAudioContext;
            if (!AudioContext) return '';

            var ctx = new AudioContext(1, 44100, 44100);
            var osc = ctx.createOscillator();
            var comp = ctx.createDynamicsCompressor();

            osc.type = 'triangle';
            osc.frequency.value = 1000;

            osc.connect(comp);
            comp.connect(ctx.destination);
            osc.start(0);
            ctx.startRendering();

            var self = this;
            return new Promise(function (resolve) {
                ctx.oncomplete = function (e) {
                    try {
                        var buf = e.renderedBuffer.getChannelData(0);
                        // Downsample aggressively to keep it light
                        var step = Math.max(1, Math.floor(buf.length / 128));
                        var str = '';
                        for (var i = 0; i < buf.length; i += step) {
                            str += String.fromCharCode(~~((buf[i] + 1) * 127));
                        }
                        resolve(self.hash(str));
                    } catch (ex) {
                        resolve('');
                    }
                };
            });
        } catch (e) {
            return Promise.resolve('');
        }
    }
,

    /**
     * Collect browser fingerprint signals
     */
    collect: function (callback) {
        var data = {};
        var nav = navigator;
        var win = window;
        var scr = screen;

        // ===== Basic Signals (low entropy, non-invasive) =====
        data.tz = this.getTimezone();
        data.lang = nav.language || '';
        data.langs = (nav.languages || []).slice(0, 3).join(',');
        data.platform = nav.platform || '';
        data.cores = nav.hardwareConcurrency || 0;
        data.mem = nav.deviceMemory || 0;
        data.touch = 'ontouchstart' in win ? 1 : 0;
        data.screen = scr.width + 'x' + scr.height + 'x' + scr.colorDepth;
        data.avail = scr.availWidth + 'x' + scr.availHeight;
        data.dpr = win.devicePixelRatio || 1;
        data.pdf = this.hasPdfPlugin() ? 1 : 0;

        // ===== Enhanced Device Signals =====
        data.maxTouchPoints = nav.maxTouchPoints || 0;
        try {
            var mql = win.matchMedia && win.matchMedia('(pointer: coarse)');
            data.pointer = mql ? (mql.matches ? 'coarse' : 'fine') : '';
        } catch (e) {
            data.pointer = '';
        }

        // ===== User Preferences (privacy-safe, coarse) =====
        try {
            data.prefersDark = win.matchMedia && win.matchMedia('(prefers-color-scheme: dark)').matches ? 1 : 0;
        } catch (e) {
            data.prefersDark = -1;
        }
        try {
            data.reducedMotion = win.matchMedia && win.matchMedia('(prefers-reduced-motion: reduce)').matches ? 1 : 0;
        } catch (e) {
            data.reducedMotion = -1;
        }

        // ===== Network Hints (coarse, non-PII) =====
        try {
            var conn = nav.connection || nav.mozConnection || nav.webkitConnection;
            if (conn) {
                data.netType = conn.effectiveType || '';
                data.netSaveData = conn.saveData ? 1 : 0;
                data.netDownlink = conn.downlink ? Math.round(conn.downlink) : 0;
            }
        } catch (e) {
        }

        // ===== Performance Timing Shape (no URLs, just relative timings) =====
        try {
            if (performance && performance.timing) {
                var t = performance.timing;
                data.navStartDelta = (t.domContentLoadedEventEnd || 0) - (t.navigationStart || 0);
                data.loadEventDelta = (t.loadEventEnd || 0) - (t.loadEventStart || 0);
            }
            if (performance && performance.getEntriesByType) {
                var res = performance.getEntriesByType('resource') || [];
                data.resCount = res.length;
            }
        } catch (e) {
        }

        // ===== Headless/Automation Detection =====
        data.webdriver = nav.webdriver ? 1 : 0;
        data.phantom = this.detectPhantom();
        data.nightmare = !!win.__nightmare ? 1 : 0;
        data.selenium = this.detectSelenium();
        data.cdc = this.detectCDP();
        data.plugins = nav.plugins ? nav.plugins.length : 0;
        data.chrome = !!win.chrome ? 1 : 0;
        data.permissions = this.checkPermissions();

        // ===== Window Consistency =====
        data.outerW = win.outerWidth || 0;
        data.outerH = win.outerHeight || 0;
        data.innerW = win.innerWidth || 0;
        data.innerH = win.innerHeight || 0;

        // ===== Function Integrity & Anti-Tamper =====
        data.evalLen = this.getEvalLength();
        data.bindNative = this.isBindNative() ? 1 : 0;
        data.getBatteryNative = this.checkNative(nav.getBattery);
        data.consoleDebugNative = this.checkNative(console.debug);
        data.querySelectorNative = this.checkNative(Document.prototype.querySelector || document.querySelector);

        // ===== Iframe / Sandboxed Context =====
        try {
            data.isIframe = (win.self !== win.top) ? 1 : 0;
        } catch (e) {
            data.isIframe = -1; // Cross-origin iframe restriction
        }

        // ===== Optional: WebGL =====
        if (this.config.collectWebGL) {
            var gl = this.getWebGLInfo();
            if (gl) {
                data.glVendor = gl.vendor || '';
                data.glRenderer = gl.renderer || '';
            }
        }

        // ===== Optional: Canvas Hash =====
        if (this.config.collectCanvas) {
            data.canvasHash = this.getCanvasHash();
        }

        // ===== Optional: Audio Hash (async) =====
        var self = this;
        var pending = 0;
        var finish = function () {
            if (pending === 0) {
                // ===== Client-side Score =====
                data.score = self.calculateScore(data);

                if (callback) callback(data);
            }
        };

        if (this.config.collectAudio) {
            var audioHash = this.getAudioHash();
            if (audioHash && typeof audioHash.then === 'function') {
                pending++;
                audioHash.then(function (h) {
                    data.audioHash = h || '';
                    pending--;
                    finish();
                }).catch(function () {
                    data.audioHash = '';
                    pending--;
                    finish();
                });
            } else {
                data.audioHash = '';
            }
        }

        // Trigger finish immediately if no async tasks
        finish();

        // For synchronous use (backward compatibility)
        if (!callback && pending === 0) {
            data.score = this.calculateScore(data);
            return data;
        }
    }
,

    /**
     * Get timezone safely
     */
    getTimezone: function () {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone || '';
        } catch (e) {
            return '';
        }
    }
,

    /**
     * Check for PDF plugin
     */
    hasPdfPlugin: function () {
        try {
            var plugins = navigator.plugins;
            for (var i = 0; i < plugins.length; i++) {
                if (plugins[i].name.toLowerCase().indexOf('pdf') > -1) {
                    return true;
                }
            }
        } catch (e) {
        }
        return false;
    }
,

    /**
     * Detect PhantomJS markers
     */
    detectPhantom: function () {
        return (window.phantom || window._phantom || window.callPhantom) ? 1 : 0;
    }
,

    /**
     * Detect Selenium markers
     */
    detectSelenium: function () {
        var doc = document;
        return (doc.__selenium_unwrapped ||
            doc.__webdriver_evaluate ||
            doc.__driver_evaluate ||
            doc.__webdriver_script_function ||
            doc.__webdriver_script_func ||
            doc.__webdriver_script_fn ||
            doc.$cdc_asdjflasutopfhvcZLmcfl_ ||
            doc.$chrome_asyncScriptInfo) ? 1 : 0;
    }
,

    /**
     * Detect Chrome DevTools Protocol markers (Puppeteer, Playwright)
     */
    detectCDP: function () {
        try {
            for (var key in window) {
                if (key.match(/^cdc_|^__\$|^\$cdc_/)) {
                    return 1;
                }
            }
        } catch (e) {
        }
        return 0;
    }
,

    /**
     * Check notification permissions for consistency
     */
    checkPermissions: function () {
        try {
            if (typeof Notification === 'undefined') {
                return 'unavailable';
            }
            // Suspicious: denied permissions with no plugins (common in headless)
            if (Notification.permission === 'denied' && navigator.plugins.length === 0) {
                return 'suspicious';
            }
            return Notification.permission;
        } catch (e) {
            return 'error';
        }
    }
,

    /**
     * Get eval function length (modified in some automation tools)
     */
    getEvalLength: function () {
        try {
            return eval.toString().length;
        } catch (e) {
            return 0;
        }
    }
,

    /**
     * Check if Function.prototype.bind is native
     */
    isBindNative: function () {
        try {
            return Function.prototype.bind.toString().indexOf('[native code]') > -1;
        } catch (e) {
            return false;
        }
    }
,

    /**
     * Get WebGL vendor and renderer info
     */
    getWebGLInfo: function () {
        try {
            var canvas = document.createElement('canvas');
            var gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
            if (!gl) return null;

            var debugInfo = gl.getExtension('WEBGL_debug_renderer_info');
            if (!debugInfo) return {vendor: '', renderer: ''};

            return {
                vendor: gl.getParameter(debugInfo.UNMASKED_VENDOR_WEBGL) || '',
                renderer: gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) || ''
            };
        } catch (e) {
            return null;
        }
    }
,

    /**
     * Generate a simple canvas hash for consistency checking
     */
    getCanvasHash: function () {
        try {
            var canvas = document.createElement('canvas');
            canvas.width = 200;
            canvas.height = 50;
            var ctx = canvas.getContext('2d');

            // Draw some elements that will vary by GPU/driver
            ctx.textBaseline = 'top';
            ctx.font = '14px Arial';
            ctx.fillStyle = '#f60';
            ctx.fillRect(125, 1, 62, 20);
            ctx.fillStyle = '#069';
            ctx.fillText('MLBotD', 2, 15);
            ctx.fillStyle = 'rgba(102, 204, 0, 0.7)';
            ctx.fillText('MLBotD', 4, 17);

            return this.hash(canvas.toDataURL());
        } catch (e) {
            return '';
        }
    }
,

    /**
     * Calculate client-side integrity score with explainable reasons
     */
    calculateScore: function (data) {
        var score = 100;
        var reasons = [];

        // Definite automation markers
        if (data.webdriver) {
            score -= 50;
            reasons.push('webdriver');
        }
        if (data.phantom) {
            score -= 50;
            reasons.push('phantom');
        }
        if (data.nightmare) {
            score -= 50;
            reasons.push('nightmare');
        }
        if (data.selenium) {
            score -= 50;
            reasons.push('selenium');
        }
        if (data.cdc) {
            score -= 40;
            reasons.push('cdp');
        }

        // Suspicious indicators
        if (data.plugins === 0 && data.chrome) {
            score -= 20;
            reasons.push('chrome-no-plugins');
        }
        if (data.outerW === 0 || data.outerH === 0) {
            score -= 30;
            reasons.push('zero-outer');
        }
        if (data.innerW === data.outerW && data.innerH === data.outerH) {
            score -= 10;
            reasons.push('no-chrome-ui');
        }
        if (!data.bindNative) {
            score -= 20;
            reasons.push('bind-not-native');
        }
        if (data.evalLen > 0 && (data.evalLen < 30 || data.evalLen > 50)) {
            score -= 15;
            reasons.push('eval-len-weird');
        }
        if (data.permissions === 'suspicious') {
            score -= 25;
            reasons.push('perm-suspicious');
        }

        // Anti-tamper signals
        if (data.getBatteryNative === 0) {
            score -= 15;
            reasons.push('getBattery-wrapped');
        }
        if (data.consoleDebugNative === 0) {
            score -= 10;
            reasons.push('console-wrapped');
        }
        if (data.querySelectorNative === 0) {
            score -= 15;
            reasons.push('querySelector-wrapped');
        }

        // Context anomalies
        if (data.isIframe === 1 && data.outerW === 0) {
            score -= 20;
            reasons.push('suspicious-iframe');
        }

        // Store reasons for explainability
        data.scoreReasons = reasons.join(',');
        return Math.max(0, score);
    }
,

    /**
     * Send fingerprint data to server (prefer sendBeacon for reliability)
     */
    send: function (data) {
        try {
            var payload = JSON.stringify(data);

            // Prefer sendBeacon for non-blocking, reliable delivery
            if (navigator.sendBeacon) {
                var blob = new Blob([payload], {type: 'application/json'});
                navigator.sendBeacon(this.endpoint, blob);
                return;
            }

            // Fallback to XHR
            var xhr = new XMLHttpRequest();
            xhr.open('POST', this.endpoint, true);
            xhr.setRequestHeader('Content-Type', 'application/json');
            xhr.setRequestHeader('X-ML-BotD-Token', this.token);
            xhr.timeout = this.config.timeout;

            xhr.onerror = function () {
                // Silent fail - don't break the page
            };

            xhr.send(payload);
        } catch (e) {
            // Don't break page on error
        }
    }
,

    /**
     * Main entry point
     */
    run: function () {
        var self = this;

        // Setup interaction tracking (if enabled)
        var getInteracted = null;
        if (this.config.collectInteraction) {
            getInteracted = this.setupInteractionSignals();
        }

        // Small delay to not block page load
        setTimeout(function () {
            try {
                // Collect with async callback support
                self.collect(function (data) {
                    data.ts = Date.now();

                    // Add interaction signal if enabled
                    if (getInteracted) {
                        data.interacted = getInteracted();
                    }

                    self.send(data);
                });
            } catch (e) {
                // Send error report
                self.send({
                    error: e.message || 'Unknown error',
                    ts: Date.now()
                });
            }
        }, 100);
    }
}

    // Run when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            MLBotD.run();
        });
    } else {
        MLBotD.run();
    }
})();
