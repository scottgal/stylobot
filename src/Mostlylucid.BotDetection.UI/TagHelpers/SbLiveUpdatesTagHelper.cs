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
        var nonce = _httpContextAccessor.HttpContext?.Items["CspNonce"]?.ToString();
        var nonceAttr = !string.IsNullOrEmpty(nonce) ? $" nonce=\"{nonce}\"" : "";

        output.TagName = null;

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

    // Hold user-active through afterSettle (not just afterRequest). Between response
    // arrival (afterRequest) and DOM update (afterSettle) there's a small window where
    // data-sb-params on the widget hasn't been updated yet. If SignalR's debounce timer
    // fires in that window, flush() reads the STALE data-sb-params and fetches the old
    // page, then its response paints over the just-arrived user response. Holding the
    // lock through settle means flush() either skips the widget (still active) or sees
    // the post-swap params (no stomp).
    document.body.addEventListener('htmx:afterSettle', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    }});

    // Belt-and-braces release: if the request errors out (no swap, no settle) we still
    // need to clear user-active so subsequent SignalR refreshes work normally.
    document.body.addEventListener('htmx:responseError', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    }});
    document.body.addEventListener('htmx:sendError', function(ev) {{
        var wid = widgetForElt(ev.detail && ev.detail.elt);
        if (wid) userActiveWidgets.delete(wid);
    }});

    // Distinguish user-initiated requests from SignalR background batches by URL.
    // User actions hit /partials/<widget-name>?... (topbots, sessions, recent, etc.);
    // SignalR refreshes hit /partials/update?widgets=... -- and ONLY the latter should
    // ever be refused when the widget is being actively driven by the user. Refusing the
    // user's own swap (which my first cut did) means clicking Next never updates the DOM,
    // the widget stays on page 1 forever, and the next SignalR refresh paints page 1 on top.
    function isSignalRBackgroundRequest(ev) {{
        var xhr = ev.detail && ev.detail.xhr;
        var url = (xhr && xhr.responseURL) || '';
        if (!url && ev.detail && ev.detail.requestConfig) url = ev.detail.requestConfig.path || '';
        if (!url && ev.detail && ev.detail.pathInfo) url = ev.detail.pathInfo.path || '';
        return url.indexOf('/partials/update') !== -1;
    }}

    document.body.addEventListener('htmx:beforeSwap', function(ev) {{
        if (!isSignalRBackgroundRequest(ev)) return;
        var target = ev.detail && ev.detail.target;
        if (!target || !target.getAttribute) return;
        var wid = target.getAttribute('data-sb-widget');
        if (wid && userActiveWidgets.has(wid)) {{
            ev.detail.shouldSwap = false;
        }}
    }});

    // OOB swaps (which SignalR refreshes use to target multiple widgets in one response)
    // fire htmx:oobBeforeSwap, NOT htmx:beforeSwap. Hook that too or the suppression
    // silently skips every multi-widget batch.
    document.body.addEventListener('htmx:oobBeforeSwap', function(ev) {{
        if (!isSignalRBackgroundRequest(ev)) return;
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
            htmx.ajax('GET', url, {{ target: 'body', swap: 'none' }});
        }}
    }}

    // Restore previously-saved widget filter/sort params from sessionStorage so that
    // switching dashboard tabs preserves the user's last filter selection.
    document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
        var wid = el.getAttribute('data-sb-widget');
        var saved = sessionStorage.getItem('sb:wp:' + wid);
        if (saved) el.setAttribute('data-sb-params', saved);
    }});

    // After any HTMX swap settles (including OOB), persist current widget params.
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
