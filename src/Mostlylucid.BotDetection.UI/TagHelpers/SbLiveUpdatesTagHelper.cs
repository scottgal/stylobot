using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Injects the StyloBot HTMX + SignalR coordinator script.
///     Place this tag at the bottom of your page (before &lt;/body&gt;) to enable
///     live updates for any StyloBot widget partials on the page.
///     <para>
///     Requires: HTMX (&lt;script src="htmx.org"&gt;) and SignalR client loaded before this tag.
///     Each widget partial declares <c>data-sb-widget</c> and <c>data-sb-depends</c> attributes
///     so the coordinator knows which widgets to refresh when SignalR events arrive.
///     </para>
/// </summary>
/// <example>
///     <code>
///     &lt;script src="https://unpkg.com/htmx.org@@2.0.4"&gt;&lt;/script&gt;
///     &lt;script src="https://cdn.jsdelivr.net/npm/@@microsoft/signalr@@8.0.0/dist/browser/signalr.min.js"&gt;&lt;/script&gt;
///     &lt;sb-live-updates /&gt;
///     </code>
/// </example>
[HtmlTargetElement("sb-live-updates", TagStructure = TagStructure.WithoutEndTag)]
public class SbLiveUpdatesTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StyloBotDashboardOptions? _options;

    public SbLiveUpdatesTagHelper(
        IHttpContextAccessor httpContextAccessor,
        StyloBotDashboardOptions? options = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    /// <summary>Override the SignalR hub URL. Defaults to the configured HubPath.</summary>
    [HtmlAttributeName("hub-url")]
    public string? HubUrl { get; set; }

    /// <summary>Override the base path for partial endpoints. Defaults to configured BasePath.</summary>
    [HtmlAttributeName("base-path")]
    public string? BasePath { get; set; }

    /// <summary>Debounce interval in milliseconds. Defaults to 500.</summary>
    [HtmlAttributeName("debounce")]
    public int DebounceMs { get; set; } = 500;

    /// <summary>
    ///     Override the user-active cooldown window in milliseconds. Defaults to
    ///     <see cref="StyloBotDashboardOptions.UserActiveCooldownMs"/> (3000).
    ///     Lower values risk the late-arriving-OOB clobber race; raise only if you
    ///     see operators losing filter state under genuine SignalR-storm conditions.
    /// </summary>
    [HtmlAttributeName("cooldown")]
    public int? CooldownMs { get; set; }

    /// <summary>Periodic refresh interval in seconds. Set to 0 to disable. Defaults to 30.</summary>
    [HtmlAttributeName("refresh-interval")]
    public int RefreshInterval { get; set; } = 30;

    /// <summary>Show a connection status indicator. Defaults to true.</summary>
    [HtmlAttributeName("show-status")]
    public bool ShowStatus { get; set; } = true;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var basePath = BasePath ?? _options?.BasePath.TrimEnd('/') ?? "/_stylobot";
        var hubUrl = HubUrl ?? _options?.HubPath ?? $"{basePath}/hub";
        var cooldownMs = CooldownMs ?? _options?.UserActiveCooldownMs ?? 3000;
        var nonce = _httpContextAccessor.HttpContext?.Items["CspNonce"]?.ToString();
        var nonceAttr = !string.IsNullOrEmpty(nonce) ? $" nonce=\"{nonce}\"" : "";

        output.TagName = null;

        // Cross-swap transition via the browser's View Transitions API. WidgetRenderHelpers
        // emits hx-swap-oob="outerHTML transition:true" on OOB responses so HTMX hands
        // the old->new replacement to the browser instead of doing a hard remove-then-insert.
        // Two pieces here:
        //
        //   1. JS sets a unique `view-transition-name` per [data-sb-widget] so the browser
        //      pairs the old and new elements correctly and animates only the widget rather
        //      than the entire document. We assign on initial page load and refresh after
        //      every settle so newly-swapped widgets get the name too.
        //
        //   2. The ::view-transition-old/-new pseudo-element CSS keeps the cross-fade short
        //      (160ms) and predictable. The old element fades out and slides up 2px while
        //      the new fades in and slides down 2px -- the motion masks the brief overlap
        //      so the operator sees a fade, not a flash.
        //
        // The previous approach (CSS animation gated on the .htmx-added class) raced HTMX's
        // 20ms settle window: the class came off before the 180ms animation could finish.
        // View Transitions don't depend on class lifetime so there's no race.
        output.Content.AppendHtml($@"<style{nonceAttr}>
[data-sb-widget] {{ contain: layout style; }}
::view-transition-old(sb-widget) {{
  animation: sb-vt-old 140ms cubic-bezier(0.2, 0.7, 0.2, 1) both;
}}
::view-transition-new(sb-widget) {{
  animation: sb-vt-new 160ms cubic-bezier(0.2, 0.7, 0.2, 1) both;
}}
@keyframes sb-vt-old {{
  from {{ opacity: 1; transform: translateY(0);    }}
  to   {{ opacity: 0; transform: translateY(-2px); }}
}}
@keyframes sb-vt-new {{
  from {{ opacity: 0; transform: translateY(2px); }}
  to   {{ opacity: 1; transform: translateY(0);   }}
}}
</style>
<script{nonceAttr}>
(function() {{
    // Assign a unique view-transition-name per widget so the browser can pair the
    // old element with its replacement across the OOB swap. We use the widget id
    // (data-sb-widget value) and re-assign on every settle so freshly-swapped
    // widgets pick up a name too. Falling back to {{vt-name}} = sb-widget keeps the
    // CSS selectors deterministic.
    function nameWidgets() {{
        // Only legacy widgets (those that get outerHTML-swapped wholesale on a beacon)
        // need the view-transition cross-fade to mask the flash. Widgets that have
        // adopted the two-region contract -- a [data-sb-data-region] descendant means
        // SignalR beacons only innerHTML-swap that inner region, the chrome stays put
        // and there is no flash to mask. Assigning view-transition-name there would
        // make the browser cross-fade the whole widget on every beacon, which the
        // operator perceives as pulsing.
        document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
            var hasDataRegion = !!el.querySelector('[data-sb-data-region]');
            if (hasDataRegion) {{
                // 'none' is the explicit CSS value that opts out of view transitions.
                // Empty string only clears the inline declaration -- a parent or :root
                // rule could still apply. 'none' is unambiguous and also avoids the
                // compositor-layer promotion that causes the widget to float on scroll.
                el.style.viewTransitionName = 'none';
                return;
            }}
            if (!el.style.viewTransitionName || el.style.viewTransitionName === 'none') {{
                el.style.viewTransitionName = 'sb-widget';
            }}
        }});
    }}
    if (document.readyState !== 'loading') nameWidgets();
    else document.addEventListener('DOMContentLoaded', nameWidgets);
    document.body.addEventListener('htmx:afterSettle', nameWidgets);
}})();
</script>
");

        if (ShowStatus)
        {
            output.Content.AppendHtml(
                "<span id=\"sb-connection-status\" class=\"w-2 h-2 rounded-full sb-disconnected\" " +
                "title=\"SignalR: disconnected\" style=\"display:inline-block\"></span>\n");
        }

        // Serialize via JsonSerializer so any special chars (quotes, backslashes) are JS-safe.
        var basePathJson = JsonSerializer.Serialize(basePath);
        var hubUrlJson   = JsonSerializer.Serialize(hubUrl);

        output.Content.AppendHtml($@"<script{nonceAttr}>
(function() {{
    'use strict';
    var BASE = {basePathJson};
    var HUB  = {hubUrlJson};
    var DEBOUNCE_MS = {DebounceMs};

    function getWidgetMap() {{
        var map = {{}};
        document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
            var deps = (el.getAttribute('data-sb-depends') || '').split(',');
            deps.forEach(function(dep) {{
                dep = dep.trim();
                if (!dep) return;
                if (!map[dep]) map[dep] = [];
                var wid = el.getAttribute('data-sb-widget');
                if (map[dep].indexOf(wid) === -1) map[dep].push(wid);
            }});
        }});
        return map;
    }}

    var pending = {{}};
    var debounceTimer = null;

    // General pattern: a widget the user is actively driving (filter click, sort header,
    // page nav, any in-flight user-initiated HTMX request whose source sits inside that
    // widget) WINS over SignalR-driven background refreshes. We track that set here and
    // gate both directions:
    //   - outgoing refresh requests skip user-active widgets (no stale params sent),
    //   - incoming OOB swap responses targeting user-active widgets are refused (no
    //     race where a SignalR refresh already in flight clobbers the just-clicked filter).
    var userActiveWidgets = new Set();

    function widgetForElt(elt) {{
        if (!elt || !elt.closest) return null;
        var root = elt.closest('[data-sb-widget]');
        return root ? root.getAttribute('data-sb-widget') : null;
    }}

    document.body.addEventListener('htmx:beforeRequest', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.add(wid);
    }});

    // After the user's swap settles, hold user-active for a brief COOLDOWN so any
    // SignalR refresh whose REQUEST was fired BEFORE the user click but whose RESPONSE
    // arrives AFTER the user settle still gets refused. Without the cooldown that
    // late-arriving OOB swap silently restores the pre-click state -- the operator
    // clicks Bot, sees bots for ~200ms, then the widget flips back to All. The
    // cooldown window is configurable via StyloBotDashboardOptions.UserActiveCooldownMs
    // (default 3s); subsequent refreshes that fire AFTER the cooldown read the current
    // post-swap data-sb-params and are harmless.
    var COOLDOWN_MS = {cooldownMs};
    var cooldownTimers = {{}};
    function scheduleRelease(wid) {{
        if (!wid) return;
        if (cooldownTimers[wid]) clearTimeout(cooldownTimers[wid]);
        cooldownTimers[wid] = setTimeout(function() {{
            userActiveWidgets.delete(wid);
            delete cooldownTimers[wid];
        }}, COOLDOWN_MS);
    }}

    document.body.addEventListener('htmx:afterSettle', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) scheduleRelease(wid);
    }});

    // Belt-and-braces release: if the request errors out (no swap, no settle) we still
    // need to clear user-active so subsequent SignalR refreshes work normally. No
    // cooldown here -- there's no successful swap result to protect.
    document.body.addEventListener('htmx:responseError', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    }});

    // Global row-click delegation: any element carrying data-href becomes a clickable
    // row that navigates to that URL on click. Used by the Behavioral Sessions table,
    // the EndpointsCompact rows, and any future widget that wants a whole-row click
    // target without nesting inline onclick handlers (CSP-safe). Real interactive
    // children (links, buttons, form fields, htmx triggers) keep their own click
    // behaviour via the exclusion guard.
    document.body.addEventListener('click', function(ev) {{
        if (ev.target.closest('a, button, input, select, textarea, label, [hx-get], [hx-post]')) return;
        var row = ev.target.closest('[data-href]');
        if (!row) return;
        var href = row.getAttribute('data-href');
        if (href) window.location.href = href;
    }});
    document.body.addEventListener('htmx:sendError', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    }});

    // Distinguishing user vs background requests by URL doesn't work for OOB swaps --
    // htmx:oobBeforeSwap fires with no xhr or pathInfo on the event detail, so any URL
    // lookup returns empty. The simpler invariant for this app: OOB swaps are ONLY ever
    // generated by /partials/update (the SignalR batch endpoint). User clicks use direct
    // hx-target / hx-swap on the widget root and fire normal beforeSwap, not OOB. So
    // suppressing OOB swaps to user-active widgets is the right rule regardless of URL.
    //
    // The previous attempt's beforeSwap handler was also wrong: ev.detail.target for a
    // SignalR /partials/update response is the BODY (not the widget root), and the widget
    // root only appears on the OOB sub-swap. So checking target.data-sb-widget at
    // beforeSwap-time never matched and the handler was a no-op. Dropped entirely.
    document.body.addEventListener('htmx:oobBeforeSwap', function(ev) {{
        var target = ev.detail && ev.detail.target;
        if (!target || !target.getAttribute) return;
        var wid = target.getAttribute('data-sb-widget');
        if (wid && userActiveWidgets.has(wid)) {{
            ev.detail.shouldSwap = false;
        }}
    }});

    function invalidate(signal) {{
        var widgetMap = getWidgetMap();
        var widgets = widgetMap[signal] || [];
        widgets.forEach(function(w) {{ pending[w] = true; }});
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(flush, DEBOUNCE_MS);
    }}

    function flush() {{
        var ids = Object.keys(pending).filter(function(id) {{
            return !userActiveWidgets.has(id);
        }});
        pending = {{}};
        if (ids.length === 0) return;

        var qs = new URLSearchParams();
        qs.set('widgets', ids.join(','));

        ids.forEach(function(wid) {{
            var el = document.querySelector('[data-sb-widget=""' + wid + '""]');
            if (!el) return;
            var raw = el.getAttribute('data-sb-params');
            if (!raw) return;
            try {{
                new URLSearchParams(raw).forEach(function(val, key) {{
                    if (val !== '' && val !== 'undefined' && val !== 'null')
                        qs.set(wid + '.' + key, val);
                }});
            }} catch(e) {{ }}
        }});

        var url = BASE + '/partials/update?' + qs.toString();
        if (typeof htmx !== 'undefined') {{
            // Wrap the OOB batch in the View Transitions API when available so the
            // browser cross-fades old->new on every [data-sb-widget] swapped in this
            // batch. The DOM mutations htmx.ajax triggers inside the callback are
            // captured by startViewTransition; the browser snapshots the before
            // state, runs the swaps synchronously inside the callback, then animates
            // old -> new using the view-transition-name we set per widget. Falls
            // through to a plain swap when the API is unavailable (Firefox today).
            if (typeof document.startViewTransition === 'function') {{
                document.startViewTransition(function() {{
                    return new Promise(function(resolve) {{
                        htmx.ajax('GET', url, {{ target: 'body', swap: 'none' }}).then(resolve, resolve);
                    }});
                }});
            }} else {{
                htmx.ajax('GET', url, {{ target: 'body', swap: 'none' }});
            }}
        }}
    }}

    // Restore previously-saved widget filter/sort params from sessionStorage so that
    // switching dashboard tabs preserves the user's last filter selection. Only the
    // user-driven keys (filter, sort, dir, page) are carried across visits; page-author
    // keys (pageSize, widgetId) ALWAYS come from the fresh SSR-rendered data-sb-params
    // for THIS placement. Without this split, the overview tab's live-activity widget
    // (pageSize=10) wrote sessionStorage that then clobbered the activity tab's
    // SSR pageSize=25, the SignalR refresh sent pageSize=10 in the params, the
    // partial returned 10 rows, and the visitor saw the SSR list shorten by 60%
    // on the first OOB swap (the long-flagged 'loads long, instantly shortens' bug).
    var USER_DRIVEN_KEYS = ['filter', 'sort', 'dir', 'page'];
    document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
        var wid = el.getAttribute('data-sb-widget');
        var saved = sessionStorage.getItem('sb:wp:' + wid);
        if (!saved) return;
        try {{
            var fresh = new URLSearchParams(el.getAttribute('data-sb-params') || '');
            var savedParams = new URLSearchParams(saved);
            USER_DRIVEN_KEYS.forEach(function(key) {{
                if (savedParams.has(key)) fresh.set(key, savedParams.get(key));
            }});
            el.setAttribute('data-sb-params', fresh.toString());
        }} catch(e) {{ }}
    }});

    // After any HTMX swap settles (including OOB), persist current widget params.
    // We save the whole string but the restore above only consumes user-driven keys,
    // so re-saving pageSize is harmless -- it stays attached but is ignored when
    // restored against a different placement.
    document.body.addEventListener('htmx:afterSettle', function() {{
        document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
            var wid = el.getAttribute('data-sb-widget');
            var params = el.getAttribute('data-sb-params');
            if (wid && params) sessionStorage.setItem('sb:wp:' + wid, params);
        }});
    }});

    if (typeof signalR === 'undefined') {{ console.warn('StyloBot: signalR not loaded'); return; }}

    var statusEl = document.getElementById('sb-connection-status');
    function setStatus(state) {{
        if (!statusEl) return;
        statusEl.className = 'w-2 h-2 rounded-full sb-' + state;
        statusEl.title = 'SignalR: ' + state;
    }}

    var connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB)
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    // SignalR is beacon-only: server sends lightweight invalidation signals,
    // client triggers HTMX partial refreshes. No data payloads over the wire.
    connection.on('BroadcastInvalidation', function(signal) {{ if (signal) invalidate(signal); }});

    connection.onreconnecting(function() {{ setStatus('connecting'); }});
    connection.onreconnected(function()  {{ setStatus('connected'); }});
    connection.onclose(function()        {{ setStatus('disconnected'); }});

    connection.start()
        .then(function()  {{ setStatus('connected'); }})
        .catch(function() {{ setStatus('disconnected'); }});

    var REFRESH_MS = {RefreshInterval * 1000};
    if (REFRESH_MS > 0) {{
        setInterval(function() {{
            document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
                var wid = el.getAttribute('data-sb-widget');
                if (wid) pending[wid] = true;
            }});
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(flush, DEBOUNCE_MS);
        }}, REFRESH_MS);
    }}
}})();
</script>");
    }
}
