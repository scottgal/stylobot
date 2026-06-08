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
        var actionKind = article.querySelector('[data-edit-action-kind]');
        var form = article.querySelector('form');

        if (!expr || !chipPane || !validation || !saveBtn || !acList || !form) return;

        var ast = null;
        var lastGoodAst = null;

        // Action-kind toggle for the per-kind metadata inputs.
        if (actionKind) {
            actionKind.addEventListener('change', function () {
                var kind = actionKind.value;
                article.querySelectorAll('[data-edit-action-meta]').forEach(function (el) {
                    el.hidden = el.dataset.editActionMeta !== kind;
                });
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

        // Live re-parse on every keystroke (80ms debounce).
        expr.addEventListener('input', debounce(function () { runParse(expr.value); }, 80));
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
                el.addEventListener('input', debounce(rebuildExpressionFromChips, 80));
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
            var kindEl = article.querySelector('[data-edit-action-kind]');
            var kind = kindEl ? kindEl.value : 'observe';
            var out = { kind: kind };
            if (kind === 'challenge') {
                var c = article.querySelector('[name="challenge_kind"]');
                out.challengeKind = (c && c.value) || 'turnstile';
            } else if (kind === 'tag') {
                var t = article.querySelector('[name="tag_name"]');
                out.tagName = (t && t.value) || '';
            } else if (kind === 'ratelimit') {
                var r = article.querySelector('[name="requests_per_minute"]');
                out.requestsPerMinute = parseInt((r && r.value) || '60', 10);
            }
            return out;
        }
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
