// Period-selector pills (operator directive 2026-08-12): the Custom pill toggles
// the inline from/to inputs. External file because the dashboard CSP's
// script-src 'self' 'nonce-…' 'unsafe-eval' rejects nonced-less inline scripts.
(function () {
    'use strict';
    var toggle = document.querySelector('[data-custom-range-toggle]');
    var inputs = document.getElementById('sb-custom-range-inputs');
    if (!toggle || !inputs) return;

    function sync() {
        var active = toggle.getAttribute('aria-expanded') === 'true';
        inputs.classList.toggle('hidden', !active);
    }

    toggle.addEventListener('click', function () {
        var nowOpen = inputs.classList.contains('hidden');
        inputs.classList.toggle('hidden', !nowOpen);
        toggle.classList.toggle('btn-primary', nowOpen);
        toggle.setAttribute('aria-expanded', nowOpen ? 'true' : 'false');
    });

    sync();
})();
