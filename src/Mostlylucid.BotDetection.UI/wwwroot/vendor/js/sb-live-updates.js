/*
 * StyloBot live-updates coordinator.
 *
 * Loaded by <sb-live-updates /> -- the tag helper emits one <script src> with
 * config on data-* attributes (data-base-path, data-hub-url, data-debounce,
 * data-cooldown, data-refresh-interval). No inline script, no CDN; this file
 * ships as a static web asset under /_content/Mostlylucid.BotDetection.UI/
 * vendor/js/sb-live-updates.js.
 *
 * Behaviour: subscribe to the SignalR hub's BroadcastInvalidation beacons,
 * debounce, and re-fetch the affected widget partials via HTMX OOB swaps.
 * User-active widgets (mouse over, focus in, in-flight htmx request, or
 * recent settle within the cooldown window) refuse SignalR-driven swaps so
 * the user's filter / sort / page selection survives every beacon.
 *
 * Requires htmx and the SignalR client to be loaded before this script.
 */
(function () {
    'use strict';

    var thisScript = document.currentScript;
    if (!thisScript) {
        // Older browsers, or this script was injected dynamically. Fall back to
        // looking up the first <script> with the known src so the data-* config
        // is still reachable.
        var scripts = document.getElementsByTagName('script');
        for (var i = 0; i < scripts.length; i++) {
            if (scripts[i].src && scripts[i].src.indexOf('sb-live-updates.js') !== -1) {
                thisScript = scripts[i];
                break;
            }
        }
    }
    if (!thisScript) {
        console.warn('StyloBot: sb-live-updates.js cannot locate its own <script> tag; config unreadable');
        return;
    }

    var BASE = thisScript.dataset.basePath || '/_stylobot';
    var HUB = thisScript.dataset.hubUrl || (BASE + '/hub');
    var DEBOUNCE_MS = parseInt(thisScript.dataset.debounce, 10);
    if (!(DEBOUNCE_MS > 0)) DEBOUNCE_MS = 500;
    var COOLDOWN_MS = parseInt(thisScript.dataset.cooldown, 10);
    if (!(COOLDOWN_MS > 0)) COOLDOWN_MS = 3000;
    var REFRESH_MS = parseInt(thisScript.dataset.refreshInterval, 10);
    if (isNaN(REFRESH_MS)) REFRESH_MS = 30000;

    // ----- Live-updates pause toggle ------------------------------------
    // localStorage key: 'sb:live-updates' = 'live' (default) | 'paused'.
    // When paused, the coordinator refuses to fire any flush and stays
    // disconnected from the SignalR hub. Toggled by the <button id="sb-live-toggle">
    // emitted by the SbLiveUpdates tag helper. State is per-browser and
    // survives reloads -- the operator doesn't want to re-pause every navigation.
    var LIVE_KEY = 'sb:live-updates';
    function isPaused() {
        try { return localStorage.getItem(LIVE_KEY) === 'paused'; }
        catch (e) { return false; }
    }
    function setPaused(paused) {
        try { localStorage.setItem(LIVE_KEY, paused ? 'paused' : 'live'); }
        catch (e) { /* ignore quota / disabled storage */ }
    }
    var toggleBtn = document.getElementById('sb-live-toggle');
    var toggleLabel = document.getElementById('sb-live-toggle-label');
    function renderToggle() {
        if (!toggleBtn) return;
        var paused = isPaused();
        toggleBtn.dataset.state = paused ? 'paused' : 'live';
        toggleBtn.title = paused ? 'Live updates: paused (click to resume)'
                                 : 'Live updates: on (click to pause)';
        if (toggleLabel) toggleLabel.textContent = paused ? 'PAUSED' : 'LIVE';
    }
    if (toggleBtn) {
        toggleBtn.addEventListener('click', function () {
            var nowPaused = !isPaused();
            setPaused(nowPaused);
            renderToggle();
            if (nowPaused) {
                // Tear down the active SignalR connection so we're truly idle
                // (no socket traffic, no heartbeats) until resumed.
                try { connection && connection.stop && connection.stop(); }
                catch (e) { /* ignore */ }
                setStatus('paused');
            } else {
                // Resume: re-establish the SignalR connection.
                try {
                    connection && connection.start && connection.start()
                        .then(function () { setStatus('connected'); })
                        .catch(function () { setStatus('disconnected'); });
                } catch (e) { setStatus('disconnected'); }
            }
        });
    }
    renderToggle();

    function getWidgetMap() {
        var map = {};
        document.querySelectorAll('[data-sb-widget]').forEach(function (el) {
            var deps = (el.getAttribute('data-sb-depends') || '').split(',');
            deps.forEach(function (dep) {
                dep = dep.trim();
                if (!dep) return;
                if (!map[dep]) map[dep] = [];
                var wid = el.getAttribute('data-sb-widget');
                if (map[dep].indexOf(wid) === -1) map[dep].push(wid);
            });
        });
        return map;
    }

    var pending = {};
    var debounceTimer = null;

    // General pattern: a widget the user is actively driving (filter click, sort header,
    // page nav, any in-flight user-initiated HTMX request whose source sits inside that
    // widget) WINS over SignalR-driven background refreshes. We track that set here and
    // gate both directions:
    //   - outgoing refresh requests skip user-active widgets (no stale params sent),
    //   - incoming OOB swap responses targeting user-active widgets are refused (no
    //     race where a SignalR refresh already in flight clobbers the just-clicked filter).
    var userActiveWidgets = new Set();

    function widgetForElt(elt) {
        if (!elt || !elt.closest) return null;
        var root = elt.closest('[data-sb-widget]');
        return root ? root.getAttribute('data-sb-widget') : null;
    }

    document.body.addEventListener('htmx:beforeRequest', function (ev) {
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.add(wid);
    });

    // After the user's swap settles, hold user-active for a brief COOLDOWN so any
    // SignalR refresh whose REQUEST was fired BEFORE the user click but whose RESPONSE
    // arrives AFTER the user settle still gets refused. Without the cooldown that
    // late-arriving OOB swap silently restores the pre-click state -- the operator
    // clicks Bot, sees bots for ~200ms, then the widget flips back to All.
    var cooldownTimers = {};
    function scheduleRelease(wid) {
        if (!wid) return;
        if (cooldownTimers[wid]) clearTimeout(cooldownTimers[wid]);
        cooldownTimers[wid] = setTimeout(function () {
            userActiveWidgets.delete(wid);
            delete cooldownTimers[wid];
        }, COOLDOWN_MS);
    }
    function holdActive(wid) {
        if (!wid) return;
        userActiveWidgets.add(wid);
        if (cooldownTimers[wid]) {
            clearTimeout(cooldownTimers[wid]);
            delete cooldownTimers[wid];
        }
    }

    document.body.addEventListener('htmx:afterSettle', function (ev) {
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) scheduleRelease(wid);
    });

    // Hover protection: while the cursor is over a widget, treat it as user-active
    // so SignalR-driven destructive swaps do not yank links out from under the
    // pointer mid-hover.
    document.body.addEventListener('mouseenter', function (ev) {
        var wid = widgetForElt(ev.target);
        if (wid) holdActive(wid);
    }, true);
    document.body.addEventListener('mouseleave', function (ev) {
        var wid = widgetForElt(ev.target);
        if (wid) scheduleRelease(wid);
    }, true);
    document.body.addEventListener('focusin', function (ev) {
        var wid = widgetForElt(ev.target);
        if (wid) holdActive(wid);
    });
    document.body.addEventListener('focusout', function (ev) {
        var wid = widgetForElt(ev.target);
        if (wid) scheduleRelease(wid);
    });

    // Belt-and-braces release on errored requests -- no swap, no settle, so no
    // afterSettle handler runs. Clear without cooldown.
    document.body.addEventListener('htmx:responseError', function (ev) {
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    });
    document.body.addEventListener('htmx:sendError', function (ev) {
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    });

    // Global row-click delegation: any element carrying data-href becomes a clickable
    // row that navigates to that URL. Skip if a real interactive child was clicked.
    document.body.addEventListener('click', function (ev) {
        if (ev.target.closest('a, button, input, select, textarea, label, [hx-get], [hx-post]')) return;
        var row = ev.target.closest('[data-href]');
        if (!row) return;
        var href = row.getAttribute('data-href');
        if (href) window.location.href = href;
    });

    // Refuse OOB swaps targeting a user-active widget. The beacon's OOB sub-swap
    // target can be either the widget root or an inner [data-sb-data-region]; walk
    // up to the enclosing widget so inner-region swaps are gated by the same check.
    document.body.addEventListener('htmx:oobBeforeSwap', function (ev) {
        var target = ev.detail && ev.detail.target;
        if (!target || !target.closest) return;
        var widgetRoot = target.closest('[data-sb-widget]');
        if (!widgetRoot) return;
        var wid = widgetRoot.getAttribute('data-sb-widget');
        if (wid && userActiveWidgets.has(wid)) {
            ev.detail.shouldSwap = false;
        }
    });

    function invalidate(signal) {
        if (isPaused()) return;
        var widgetMap = getWidgetMap();
        var widgets = widgetMap[signal] || [];
        widgets.forEach(function (w) { pending[w] = true; });
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(flush, DEBOUNCE_MS);
    }

    function flush() {
        if (isPaused()) { pending = {}; return; }
        var ids = Object.keys(pending).filter(function (id) {
            return !userActiveWidgets.has(id);
        });
        pending = {};
        if (ids.length === 0) return;

        var qs = new URLSearchParams();
        qs.set('widgets', ids.join(','));

        ids.forEach(function (wid) {
            var el = document.querySelector('[data-sb-widget="' + wid + '"]');
            if (!el) return;
            var raw = el.getAttribute('data-sb-params');
            if (!raw) return;
            try {
                new URLSearchParams(raw).forEach(function (val, key) {
                    if (val !== '' && val !== 'undefined' && val !== 'null') {
                        qs.set(wid + '.' + key, val);
                    }
                });
            } catch (e) { /* ignore malformed params */ }
        });

        var url = BASE + '/partials/update?' + qs.toString();
        if (typeof htmx !== 'undefined') {
            // Plain htmx.ajax, NOT startViewTransition. The beacon swaps a small
            // data-region innerHTML in under one frame; a view-transition overlay
            // would freeze cursor / hover state for ~160ms each time the beacon
            // fires (multiple per second).
            htmx.ajax('GET', url, { target: 'body', swap: 'none' });
        }
    }

    // Carry user-driven filter/sort/page across tabs so switching dashboard tabs
    // preserves the last filter selection. Page-author keys (pageSize, widgetId)
    // always come from the fresh SSR data-sb-params -- never from sessionStorage --
    // to avoid an overview-tab pageSize=10 clobbering an activity-tab pageSize=25.
    var USER_DRIVEN_KEYS = ['filter', 'sort', 'dir', 'page'];
    document.querySelectorAll('[data-sb-widget]').forEach(function (el) {
        var wid = el.getAttribute('data-sb-widget');
        var saved = sessionStorage.getItem('sb:wp:' + wid);
        if (!saved) return;
        try {
            var fresh = new URLSearchParams(el.getAttribute('data-sb-params') || '');
            var savedParams = new URLSearchParams(saved);
            USER_DRIVEN_KEYS.forEach(function (key) {
                if (savedParams.has(key)) fresh.set(key, savedParams.get(key));
            });
            el.setAttribute('data-sb-params', fresh.toString());
        } catch (e) { /* malformed saved params, ignore */ }
    });

    document.body.addEventListener('htmx:afterSettle', function () {
        document.querySelectorAll('[data-sb-widget]').forEach(function (el) {
            var wid = el.getAttribute('data-sb-widget');
            var params = el.getAttribute('data-sb-params');
            if (wid && params) sessionStorage.setItem('sb:wp:' + wid, params);
        });
    });

    if (typeof signalR === 'undefined') {
        console.warn('StyloBot: signalR client not loaded; live updates disabled');
        return;
    }

    var statusEl = document.getElementById('sb-connection-status');
    function setStatus(state) {
        if (!statusEl) return;
        statusEl.className = 'w-2 h-2 rounded-full sb-' + state;
        statusEl.title = 'SignalR: ' + state;
    }

    var connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB)
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    // SignalR is beacon-only: server sends lightweight invalidation signals,
    // client triggers HTMX partial refreshes. No data payloads over the wire.
    connection.on('BroadcastInvalidation', function (signal) {
        if (signal) invalidate(signal);
    });

    connection.onreconnecting(function () { setStatus('connecting'); });
    connection.onreconnected(function () { setStatus('connected'); });
    connection.onclose(function () { setStatus('disconnected'); });

    // Honour the live-updates toggle on first paint: if the operator left
    // the dashboard in 'paused' state, do not auto-connect. The connection
    // is established only when the toggle flips back to 'live'.
    if (isPaused()) {
        setStatus('paused');
    } else {
        connection.start()
            .then(function () { setStatus('connected'); })
            .catch(function () { setStatus('disconnected'); });
    }

    // SignalR is push-only. No setInterval-based forced refresh -- the design
    // is that nothing happens to widgets except in response to a beacon, and
    // the morph swap means even a beacon only mutates rows whose data actually
    // changed. REFRESH_MS is kept as a configuration knob for back-compat but
    // is intentionally not consumed.
    void REFRESH_MS;
})();
