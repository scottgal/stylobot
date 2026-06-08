/*
 * StyloBot Policy Stack realtime glue (B6).
 *
 * Each <section data-policy-stack-scope="..." data-policy-stack-ancestors="..."
 *               data-policy-stack-rows-url="..."> renders the Policy Stack
 * control. On mount this script joins one SignalR group per ancestor hash;
 * on unmount it leaves them. When the hub emits 'PolicyChanged', every section
 * whose ancestor list includes the changed scope key fires an htmx.ajax to its
 * rows-url and swaps the response in place (outerHTML on the envelope div).
 *
 * Requires htmx and the signalR client to be loaded before this script. The
 * script is silent when either is missing -- it does not log, does not throw,
 * does not interfere with anything else on the page.
 *
 * Self-hosted under /_content/Mostlylucid.BotDetection.UI/vendor/js/. The
 * dashboard middleware mounts this on every page that renders a policy
 * stack section; the sb-live-updates tag helper triggers the include from
 * the dashboard layout.
 */
(function () {
    'use strict';

    if (typeof signalR === 'undefined') return;
    if (typeof window === 'undefined') return;

    // ---------- Build / reuse the connection ----------
    //
    // sb-live-updates.js already owns the main hub connection for the
    // BroadcastInvalidation beacon. We DELIBERATELY make our own one here so
    // the policy stack lifecycle is independent: a page that doesn't include
    // sb-live-updates still gets policy-stack live updates, and a page that
    // does include it still routes PolicyChanged through the dedicated hub
    // group machinery (BroadcastInvalidation has its own per-signal flush
    // semantics that don't apply to scope-keyed beacons).

    var hubUrl = (document.querySelector('script[data-policy-stack-hub-url]') || {}).dataset
        ? document.querySelector('script[data-policy-stack-hub-url]').dataset.policyStackHubUrl
        : '/_stylobot/hub';
    var scriptEl = document.querySelector('script[src*="policy-stack-realtime.js"]');
    if (scriptEl && scriptEl.dataset && scriptEl.dataset.hubUrl) {
        hubUrl = scriptEl.dataset.hubUrl;
    }

    var connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    var connectionReady = false;
    var joinedGroups = Object.create(null);   // hash -> refcount

    function joinGroup(hash) {
        if (!hash) return;
        joinedGroups[hash] = (joinedGroups[hash] || 0) + 1;
        if (joinedGroups[hash] !== 1) return;
        if (!connectionReady) return;
        connection.invoke('JoinPolicyGroup', hash).catch(function () { /* swallow */ });
    }

    function leaveGroup(hash) {
        if (!hash) return;
        var rc = joinedGroups[hash];
        if (!rc) return;
        if (rc > 1) { joinedGroups[hash] = rc - 1; return; }
        delete joinedGroups[hash];
        if (!connectionReady) return;
        connection.invoke('LeavePolicyGroup', hash).catch(function () { /* swallow */ });
    }

    // ---------- Section lifecycle ----------

    function ancestorsOf(el) {
        var raw = el.getAttribute('data-policy-stack-ancestors') || '';
        if (!raw) return [];
        return raw.split(',').filter(function (h) { return !!h; });
    }

    function joinAll(el) { ancestorsOf(el).forEach(joinGroup); }
    function leaveAll(el) { ancestorsOf(el).forEach(leaveGroup); }

    function forEachSection(node, fn) {
        if (!(node instanceof HTMLElement)) return;
        if (node.hasAttribute && node.hasAttribute('data-policy-stack-scope')) fn(node);
        var nested = node.querySelectorAll
            ? node.querySelectorAll('[data-policy-stack-scope]')
            : [];
        for (var i = 0; i < nested.length; i++) fn(nested[i]);
    }

    // ---------- PolicyChanged dispatch ----------
    //
    // Wire-format payload is the canonical scope key string the server
    // computes from PolicyScopeKeys.ScopeKey. Sections store the hash of
    // every ancestor; the dispatch hashes the inbound key and matches against
    // each section's ancestor hash list. We do the hashing here so we don't
    // have to ship the ancestor scope keys (which can contain ',' characters
    // in path templates) -- hashes are alphanumeric and parse cleanly.

    function hashString(str) {
        // Cheap djb2-style fallback that's used ONLY when the modern
        // SubtleCrypto SHA-256 path isn't reachable (e.g. file:// pages
        // during tests). The server uses real SHA-256 to compute the hash
        // it emits in data-policy-stack-ancestors, so the fallback gives us
        // a different hash than the server -- production never hits this
        // path because every dashboard page is served over http(s).
        var hash = 5381;
        for (var i = 0; i < str.length; i++) {
            hash = ((hash << 5) + hash) + str.charCodeAt(i);
            hash = hash & hash;
        }
        return ('00000000' + (hash >>> 0).toString(16)).slice(-8) + '00000000';
    }

    function bytesToHex(bytes) {
        var out = '';
        for (var i = 0; i < bytes.length; i++) {
            var b = bytes[i];
            out += (b < 16 ? '0' : '') + b.toString(16);
        }
        return out;
    }

    function scopeKeyToHashAsync(scopeKey) {
        // 16-hex-char SHA-256 prefix to match the server's
        // PolicyScopeKeys.ScopeHash output. SubtleCrypto is async; cache by
        // scope key so a steady-state stream of one repeated change costs
        // exactly one digest.
        if (!window.crypto || !window.crypto.subtle) {
            return Promise.resolve(hashString(scopeKey).slice(0, 16));
        }
        var enc = new TextEncoder().encode(scopeKey);
        return window.crypto.subtle.digest('SHA-256', enc).then(function (buf) {
            return bytesToHex(new Uint8Array(buf)).slice(0, 16);
        });
    }

    var hashCache = Object.create(null);
    function resolveHash(scopeKey) {
        if (hashCache[scopeKey]) return Promise.resolve(hashCache[scopeKey]);
        return scopeKeyToHashAsync(scopeKey).then(function (hash) {
            hashCache[scopeKey] = hash;
            return hash;
        });
    }

    function refreshSection(section) {
        var url = section.getAttribute('data-policy-stack-rows-url');
        if (!url) return;
        // The envelope div is the outerHTML swap target. It carries the same
        // id the server emits in the rows-url response.
        var target = section.querySelector('[id^="sb-policy-stack-rows-"]');
        if (!target) return;
        if (!window.htmx || typeof window.htmx.ajax !== 'function') return;
        try {
            window.htmx.ajax('GET', url, { target: target, swap: 'outerHTML' });
        } catch (e) { /* swallow */ }
    }

    connection.on('PolicyChanged', function (scopeKey) {
        if (!scopeKey) return;
        resolveHash(scopeKey).then(function (changedHash) {
            var sections = document.querySelectorAll('[data-policy-stack-scope]');
            for (var i = 0; i < sections.length; i++) {
                var section = sections[i];
                var ancestors = ancestorsOf(section);
                if (ancestors.indexOf(changedHash) !== -1) refreshSection(section);
            }
        });
    });

    // ---------- Connection start + reconnect rejoin ----------

    function rejoinAll() {
        for (var hash in joinedGroups) {
            if (Object.prototype.hasOwnProperty.call(joinedGroups, hash)) {
                connection.invoke('JoinPolicyGroup', hash).catch(function () { /* swallow */ });
            }
        }
    }

    connection.onreconnected(function () { connectionReady = true; rejoinAll(); });
    connection.onclose(function () { connectionReady = false; });

    function start() {
        connection.start().then(function () {
            connectionReady = true;
            rejoinAll();
        }).catch(function () {
            setTimeout(start, 2000);
        });
    }
    start();

    // ---------- DOM observer ----------

    var observer = new MutationObserver(function (mutations) {
        for (var i = 0; i < mutations.length; i++) {
            var m = mutations[i];
            for (var j = 0; j < m.addedNodes.length; j++)   forEachSection(m.addedNodes[j], joinAll);
            for (var k = 0; k < m.removedNodes.length; k++) forEachSection(m.removedNodes[k], leaveAll);
        }
    });
    observer.observe(document.documentElement, { subtree: true, childList: true });

    // Initial pass once the DOM is ready.
    function joinInitial() {
        var sections = document.querySelectorAll('[data-policy-stack-scope]');
        for (var i = 0; i < sections.length; i++) joinAll(sections[i]);
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', joinInitial, { once: true });
    } else {
        joinInitial();
    }
})();
