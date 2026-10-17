(function () {
    'use strict';
    // Keep the last successful HTML on a failed refresh. HTMX already refuses
    // to swap error responses; expose that failure rather than painting zeros.
    function failed(event) {
        var root = event.detail.elt.closest('[data-sb-api-traffic]');
        if (!root) return;
        root.querySelector('[data-sb-traffic-error]').hidden = false;
        var content = root.querySelector('#sb-api-traffic-content');
        content.dataset.state = content.dataset.hasSnapshot === 'true' ? 'stale' : 'unavailable';
    }
    ['htmx:responseError', 'htmx:sendError', 'htmx:timeout'].forEach(function (name) {
        document.addEventListener(name, failed);
    });
    document.addEventListener('htmx:afterSwap', function (event) {
        if (event.detail.target.id !== 'sb-api-traffic-content') return;
        // outerHTML removes the original target, so find the replacement in the document.
        var content = document.getElementById('sb-api-traffic-content');
        var root = content && content.closest('[data-sb-api-traffic]');
        if (root) root.querySelector('[data-sb-traffic-error]').hidden = true;
    });
}());
