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

    function invalidate(signal) {{
        var widgetMap = getWidgetMap();
        var widgets = widgetMap[signal] || [];
        widgets.forEach(function(w) {{ pending[w] = true; }});
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(flush, DEBOUNCE_MS);
    }}

    function flush() {{
        var ids = Object.keys(pending);
        if (ids.length === 0) return;
        pending = {{}};

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
