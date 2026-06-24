/*
 * StyloBot Policy Stack -- A8 scope-group accordion + Owned/Effective tab JS.
 *
 * Two behaviours wired here, both via document-level event delegation so
 * SignalR-driven OOB swaps that replace a scope group's content don't have
 * to re-bind handlers -- the listeners live on document and key off the
 * event target's data attributes (per spec
 * 2026-06-24-policy-stack-3band-card-design.md).
 *
 *   1) Disclosure caret. Click the [.sb-policy-scope-group-disclosure]
 *      button: flip aria-expanded on the button, toggle [hidden] on the
 *      body region the button controls via aria-controls.
 *
 *   2) Owned / Effective tab toggle. Click a [role="tab"] inside a
 *      [data-scope-group]: flip aria-selected on every tab in the same
 *      group, toggle [hidden] on every panel so the matching
 *      [data-panel] becomes visible.
 *
 * Vanilla DOM, no framework deps. ES5-compatible to match the sibling
 * policy-stack-reorder.js / policy-stack-edit.js style (no =>, no const,
 * no template literals). Native <button> elements own the keyboard
 * semantics (Tab/Enter/Space) -- arrow-key tab navigation is a Phase B
 * a11y enhancement.
 */
(function () {
    'use strict';

    // Owned / Effective tab toggle. Pattern: click a [role="tab"] inside a
    // [data-scope-group], flip aria-selected on every sibling tab, hide
    // every panel and reveal the one whose data-panel matches the
    // clicked tab's data-tab.
    document.addEventListener('click', function (ev) {
        var tab = ev.target.closest('[data-scope-group] [role="tab"]');
        if (!tab) return;

        var group = tab.closest('[data-scope-group]');
        if (!group) return;

        var targetTabName = tab.getAttribute('data-tab');
        if (!targetTabName) return;

        // Flip aria-selected on every tab inside this group.
        var siblings = group.querySelectorAll('[role="tab"]');
        for (var i = 0; i < siblings.length; i++) {
            siblings[i].setAttribute(
                'aria-selected',
                siblings[i] === tab ? 'true' : 'false');
        }

        // Toggle [hidden] on every panel inside this group; the matching
        // panel (data-panel === targetTabName) becomes visible.
        var panels = group.querySelectorAll('[role="tabpanel"]');
        for (var j = 0; j < panels.length; j++) {
            var isTarget = panels[j].getAttribute('data-panel') === targetTabName;
            if (isTarget) {
                panels[j].removeAttribute('hidden');
            } else {
                panels[j].setAttribute('hidden', '');
            }
        }
    });

    // Disclosure caret toggle. Pattern: click the disclosure button,
    // flip aria-expanded, toggle [hidden] on the body region the button
    // controls via aria-controls.
    document.addEventListener('click', function (ev) {
        var disclosure = ev.target.closest('.sb-policy-scope-group-disclosure');
        if (!disclosure) return;

        var expanded = disclosure.getAttribute('aria-expanded') === 'true';
        var newExpanded = !expanded;
        disclosure.setAttribute('aria-expanded', newExpanded ? 'true' : 'false');

        var controlsId = disclosure.getAttribute('aria-controls');
        if (!controlsId) return;
        var body = document.getElementById(controlsId);
        if (!body) return;

        if (newExpanded) {
            body.removeAttribute('hidden');
        } else {
            body.setAttribute('hidden', '');
        }
    });
})();