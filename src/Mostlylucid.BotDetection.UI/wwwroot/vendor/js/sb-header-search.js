// Se3 -- dashboard header type-ahead.
// Reads basePath off its own <script data-base-path="..."> tag (mirrors
// sb-live-updates.js) and POSTs to <basePath>/search/visitors. The middleware
// returns a JSON array of { fingerprintId, primarySignature, resolvedName,
// lastSeen } records, capped at DashboardLayoutOptions.SearchMaxResults.
(function () {
    'use strict';

    var input = document.getElementById('sb-header-search');
    var results = document.getElementById('sb-header-search-results');
    if (!input || !results) return;

    // basePath resolution: prefer the input's data attribute (server-rendered
    // with the actual dashboard base) over the script tag's, so embedding hosts
    // (marketing site at /dashboard, FOSS standalone at /_stylobot) get the
    // right route without a build-time switch.
    var basePath = (input.dataset.basePath || '/_stylobot').replace(/\/+$/, '');

    // cmd-K / ctrl-K to focus, Escape to dismiss + blur.
    document.addEventListener('keydown', function (e) {
        if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
            e.preventDefault();
            input.focus();
            input.select();
        }
        if (e.key === 'Escape' && document.activeElement === input) {
            input.blur();
            results.classList.add('hidden');
        }
    });

    // Debounced type-ahead. 150ms is fast enough to feel live but coarse enough
    // that a normal typist (~5 chars/sec) only fires once per word.
    var timer = null;
    var lastQuery = '';
    input.addEventListener('input', function () {
        clearTimeout(timer);
        var q = input.value.trim();
        if (q.length < 2) {
            results.classList.add('hidden');
            return;
        }
        if (q === lastQuery) return;
        timer = setTimeout(function () {
            lastQuery = q;
            fetch(basePath + '/search/visitors?q=' + encodeURIComponent(q), {
                headers: { 'Accept': 'application/json' }
            })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (hits) {
                    if (hits === null) return;
                    // Always rebuild via DOM APIs (textContent / setAttribute) so
                    // no field from the API is ever interpolated into innerHTML.
                    // resolvedName is operator-editable on the signature detail
                    // page; treating it as untrusted is the right default.
                    while (results.firstChild) results.removeChild(results.firstChild);
                    if (!Array.isArray(hits) || hits.length === 0) {
                        var empty = document.createElement('div');
                        empty.className = 'px-3 py-2 text-xs text-base-content/50';
                        empty.textContent = 'no matches';
                        results.appendChild(empty);
                    } else {
                        hits.forEach(function (h) {
                            var a = document.createElement('a');
                            a.className = 'block px-3 py-2 hover:bg-base-200';
                            a.setAttribute('href',
                                basePath + '/visitors/' + encodeURIComponent(h.primarySignature || ''));

                            var name = document.createElement('div');
                            name.className = 'text-sm';
                            name.textContent = h.resolvedName || '';
                            a.appendChild(name);

                            var fp = document.createElement('div');
                            fp.className = 'text-xs text-base-content/40';
                            fp.textContent = (h.fingerprintId || '').slice(0, 12);
                            a.appendChild(fp);

                            results.appendChild(a);
                        });
                    }
                    results.classList.remove('hidden');
                })
                .catch(function () { /* silent: type-ahead noise is worse than no answer */ });
        }, 150);
    });

    // Dismiss on outside click.
    document.addEventListener('click', function (e) {
        if (!input.contains(e.target) && !results.contains(e.target)) {
            results.classList.add('hidden');
        }
    });
})();