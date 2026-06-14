/*
 * StyloBot Policy Stack -- C6 expression editor.
 *
 * Wires the bidirectional chip <-> raw-text editor. Both panes are visible
 * and editable at the same time. Typing in the textarea triggers a server
 * parse (POST /dashboard/policystack/parse) and re-renders the chips with
 * the lowercase-discriminator AST the parse route emits. Manipulating a
 * chip rebuilds the text from the chip DOM and writes it back to the
 * textarea. While the textarea is mid-edit and unparseable, the chip pane
 * greys out the last-good state and the Save button stays disabled.
 *
 * Vanilla JS, no framework deps. Mounts itself on DOMContentLoaded and on
 * every htmx:afterSwap event so new .sb-policy-stack-edit-row elements
 * dropped in by HTMX (the pencil button's outerHTML swap) self-initialise.
 */
(function () {
    'use strict';

    function init(root) {
        if (!root || !root.querySelectorAll) return;
        root.querySelectorAll('.sb-policy-stack-edit-row').forEach(setUpEditRow);
    }

    function setUpEditRow(article) {
        if (article.dataset.editInitialised === '1') return;
        article.dataset.editInitialised = '1';

        var expr = article.querySelector('[data-edit-expression]');
        var chipPane = article.querySelector('[data-edit-chip-pane]');
        var validation = article.querySelector('[data-edit-validation]');
        var saveBtn = article.querySelector('[data-edit-save]');
        var acList = article.querySelector('[data-edit-autocomplete]');
        // Task 8 -- the kind selector is the <select name="action.kind"> at the
        // top of _EditAction.cshtml, marked with [data-action-kind-select]. The
        // per-kind partial fieldsets carry [data-edit-action-kind="<kind>"] as
        // a STRING (not a select value); they're the swap target, not the
        // selector. Reading kind from the SELECT is what flows into
        // collectActionFromForm + scheduleBacktest.
        var actionKindSelect = article.querySelector('[data-action-kind-select]');
        var form = article.querySelector('form');

        if (!expr || !chipPane || !validation || !saveBtn || !acList || !form) return;

        // Task 21 -- debounce timings ride on data-* attributes so commercial
        // can override via PolicyStackEditorOptions without touching the JS.
        // parseInt('', 10) => NaN; NaN || <fallback> => fallback, so a FOSS
        // standalone render (where the attribute is empty because no
        // PolicyEditPresenter populated it) keeps the legacy literals.
        var parseDebounceMs = parseInt(article.dataset.parseDebounceMs, 10) || 80;
        var backtestDebounceMs = parseInt(article.dataset.backtestDebounceMs, 10) || 500;

        var ast = null;
        var lastGoodAst = null;

        // Task 8 swap pattern -- when the operator changes the action kind,
        // the <select>'s hx-get fetches /dashboard/policystack/action-editor
        // and replaces [data-action-editor-slot]'s contents. HTMX's own
        // change handler issues the swap; we listen too so we can re-fire
        // the C8 backtest (the projection depends on the selected kind).
        // No DOM toggling here -- the slot-swap replaces the per-kind
        // partial in full, so the old [data-edit-action-meta] hide/show
        // pattern is gone (Task 8 asserts non-active kinds aren't in the
        // DOM at all). The htmx:afterSwap handler at the bottom catches
        // the actual slot replacement and re-triggers backtest from the
        // current expr value.
        if (actionKindSelect) {
            actionKindSelect.addEventListener('change', function () {
                // The HTMX swap is in flight; the afterSwap handler will
                // re-fire the backtest once the new partial is in place.
                // We don't queue anything here because the slot's stale
                // contents would re-render with stale kind data otherwise.
            });
        }

        // Chip-control buttons (+ AND / + OR).
        article.querySelectorAll('[data-edit-chip-add]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                addChip(btn.dataset.editChipAdd);
            });
        });

        // Initial parse from the textarea seed.
        runParse(expr.value);

        // Live re-parse on every keystroke (debounce from server-rendered
        // data-parse-debounce-ms; FOSS-fallback 80ms).
        expr.addEventListener('input', debounce(function () { runParse(expr.value); }, parseDebounceMs));
        expr.addEventListener('keydown', onExpressionKey);
        expr.addEventListener('keyup', maybeShowAutocomplete);
        expr.addEventListener('click', maybeShowAutocomplete);
        expr.addEventListener('blur', function () { hideAutocomplete(150); });

        // Submit goes through the commercial mutation API. The article's
        // data-* attributes carry the URL + method (POST for new, PUT for
        // existing) so this single handler covers both cases.
        form.addEventListener('submit', onSubmit);

        function runParse(text) {
            var trimmed = (text || '').trim();
            if (!trimmed) {
                ast = { kind: 'empty' };
                lastGoodAst = null;
                renderChips(null, /*greyedOut=*/false);
                showValidation('add at least one term', false);
                return;
            }
            fetch('/dashboard/policystack/parse', {
                method: 'POST',
                headers: { 'Content-Type': 'text/plain' },
                body: trimmed,
                credentials: 'same-origin'
            }).then(function (resp) {
                return resp.json().then(function (body) {
                    if (resp.status === 200) {
                        ast = body.ast;
                        lastGoodAst = ast;
                        renderChips(ast, /*greyedOut=*/false);
                        var n = countTerms(ast);
                        showValidation('valid -- ' + n + ' term' + (n === 1 ? '' : 's'), true);
                        // C8: kick a debounced backtest so the panel
                        // updates as the operator types. Only fire when
                        // the parse succeeds -- there's no point asking
                        // the server to project an unparseable predicate.
                        scheduleBacktest(trimmed);
                    } else {
                        ast = null;
                        renderChips(lastGoodAst, /*greyedOut=*/true);
                        showValidation(
                            'parse error at char ' + (body && body.position != null ? body.position : '?') +
                            ': ' + ((body && body.message) || 'invalid expression'),
                            false);
                    }
                });
            }).catch(function () {
                showValidation('parser unreachable', false);
            });
        }

        // C8 backtest -- debounced POST to /dashboard/policystack/backtest.
        // Debounce comes from server-rendered data-backtest-debounce-ms
        // (commercial PolicyStackEditorOptions.BacktestDebounceMs);
        // FOSS-fallback 500ms when the attribute is unset.
        // The scheduler is held on closure-local state so a fast typist
        // collapses the burst into a single request. We don't queue an
        // initial request before the first successful parse -- the slot
        // renders the placeholder until the operator types something
        // valid.
        var backtestTimer = null;
        function scheduleBacktest(text) {
            if (backtestTimer) clearTimeout(backtestTimer);
            backtestTimer = setTimeout(function () { runBacktest(text); }, backtestDebounceMs);
        }

        function runBacktest(text) {
            var slot = article.querySelector('[data-edit-backtest-slot]');
            if (!slot) return;
            var currentWindow = readCurrentWindow();
            var currentActionKind = actionKindSelect ? actionKindSelect.value : 'observe';
            fetch('/dashboard/policystack/backtest', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    predicate: text,
                    actionKind: currentActionKind,
                    window: currentWindow
                }),
                credentials: 'same-origin'
            }).then(function (resp) {
                if (!resp.ok) return null;
                return resp.text();
            }).then(function (html) {
                if (html == null) return;
                slot.innerHTML = html;
                wireBacktestPanel(slot, text);
            }).catch(function () {
                // Quiet fail -- backtest is best-effort UI signal, not a
                // correctness boundary.
            });
        }

        function readCurrentWindow() {
            var existing = article.querySelector('[data-edit-backtest][data-window]');
            return existing ? (existing.getAttribute('data-window') || '24h') : '24h';
        }

        function wireBacktestPanel(slot, lastText) {
            // Window picker -- click a button, re-run with the new window.
            slot.querySelectorAll('[data-backtest-window]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var fieldset = slot.querySelector('[data-edit-backtest]');
                    if (fieldset) fieldset.setAttribute('data-window', btn.getAttribute('data-backtest-window'));
                    runBacktest(lastText);
                });
            });

            // "Show matching requests" -- hand the comma-separated sample
            // off to the explainer panel by appending a hash fragment the
            // explainer already listens for. The actual hand-off is the
            // explainer's responsibility; here we just dispatch a custom
            // event so other listeners can react too.
            var showMatchingBtn = slot.querySelector('[data-backtest-show-matching]');
            if (showMatchingBtn) {
                showMatchingBtn.addEventListener('click', function () {
                    var sample = showMatchingBtn.getAttribute('data-fingerprint-sample') || '';
                    var fingerprints = sample.split(',').filter(function (s) { return s.length > 0; });
                    article.dispatchEvent(new CustomEvent('sb-policy-backtest-show-matching', {
                        bubbles: true,
                        detail: { fingerprints: fingerprints }
                    }));
                });
            }

            // "Promote to OBSERVE for 24h" -- override the form's mode +
            // attach auto_promote_at so the submit body carries the C9
            // marker. We update the in-form mode select so the existing
            // onSubmit handler picks it up uniformly.
            var promoteBtn = slot.querySelector('[data-backtest-promote-observe]');
            if (promoteBtn) {
                promoteBtn.addEventListener('click', function () {
                    var modeSel = article.querySelector('[data-edit-mode]');
                    if (modeSel) modeSel.value = 'observe';
                    // 24h from now, ISO 8601 UTC. Stamped onto a hidden
                    // input so the existing submit handler can pick it up
                    // without bespoke handling here.
                    var when = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
                    var hidden = article.querySelector('input[name="auto_promote_at"]');
                    if (!hidden) {
                        hidden = document.createElement('input');
                        hidden.type = 'hidden';
                        hidden.name = 'auto_promote_at';
                        form.appendChild(hidden);
                    }
                    hidden.value = when;
                    // Trigger the form submit so the operator doesn't
                    // need to also hit Save -- the promote button IS the
                    // save in this affordance.
                    if (typeof form.requestSubmit === 'function') form.requestSubmit();
                    else form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
                });
            }
        }

        function showValidation(msg, ok) {
            validation.textContent = (ok ? '✓ ' : '✗ ') + msg;
            validation.dataset.editValidationOk = ok ? '1' : '0';
            saveBtn.disabled = !ok || !ast;
        }

        function renderChips(node, greyedOut) {
            chipPane.dataset.greyedOut = greyedOut ? '1' : '0';
            // Clear via DOM, not innerHTML, so untrusted facet/value strings
            // that arrive in the AST from the server-side parser can never
            // round-trip into an HTML-injection sink. renderTerm only ever
            // sets values via .value / textContent, never innerHTML.
            while (chipPane.firstChild) chipPane.removeChild(chipPane.firstChild);
            if (!node || node.kind === 'empty') {
                var placeholder = document.createElement('em');
                placeholder.className = 'sb-edit-chip-placeholder';
                placeholder.textContent = 'Add a term to begin';
                chipPane.appendChild(placeholder);
                return;
            }
            chipPane.appendChild(renderNode(node));
        }

        function renderNode(node) {
            if (node.kind === 'and' || node.kind === 'or') {
                var wrap = document.createElement('div');
                wrap.className = 'sb-edit-combinator sb-edit-' + node.kind;
                (node.children || []).forEach(function (child, i) {
                    if (i > 0) {
                        var kw = document.createElement('span');
                        kw.className = 'sb-edit-kw';
                        kw.textContent = node.kind.toUpperCase();
                        wrap.appendChild(kw);
                    }
                    wrap.appendChild(renderNode(child));
                });
                return wrap;
            }
            return renderTerm(node);
        }

        function renderTerm(term) {
            var chip = document.createElement('span');
            chip.className = 'sb-edit-chip';
            chip.dataset.facet = term.facet || '';
            chip.dataset.op = term.op || 'eq';

            var facetEl = document.createElement('input');
            facetEl.type = 'text';
            facetEl.value = term.facet || '';
            facetEl.className = 'sb-edit-chip-facet';

            var opEl = document.createElement('select');
            opEl.className = 'sb-edit-chip-op';
            opEl.appendChild(new Option(term.op || 'eq', term.op || 'eq'));

            var valEl = document.createElement('input');
            valEl.type = 'text';
            valEl.className = 'sb-edit-chip-value';
            valEl.value = formatValueForChipInput(term.value);

            [facetEl, valEl].forEach(function (el) {
                // Same parse-debounce as the textarea -- chip rebuild flows
                // through runParse() so the operator gets one POST per burst
                // regardless of which pane they're editing.
                el.addEventListener('input', debounce(rebuildExpressionFromChips, parseDebounceMs));
            });
            opEl.addEventListener('change', rebuildExpressionFromChips);

            chip.appendChild(facetEl);
            chip.appendChild(opEl);
            chip.appendChild(valEl);
            return chip;
        }

        function formatValueForChipInput(v) {
            if (v == null) return '';
            if (Array.isArray(v)) return '(' + v.join(', ') + ')';
            if (typeof v === 'boolean') return v ? 'true' : 'false';
            return String(v);
        }

        function rebuildExpressionFromChips() {
            var text = buildExpressionTextFromChipPane(chipPane);
            expr.value = text;
            runParse(text);
        }

        function buildExpressionTextFromChipPane(pane) {
            var root = pane.querySelector('.sb-edit-combinator, .sb-edit-chip');
            return root ? nodeToText(root) : '';
        }

        function nodeToText(el) {
            if (el.classList.contains('sb-edit-chip')) {
                var f = (el.querySelector('.sb-edit-chip-facet').value || '').trim();
                var o = el.querySelector('.sb-edit-chip-op').value;
                var v = (el.querySelector('.sb-edit-chip-value').value || '').trim();
                return f + ' ' + o + ' ' + v;
            }
            var kind = el.classList.contains('sb-edit-or') ? 'or' : 'and';
            var children = Array.prototype.filter.call(el.children, function (c) {
                return c.classList.contains('sb-edit-chip') || c.classList.contains('sb-edit-combinator');
            });
            return children.map(nodeToText).join(' ' + kind + ' ');
        }

        function addChip(kind) {
            var current = (expr.value || '').trim();
            var sep = kind === 'or' ? ' or ' : ' and ';
            var seed = 'signal.name = value';
            expr.value = current ? (current + sep + seed) : seed;
            runParse(expr.value);
        }

        // AUTOCOMPLETE
        function maybeShowAutocomplete() {
            var token = getTokenAroundCursor(expr);
            if (!token || token.length < 2) {
                hideAutocomplete(0);
                return;
            }
            fetch('/dashboard/signals/search?q=' + encodeURIComponent(token) + '&n=20', {
                credentials: 'same-origin'
            }).then(function (resp) {
                if (!resp.ok) {
                    hideAutocomplete(0);
                    return null;
                }
                return resp.json();
            }).then(function (body) {
                if (!body) return;
                showAutocomplete(body.items || [], token);
            }).catch(function () { hideAutocomplete(0); });
        }

        function showAutocomplete(items, q) {
            while (acList.firstChild) acList.removeChild(acList.firstChild);
            items.forEach(function (item) {
                var li = document.createElement('li');
                li.setAttribute('role', 'option');
                li.dataset.facet = item.key;
                var strong = document.createElement('strong');
                strong.textContent = item.key;
                var small = document.createElement('small');
                small.textContent = ' ' + (item.kind || '');
                var desc = document.createElement('div');
                desc.textContent = item.short || '';
                li.appendChild(strong);
                li.appendChild(small);
                li.appendChild(desc);
                li.addEventListener('mousedown', function (ev) {
                    ev.preventDefault();
                    acceptAutocomplete(item.key);
                });
                acList.appendChild(li);
            });
            acList.hidden = items.length === 0;
        }

        function hideAutocomplete(delay) {
            setTimeout(function () { acList.hidden = true; }, delay);
        }

        function onExpressionKey(evt) {
            if (acList.hidden) return;
            if (evt.key === 'Tab' || evt.key === 'Enter') {
                var first = acList.querySelector('li');
                if (first) {
                    evt.preventDefault();
                    acceptAutocomplete(first.dataset.facet);
                }
            } else if (evt.key === 'Escape') {
                hideAutocomplete(0);
            }
        }

        function acceptAutocomplete(key) {
            var bounds = tokenBoundsAroundCursor(expr);
            var before = expr.value.slice(0, bounds.start);
            var after = expr.value.slice(bounds.end);
            expr.value = before + key + after;
            expr.selectionStart = expr.selectionEnd = (before + key).length;
            hideAutocomplete(0);
            runParse(expr.value);
        }

        function getTokenAroundCursor(ta) {
            var b = tokenBoundsAroundCursor(ta);
            return ta.value.slice(b.start, b.end);
        }

        function tokenBoundsAroundCursor(ta) {
            var cursor = ta.selectionStart || 0;
            var text = ta.value;
            var isIdentChar = function (c) { return /[A-Za-z0-9_.]/.test(c); };
            var start = cursor;
            while (start > 0 && isIdentChar(text[start - 1])) start--;
            var end = cursor;
            while (end < text.length && isIdentChar(text[end])) end++;
            return { start: start, end: end };
        }

        function onSubmit(evt) {
            evt.preventDefault();
            if (!ast) return;
            var body = {
                id: article.dataset.ruleId === 'new' ? null : article.dataset.ruleId,
                scope: decodePolicyScopeFromUrl(article.dataset.editScope),
                priority: 0,
                predicate: expr.value,
                action: collectActionFromForm(article),
                mode: (article.querySelector('[data-edit-mode]') || {}).value || 'draft',
                notes: (article.querySelector('[name="notes"]') || {}).value || ''
            };
            // C8 "Promote to OBSERVE for 24h" sets a hidden auto_promote_at
            // input. When present, attach it so C9's scheduler can pick it
            // up server-side. The mutation API tolerates the field even on
            // standalone FOSS (it's nullable in the DTO).
            var autoPromote = article.querySelector('input[name="auto_promote_at"]');
            if (autoPromote && autoPromote.value) {
                body.autoPromoteAt = autoPromote.value;
            }
            fetch(article.dataset.editSubmitUrl, {
                method: article.dataset.editHttpMethod || 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
                credentials: 'same-origin'
            }).then(function (resp) {
                if (resp.status === 401) {
                    window.location.href = '/login';
                    return null;
                }
                if (resp.status === 403) {
                    showValidation('forbidden -- dashboard-write role required', false);
                    return null;
                }
                if (!resp.ok) {
                    return resp.json().catch(function () { return { detail: 'HTTP ' + resp.status }; })
                        .then(function (err) {
                            showValidation((err && (err.detail || err.message)) || 'save failed', false);
                            return null;
                        });
                }
                return resp;
            }).then(function (resp) {
                if (!resp) return;
                // Swap the row back to read mode via an HTMX load trigger.
                // Build the replacement element via DOM APIs (not innerHTML/
                // outerHTML string concatenation) so even a maliciously
                // crafted edit-scope attribute can't escape attribute
                // context and inject markup.
                var rowsUrl = '/dashboard/policystack/rows?scope=' + encodeURIComponent(article.dataset.editScope) + '&tab=effective';
                var parent = article.parentElement;
                var reloader = document.createElement('div');
                reloader.setAttribute('hx-get', rowsUrl);
                reloader.setAttribute('hx-trigger', 'load');
                reloader.setAttribute('hx-swap', 'outerHTML');
                article.replaceWith(reloader);
                if (window.htmx && parent) window.htmx.process(parent);
            });
        }

        function debounce(fn, ms) {
            var t;
            return function () {
                var args = arguments;
                var self = this;
                clearTimeout(t);
                t = setTimeout(function () { fn.apply(self, args); }, ms);
            };
        }

        function countTerms(node) {
            if (!node) return 0;
            if (node.kind === 'term') return 1;
            return (node.children || []).reduce(function (s, c) { return s + countTerms(c); }, 0);
        }

        function decodePolicyScopeFromUrl(encoded) {
            // Mirror PolicyScopeUrl.Decode -- "kind|domain|subdomain|template".
            if (!encoded) return { kind: 'wildcard' };
            var parts = encoded.split('|');
            var kind = parts[0];
            return {
                kind: kind,
                domain: parts[1] ? decodeURIComponent(parts[1]) : null,
                subdomain: parts[2] ? decodeURIComponent(parts[2]) : null,
                pathTemplate: parts.length > 3 ? decodeURIComponent(parts.slice(3).join('|')) : null
            };
        }

        function collectActionFromForm(article) {
            // Task 8 -- the kind lives on the <select name="action.kind">, not
            // on the per-kind partial's <fieldset data-edit-action-kind="...">
            // (which carries the kind as a string attribute, not a `value`).
            // The per-kind partials (Tasks 4-7) emit dotted form-encoded
            // field names (action.tag.name, action.challenge.kind,
            // action.ratelimit.rate, action.throttle.rps, ...). Read each
            // by name and map to the canonical lowercase action DTO the
            // commercial mutation API consumes (Task 2 widened the DTO to
            // accept the full surface; Task 16 lands the JSONB sidecar for
            // the ratelimit fields that don't yet round-trip through the
            // discriminated-union runtime contract).
            var kindEl = article.querySelector('[data-action-kind-select]');
            var kind = kindEl ? kindEl.value : 'observe';
            var out = { kind: kind };

            function val(name) {
                var el = article.querySelector('[name="' + name + '"]');
                return el ? el.value : '';
            }
            function intOrNull(s) {
                if (s == null || s === '') return null;
                var n = parseInt(s, 10);
                return isNaN(n) ? null : n;
            }

            if (kind === 'tag') {
                out.tagName = val('action.tag.name');
            } else if (kind === 'challenge') {
                out.challengeKind = val('action.challenge.kind') || 'turnstile';
                var sk = val('action.challenge.site-key');
                if (sk) out.siteKey = sk;
            } else if (kind === 'ratelimit') {
                // Commercial DTO widening (Task 2) accepts the full surface;
                // the FOSS-only runtime currently only honours requestsPerMinute,
                // so we send both the canonical narrow shape AND the richer
                // edit-slice fields. The legacy widening in PolicyEditPresenter
                // already canonicalises rate+unit -> requestsPerMinute on read;
                // we mirror it on write so a FOSS gateway still receives a
                // valid payload if it ever lands on the wire.
                out.rate = intOrNull(val('action.ratelimit.rate')) || 6;
                out.unit = val('action.ratelimit.unit') || 'minute';
                out.key = val('action.ratelimit.key') || 'fingerprint';
                var burst = intOrNull(val('action.ratelimit.burst'));
                if (burst != null) out.burst = burst;
                var mitigation = intOrNull(val('action.ratelimit.mitigation-timeout-seconds'));
                if (mitigation != null) out.mitigationTimeoutSeconds = mitigation;
                out.overLimitAction = val('action.ratelimit.over-limit-action') || 'throttle-status';
                // Narrow-shape mirror for the FOSS runtime's RequestsPerMinute
                // bucket: rate/unit -> per-minute. The presenter uses 60 as
                // the default when the unit is "minute"; we replicate that
                // mapping here so the submit payload carries both shapes.
                out.requestsPerMinute = out.unit === 'minute'
                    ? out.rate
                    : out.unit === 'second'
                        ? out.rate * 60
                        : Math.max(1, Math.round(out.rate / 60));
            } else if (kind === 'throttle') {
                out.requestsPerSecond = intOrNull(val('action.throttle.rps')) || 10;
                var reason = val('action.throttle.reason');
                if (reason) out.reason = reason;
            }
            return out;
        }

        // Task 8 swap-aware re-binding. HTMX swaps the action-editor slot in
        // place when the kind selector changes; the article's other panes
        // (expression textarea + chip pane) stay mounted across the swap.
        // We listen at the article level so a slot swap re-fires the C8
        // backtest with the new kind, AND we re-bind the kind selector
        // because the SELECT survives the swap but the kind-string-bearing
        // fieldset inside the slot is now a fresh DOM node.
        article.addEventListener('htmx:afterSwap', function (evt) {
            if (!evt.target) return;
            // Only react when the swap landed inside this article's
            // action-editor slot -- not on every swap on the page.
            var slot = article.querySelector('[data-action-editor-slot]');
            if (!slot || !slot.contains(evt.target) && evt.target !== slot) return;
            // Re-fire backtest with the new kind. The expr value hasn't
            // changed; we just need the panel to re-project under the new
            // action.
            if (ast && expr.value.trim()) scheduleBacktest(expr.value.trim());
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { init(document); });
    } else {
        init(document);
    }
    document.body && document.body.addEventListener('htmx:afterSwap', function (evt) {
        init(evt.target || document);
    });
})();
