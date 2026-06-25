/*
 * StyloBot Policy Stack -- A8 scope-group accordion + Owned/Effective tab JS,
 * extended in C11 with a section-level Effective/Stack/Templates toggle.
 *
 * Three behaviours wired here, all via document-level event delegation so
 * SignalR-driven OOB swaps that replace a scope group's content don't have
 * to re-bind handlers -- the listeners live on document and key off the
 * event target's data attributes (per specs
 * 2026-06-24-policy-stack-3band-card-design.md and
 * 2026-06-25-policy-templates-and-shapes.md).
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
 *   3) Section-level Effective/Stack/Templates toggle (C11). Click a
 *      .sb-policy-stack-tab inside .sb-policy-stack-tabs: flip
 *      aria-selected/is-active on every sibling tab, then toggle [hidden]
 *      on every [data-tab-panel] within the same .sb-policy-stack section.
 *      A clicked tab's data-tab maps to a target panel name:
 *        - "templates" -> data-tab-panel="templates"
 *        - "effective" / "stack" -> data-tab-panel="rules"
 *      When a panel becomes visible we dispatch a 'reveal' CustomEvent so
 *      htmx's hx-trigger="reveal once" lazy-loads the templates gallery on
 *      first reveal (htmx 2.0.4 listens for the native reveal trigger; the
 *      explicit dispatch is belt-and-braces for cases where the panel is
 *      already above the fold and the IntersectionObserver wouldn't fire).
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

    // C11 -- Section-level Effective/Stack/Templates tab toggle. Click a
    // .sb-policy-stack-tab inside .sb-policy-stack-tabs and we flip
    // aria-selected/is-active on every sibling tab, then toggle [hidden]
    // on every [data-tab-panel] within the parent .sb-policy-stack
    // section. "effective" and "stack" both map to the "rules" panel
    // (the server-rendered body shows whichever one was active when the
    // page was requested); "templates" maps to the lazy-loaded panel.
    document.addEventListener('click', function (ev) {
        var tab = ev.target.closest('.sb-policy-stack-tabs > .sb-policy-stack-tab');
        if (!tab) return;

        var section = tab.closest('.sb-policy-stack');
        if (!section) return;

        var targetTabName = tab.getAttribute('data-tab');
        if (!targetTabName) return;

        // tab -> panel name. Effective/Stack share the "rules" panel.
        var targetPanelName = targetTabName === 'templates' ? 'templates' : 'rules';

        // Flip aria-selected + is-active on every sibling tab in the same strip.
        var strip = tab.parentNode;
        var siblings = strip.querySelectorAll('.sb-policy-stack-tab');
        for (var i = 0; i < siblings.length; i++) {
            var isMe = siblings[i] === tab;
            siblings[i].setAttribute('aria-selected', isMe ? 'true' : 'false');
            if (isMe) {
                siblings[i].classList.add('is-active');
            } else {
                siblings[i].classList.remove('is-active');
            }
        }

        // Toggle [hidden] on every [data-tab-panel] inside this section.
        var panels = section.querySelectorAll('[data-tab-panel]');
        for (var j = 0; j < panels.length; j++) {
            var isTarget = panels[j].getAttribute('data-tab-panel') === targetPanelName;
            if (isTarget) {
                panels[j].removeAttribute('hidden');
                // Dispatch 'reveal' so hx-trigger="reveal once" lazy-loads
                // the templates gallery on first reveal. htmx swallows
                // repeat reveals because of the "once" modifier.
                panels[j].dispatchEvent(new CustomEvent('reveal', { bubbles: true }));
            } else {
                panels[j].setAttribute('hidden', '');
            }
        }
    });
})();