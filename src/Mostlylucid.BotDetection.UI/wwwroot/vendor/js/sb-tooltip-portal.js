/*
 * StyloBot tooltip portal.
 *
 * daisyUI tooltips use absolute-positioned ::before pseudo-elements that get
 * clipped by ANY ancestor with `overflow: hidden | auto | clip | scroll`. The
 * dashboard's table chrome (`overflow-x-auto`, `overflow-auto`, card wrappers
 * with `overflow: hidden`) chops cell tooltips at the edges.
 *
 * This shim mirrors `.tooltip[data-tip]` elements into a body-level layer on
 * hover / focus. The cloned tooltip uses `position: fixed` and the trigger's
 * `getBoundingClientRect()` so it escapes every clip ancestor.
 *
 * The original .tooltip element keeps its data-tip attribute but its own
 * ::before/::after pseudos are suppressed for as long as the portal is active.
 * No daisyUI markup changes, no per-call-site rewrite.
 *
 * Self-hosted under /_content/Mostlylucid.BotDetection.UI/vendor/js/. Loaded
 * by SbLiveUpdatesTagHelper alongside the live-updates coordinator.
 */
(function () {
    'use strict';

    var PORTAL_ID = 'sb-tooltip-portal';
    var SUPPRESS_CLASS = 'sb-tooltip-portal-active';
    var ATTR_BOUND = 'data-sb-tooltip-bound';
    var current = null;
    var portal = null;

    function ensurePortal() {
        if (portal && portal.isConnected) return portal;
        portal = document.getElementById(PORTAL_ID);
        if (!portal) {
            portal = document.createElement('div');
            portal.id = PORTAL_ID;
            portal.setAttribute('role', 'tooltip');
            portal.setAttribute('aria-hidden', 'true');
            portal.style.position = 'fixed';
            portal.style.zIndex = '9999';
            portal.style.pointerEvents = 'none';
            portal.style.display = 'none';
            document.body.appendChild(portal);
        }
        return portal;
    }

    function placement(el) {
        if (el.classList.contains('tooltip-bottom')) return 'bottom';
        if (el.classList.contains('tooltip-left')) return 'left';
        if (el.classList.contains('tooltip-right')) return 'right';
        return 'top';
    }

    function position(el, p) {
        var rect = el.getBoundingClientRect();
        var pr = p.getBoundingClientRect();
        var gap = 6;
        var top, left;
        switch (placement(el)) {
            case 'bottom':
                top = rect.bottom + gap;
                left = rect.left + rect.width / 2 - pr.width / 2;
                break;
            case 'left':
                top = rect.top + rect.height / 2 - pr.height / 2;
                left = rect.left - pr.width - gap;
                break;
            case 'right':
                top = rect.top + rect.height / 2 - pr.height / 2;
                left = rect.right + gap;
                break;
            default: // top
                top = rect.top - pr.height - gap;
                left = rect.left + rect.width / 2 - pr.width / 2;
        }

        // Clamp into the viewport so tooltips near the screen edge stay visible.
        var maxLeft = window.innerWidth - pr.width - 4;
        var maxTop = window.innerHeight - pr.height - 4;
        if (left < 4) left = 4;
        if (left > maxLeft) left = maxLeft;
        if (top < 4) top = 4;
        if (top > maxTop) top = maxTop;

        p.style.top = top + 'px';
        p.style.left = left + 'px';
    }

    function show(el) {
        var tip = el.getAttribute('data-tip');
        if (!tip) return;
        var p = ensurePortal();
        p.textContent = tip;
        p.className = 'sb-tooltip-portal-tip';
        p.style.display = 'block';
        // Position after the browser has laid out the tooltip with its real width.
        // RAF here so font metrics and width are settled when we measure.
        requestAnimationFrame(function () { position(el, p); });
        el.classList.add(SUPPRESS_CLASS);
        current = el;
    }

    function hide() {
        if (portal) {
            portal.style.display = 'none';
            portal.textContent = '';
        }
        if (current) {
            current.classList.remove(SUPPRESS_CLASS);
            current = null;
        }
    }

    function bind(el) {
        if (el.getAttribute(ATTR_BOUND) === '1') return;
        el.setAttribute(ATTR_BOUND, '1');
        el.addEventListener('mouseenter', function () { show(el); });
        el.addEventListener('mouseleave', hide);
        el.addEventListener('focusin', function () { show(el); });
        el.addEventListener('focusout', hide);
    }

    function scan(root) {
        var nodes = (root || document).querySelectorAll('.tooltip[data-tip]');
        for (var i = 0; i < nodes.length; i++) bind(nodes[i]);
    }

    // Initial scan + MutationObserver so HTMX OOB swaps and SignalR-driven
    // partial replacements pick up new tooltip triggers without manual rebind.
    function start() {
        scan(document);
        var mo = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var m = mutations[i];
                for (var j = 0; j < m.addedNodes.length; j++) {
                    var n = m.addedNodes[j];
                    if (n.nodeType !== 1) continue;
                    if (n.matches && n.matches('.tooltip[data-tip]')) bind(n);
                    if (n.querySelectorAll) {
                        var inner = n.querySelectorAll('.tooltip[data-tip]');
                        for (var k = 0; k < inner.length; k++) bind(inner[k]);
                    }
                }
            }
        });
        mo.observe(document.body, { childList: true, subtree: true });

        // Hide the portal on scroll / resize so it doesn't sit stale at the
        // old anchor coordinates after the layout shifts.
        window.addEventListener('scroll', hide, true);
        window.addEventListener('resize', hide);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
