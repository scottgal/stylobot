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

    // Startup grace period: don't fire any client-side refresh in the first
    // N milliseconds after page load even if SignalR delivers beacons during
    // that window. The dashboard is freshly SSR-rendered; an immediate
    // refresh tears down DOM nodes the operator's eye just locked onto.
    // Matches the server-side broadcast cap so cold-start and steady-state
    // share the same cadence -- one refresh every BroadcastMinIntervalMs
    // (default 10s), no exceptions.
    var STARTUP_GRACE_MS = parseInt(thisScript.dataset.startupGrace, 10);
    if (!(STARTUP_GRACE_MS > 0)) STARTUP_GRACE_MS = 10000;
    var loadedAt = Date.now();
    function inStartupGrace() {
        return (Date.now() - loadedAt) < STARTUP_GRACE_MS;
    }

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

    // ----- BroadcastDirty tick-skip state ------------------------------------
    // Set to true the first time the server sends a BroadcastDirty beacon.
    // Once set, the legacy BroadcastInvalidation handler early-returns so the
    // same change is never processed twice. Old servers that only emit
    // BroadcastInvalidation never set this flag, so the legacy path keeps
    // working untouched.
    var serverSendsDirty = false;

    // Last-seen beacon tick per widget id. Populated after each OOB batch swap
    // so a repeat beacon at the same tick skips already-fresh widgets.
    var widgetTicks = {};

    // Set by flush() just before it fires an htmx.ajax call so the
    // htmx:afterSettle listener can stamp the refreshed widgets with the
    // tick that was current when the request was issued.
    var pendingSwapIds = [];
    var pendingSwapTick = 0;

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
        // If the server is emitting BroadcastDirty beacons, the new handler
        // processes all changes; skip here to avoid double-processing the same
        // change through both code paths.
        if (serverSendsDirty) return;
        if (isPaused()) return;
        var widgetMap = getWidgetMap();
        var widgets = widgetMap[signal] || [];
        widgets.forEach(function (w) { pending[w] = true; });
        clearTimeout(debounceTimer);
        // During the startup grace window, hold all queued invalidations
        // and fire them in one batch when the window closes -- so the
        // first flush is at most STARTUP_GRACE_MS after page load,
        // regardless of how many beacons arrive in the interim.
        var delay = inStartupGrace()
            ? Math.max(DEBOUNCE_MS, STARTUP_GRACE_MS - (Date.now() - loadedAt))
            : DEBOUNCE_MS;
        debounceTimer = setTimeout(flush, delay);
    }

    function flush() {
        if (isPaused()) { pending = {}; return; }
        // Belt-and-braces: even if a setTimeout fires before its calculated
        // grace-bounded delay (clock-skew, tab-throttling, debugger pauses)
        // refuse to flush during the startup grace window. Re-schedule the
        // flush to fire exactly when the grace window closes; the pending
        // set is preserved so no signals are lost.
        if (inStartupGrace()) {
            var remaining = STARTUP_GRACE_MS - (Date.now() - loadedAt);
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(flush, Math.max(50, remaining));
            return;
        }
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
            // Stash the ids and beacon tick so the htmx:afterSettle listener
            // below can stamp each refreshed widget. pendingTick is 0 when
            // using the legacy BroadcastInvalidation path, disabling tick-skip
            // (widgets start at tick 0 too, so the skip is a no-op either way).
            pendingSwapIds = ids.slice();
            pendingSwapTick = flush._pendingTick || 0;
            flush._pendingTick = 0;

            function doAjax() {
                htmx.ajax('GET', url, { target: 'body', swap: 'none' });
            }

            // Wrap in a view-transition when the browser supports it.
            // Feature-detect; don't assume support (Chrome 111+; Safari 18+).
            if (document.startViewTransition) {
                document.startViewTransition(doAjax);
            } else {
                doAjax();
            }
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
        // Stamp each widget that was refreshed in the last batch swap with the
        // beacon tick that triggered it. The in-memory widgetTicks map is the
        // primary freshness record; data-sb-tick on the DOM element is the
        // durable (SSR-stamped) baseline and survives a re-read of the file.
        // We clear pendingSwapIds after stamping so a second afterSettle (e.g.
        // a user-initiated swap) doesn't re-stamp with a stale tick.
        if (pendingSwapTick > 0 && pendingSwapIds.length > 0) {
            var stampTick = pendingSwapTick;
            var stampIds = pendingSwapIds;
            pendingSwapIds = [];
            pendingSwapTick = 0;
            stampIds.forEach(function (wid) {
                widgetTicks[wid] = stampTick;
                var el = document.querySelector('[data-sb-widget="' + wid + '"]');
                if (el) el.setAttribute('data-sb-tick', String(stampTick));
            });
        }

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
        // Broadcast to external surfaces (e.g. the scope bar's LIVE indicator):
        // any element with [data-sb-live-dot] gets data-live="connected|connecting|paused|disconnected".
        try {
            window.dispatchEvent(new CustomEvent('sb:live-status', { detail: state }));
            document.querySelectorAll('[data-sb-live-dot]').forEach(function (el) {
                el.setAttribute('data-live', state);
            });
        } catch (e) { /* ignore */ }
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

    // BroadcastDirty — richer structured beacon emitted by updated servers
    // alongside BroadcastInvalidation (for back-compat). beacon = { tick: <number>,
    // dirtyKinds: <string[]> }. On first receipt we set serverSendsDirty so the
    // legacy invalidate() handler no-ops and we don't process the change twice.
    connection.on('BroadcastDirty', function (beacon) {
        if (!beacon) return;
        if (isPaused()) return;

        // Signal to the legacy BroadcastInvalidation handler that it should
        // no-op from now on -- this server sends BroadcastDirty so we handle
        // changes here exclusively.
        serverSendsDirty = true;

        var tick = (beacon.tick != null) ? Number(beacon.tick) : 0;
        var dirtyKinds = beacon.dirtyKinds || [];
        if (dirtyKinds.length === 0) return;

        // Walk every widget on the page; mark it pending only when:
        //   (a) its data-sb-depends intersects the beacon's dirtyKinds, AND
        //   (b) its current tick is stale (data-sb-tick < beacon.tick).
        // The existing in-memory widgetTicks map is the primary freshness
        // record; data-sb-tick on the DOM element provides an SSR-stamped
        // baseline for widgets that have never been refreshed by this client.
        document.querySelectorAll('[data-sb-widget]').forEach(function (el) {
            var wid = el.getAttribute('data-sb-widget');
            if (!wid) return;

            // Resolve the widget's current tick: prefer in-memory (set after
            // the last swap), fall back to the DOM attribute stamped by the
            // server on SSR or by the previous client-side swap.
            var currentTick = widgetTicks[wid];
            if (currentTick == null) {
                currentTick = parseInt(el.getAttribute('data-sb-tick') || '0', 10);
                if (isNaN(currentTick)) currentTick = 0;
            }

            // Skip if the widget is already at or ahead of the beacon tick.
            if (currentTick >= tick) return;

            // Check whether any of the widget's declared dependencies intersect
            // the beacon's dirtyKinds.
            var deps = (el.getAttribute('data-sb-depends') || '').split(',');
            var matches = false;
            for (var i = 0; i < deps.length; i++) {
                var dep = deps[i].trim();
                if (!dep) continue;
                if (dirtyKinds.indexOf(dep) !== -1) { matches = true; break; }
            }
            if (!matches) return;

            pending[wid] = true;
        });

        if (Object.keys(pending).length === 0) return;

        // Stash the beacon tick so flush() can stamp widgets after the swap.
        flush._pendingTick = tick;

        // Feed into the same debounce / startup-grace / user-active-gating
        // path as the legacy invalidation handler.
        clearTimeout(debounceTimer);
        var delay = inStartupGrace()
            ? Math.max(DEBOUNCE_MS, STARTUP_GRACE_MS - (Date.now() - loadedAt))
            : DEBOUNCE_MS;
        debounceTimer = setTimeout(flush, delay);
    });

    // HR2 -- fingerprint name-slot rename beacon. The commercial editor POST
    // (and the HR1 Redis subscriber on every peer gateway) fires this with the
    // affected fingerprint id + slot kind. We find every .fp-name wrapper on
    // the page bound to that fingerprint (visitor list rows + signature detail
    // header both carry data-fp-id) and HTMX-swap each from the commercial
    // /given-name/read fragment so the row repaints without an F5.
    //
    // The fragment endpoint is gated by the same StyloBotApiKey policy as the
    // editor itself; the operator dashboard's outbound fetch carries the
    // session cookie / key the editor pencil already relies on, so a swap
    // failure here is the same auth state as a failed editor click. We
    // intentionally do NOT route this through the widget invalidation +
    // batched flush path because the swap target is row-local (not widget-
    // wide) and we don't want a name edit to trigger a full widget refresh.
    connection.on('fingerprint:dirty', function (fingerprintId, slot) {
        if (isPaused()) return;
        if (!fingerprintId) return;
        if (typeof htmx === 'undefined') return;
        // Escape the id with CSS.escape so a fingerprint containing
        // unexpected punctuation can't break the selector. Older browsers
        // (we still target IE-class behaviour for safety on legacy intranet
        // dashboards) fall back to a literal selector which is fine for the
        // hex / base64 ids stylobot mints.
        var safeId = (window.CSS && CSS.escape)
            ? CSS.escape(fingerprintId)
            : fingerprintId.replace(/"/g, '\\"');
        var nodes = document.querySelectorAll('.fp-name[data-fp-id="' + safeId + '"]');
        if (!nodes || nodes.length === 0) return;
        var url = '/api/v1/commercial/fingerprints/'
                + encodeURIComponent(fingerprintId)
                + '/given-name/read';
        nodes.forEach(function (node) {
            htmx.ajax('GET', url, { target: node, swap: 'outerHTML' });
        });
        // slot is ignored on the client today -- every name slot resolves
        // through the same /read endpoint so the swap covers given/llm/induced
        // identically. Keep the parameter in the wire shape so future per-slot
        // handlers don't need a contract change.
        void slot;
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
