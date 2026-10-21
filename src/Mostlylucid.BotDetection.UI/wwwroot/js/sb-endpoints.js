// Lean endpoint table: in-row detail disclosure.
//
// The table is fully server-rendered; this file only adds the open/close
// behaviour so the disclosure can announce its state (aria-expanded) and so
// only one row is open at a time. Without JS the rows still link to the full
// endpoint detail, so nothing is lost.
//
// Bound once per document: the table is swapped by HTMX, so a re-executed
// script (after a swap) must not add a second delegated listener.
(function () {
    'use strict';
    if (window.__sbEndpointsBound) { return; }
    window.__sbEndpointsBound = true;

    function detailFor(btn) {
        var id = btn.getAttribute('data-sb-ep-toggle');
        return id ? document.getElementById(id) : null;
    }

    function setOpen(btn, open) {
        var row = detailFor(btn);
        btn.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (row) { row.hidden = !open; }
    }

    document.addEventListener('click', function (event) {
        var btn = event.target.closest ? event.target.closest('[data-sb-ep-toggle]') : null;
        if (!btn) { return; }

        var open = btn.getAttribute('aria-expanded') === 'true';
        var table = btn.closest('table');
        if (!open && table) {
            // One open row at a time keeps the table scannable.
            var others = table.querySelectorAll('[data-sb-ep-toggle][aria-expanded="true"]');
            for (var i = 0; i < others.length; i++) { setOpen(others[i], false); }
        }
        setOpen(btn, !open);
    });

    // A swap replaces the markup, so any open state is gone by design; nothing
    // to restore. Keep Escape as a way out of the open row.
    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') { return; }
        var open = document.querySelector('[data-sb-ep-toggle][aria-expanded="true"]');
        if (!open) { return; }
        setOpen(open, false);
        open.focus();
    });
})();
