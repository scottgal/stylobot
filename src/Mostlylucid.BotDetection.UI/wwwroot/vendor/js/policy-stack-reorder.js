/*
 * StyloBot Policy Stack -- C7 drag-drop reorder + source-pill navigation.
 *
 * Two behaviours wired here:
 *
 *   1) Within the Effective tab, an operator with dashboard-write can grab a
 *      row by its drag handle (rendered by _RuleCard.cshtml only when
 *      canEdit && !IsInherited) and drop it to a new position in the SAME
 *      scope. On drop we POST { scope, ruleIdsInOrder } to the commercial
 *      mutation API at /api/v1/policies/reorder. The server bumps each
 *      rule's priority to gap-numbered values (0, 10, 20, ...) and the
 *      SignalR beacon from B6 fans the change to every other viewer; the
 *      local DOM is already in the new order from the drag, so no extra
 *      swap is needed here.
 *
 *   2) When a row is inherited from a broader scope, the drag handle is
 *      hidden by the partial. The row's source pill instead becomes a
 *      navigation hint: clicking it routes the operator to the owning
 *      scope's policy stack view, where the rule appears as a non-inherited
 *      row WITH a drag handle.
 *
 * Vanilla JS, no framework deps. Mounts itself on DOMContentLoaded and on
 * every htmx:afterSwap so HTMX OOB row swaps (B6) re-bind cleanly.
 */
(function () {
    'use strict';

    function init(root) {
        if (!root || !root.querySelectorAll) return;
        root.querySelectorAll('[data-policy-stack-reorder-scope]').forEach(setupContainer);
        root.querySelectorAll('[data-source-pill-href]').forEach(setupSourcePill);
    }

    function setupContainer(container) {
        if (container.dataset.reorderInitialised === '1') return;
        container.dataset.reorderInitialised = '1';

        var dragRow = null;

        container.addEventListener('dragstart', function (evt) {
            var handle = evt.target.closest && evt.target.closest('[data-policy-stack-drag-handle]');
            if (!handle) { evt.preventDefault(); return; }
            // The handle is the draggable element; the row we move is the
            // outer <li> so the whole row mutates in the DOM, not just the
            // article inside it. Either ancestor works as the row anchor
            // because the article carries data-rule-id directly.
            var rowArticle = handle.closest('[data-rule-id]');
            dragRow = rowArticle ? (rowArticle.closest('li') || rowArticle) : null;
            if (!dragRow) { evt.preventDefault(); return; }
            evt.dataTransfer.effectAllowed = 'move';
            try { evt.dataTransfer.setData('text/plain', rowArticle.dataset.ruleId || ''); } catch (_) { /* ignore */ }
            dragRow.classList.add('sb-row-dragging');
        });

        container.addEventListener('dragover', function (evt) {
            if (!dragRow) return;
            evt.preventDefault();
            evt.dataTransfer.dropEffect = 'move';
            // The drag target is whichever sibling row's bounding box the
            // pointer is currently over. If the pointer is below the
            // midline, insert AFTER it; otherwise BEFORE. This gives the
            // operator a smooth "swap" feel without a separate placeholder.
            var targetArticle = evt.target.closest && evt.target.closest('[data-rule-id]');
            if (!targetArticle) return;
            var targetRow = targetArticle.closest('li') || targetArticle;
            if (!targetRow || targetRow === dragRow || targetRow.parentElement !== container) return;
            var rect = targetRow.getBoundingClientRect();
            var insertAfter = (evt.clientY - rect.top) > rect.height / 2;
            container.insertBefore(dragRow, insertAfter ? targetRow.nextSibling : targetRow);
        });

        container.addEventListener('dragend', function () {
            if (!dragRow) return;
            dragRow.classList.remove('sb-row-dragging');
            // Collect rule ids in the order they now appear in the list.
            // Only direct-child rows count; nested elements with data-rule-id
            // (none today, but the closest('li') guard above keeps us safe).
            var order = [];
            container.querySelectorAll('[data-rule-id]').forEach(function (el) {
                var li = el.closest('li');
                if (!li || li.parentElement !== container) return;
                order.push(el.dataset.ruleId);
            });
            var scope = container.dataset.policyStackReorderScope;
            dragRow = null;
            if (order.length > 0 && scope) postReorder(scope, order);
        });

        // Pressing Escape mid-drag bails out of the in-progress drag without
        // POSTing. The browser fires dragend after this naturally; clearing
        // dragRow here makes the subsequent dragend a no-op.
        container.addEventListener('keydown', function (evt) {
            if (evt.key !== 'Escape' || !dragRow) return;
            dragRow.classList.remove('sb-row-dragging');
            dragRow = null;
        });
    }

    function setupSourcePill(pill) {
        if (pill.dataset.sourcePillInitialised === '1') return;
        pill.dataset.sourcePillInitialised = '1';
        pill.style.cursor = 'pointer';
        var go = function () {
            var href = pill.dataset.sourcePillHref;
            if (href) window.location.assign(href);
        };
        pill.addEventListener('click', go);
        pill.addEventListener('keydown', function (evt) {
            if (evt.key === 'Enter' || evt.key === ' ') {
                evt.preventDefault();
                go();
            }
        });
    }

    function postReorder(scope, ruleIdsInOrder) {
        try {
            var body = JSON.stringify({
                scope: decodePolicyScope(scope),
                ruleIdsInOrder: ruleIdsInOrder
            });
            fetch('/api/v1/policies/reorder', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: body
            }).then(function (resp) {
                if (resp.status === 401) { window.location.href = '/login'; return; }
                if (resp.status === 403) {
                    // dashboard-write enforcement bounced us. Surface a
                    // console warning; the user will need to refresh to
                    // see the rows snap back to their original order.
                    console.warn('reorder forbidden -- dashboard-write required');
                    return;
                }
                if (!resp.ok) { console.warn('reorder failed', resp.status); return; }
                // SignalR beacon (B6) fans the change to all viewers; the
                // local DOM is already in the right order from the drag.
            }).catch(function (e) { console.warn('reorder error', e); });
        } catch (e) { console.warn('reorder error', e); }
    }

    function decodePolicyScope(encoded) {
        // Mirrors PolicyScopeUrl.Decode -- "kind|domain|subdomain|template".
        // Each URL-component is decoded so the commercial API receives the
        // raw values it stored.
        if (!encoded) return { kind: 'wildcard' };
        var parts = encoded.split('|');
        var kind = parts[0];
        var safeDecode = function (s) {
            try { return decodeURIComponent(s); } catch (_) { return s; }
        };
        return {
            kind: kind,
            domain: parts[1] ? safeDecode(parts[1]) : null,
            subdomain: parts[2] ? safeDecode(parts[2]) : null,
            pathTemplate: parts.length > 3 ? safeDecode(parts.slice(3).join('|')) : null
        };
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { init(document); });
    } else {
        init(document);
    }
    if (document.body) {
        document.body.addEventListener('htmx:afterSwap', function (evt) {
            init(evt.target || document);
        });
    }
})();
