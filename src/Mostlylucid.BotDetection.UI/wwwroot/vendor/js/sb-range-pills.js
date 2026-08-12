// Period-selector pills (operator directive 2026-08-12): the Custom pill toggles
// the inline from/to inputs. External file because the dashboard CSP's
// script-src 'self' 'nonce-…' 'unsafe-eval' rejects nonced-less inline scripts.
// DELEGATED listener (document-level) so it survives the pill row's hx-swap
// (the controller re-renders the form on every selection — a direct listener
// on the initial form would die with it).
(function () {
    'use strict';

    function syncPill(toggle) {
        var inputs = document.getElementById('sb-custom-range-inputs');
        if (!toggle || !inputs) return;
        var active = toggle.getAttribute('aria-expanded') === 'true';
        inputs.classList.toggle('hidden', !active);
    }

    document.addEventListener('click', function (e) {
        var toggle = e.target.closest('[data-custom-range-toggle]');
        if (!toggle) return;
        var inputs = document.getElementById('sb-custom-range-inputs');
        if (!inputs) return;
        var nowOpen = inputs.classList.contains('hidden');
        inputs.classList.toggle('hidden', !nowOpen);
        toggle.classList.toggle('btn-primary', nowOpen);
        toggle.setAttribute('aria-expanded', nowOpen ? 'true' : 'false');
    });

    // Initial state (the SSR renders the custom inputs when the cookie says custom).
    syncPill(document.querySelector('[data-custom-range-toggle]'));
})();
