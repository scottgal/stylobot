using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Mounts the StyloBot HTMX + SignalR live-updates coordinator.
///     Place this tag at the bottom of your page (before <c>&lt;/body&gt;</c>) to enable
///     live updates for any StyloBot widget partials on the page.
///     <para>
///     Emits a single static <c>&lt;link&gt;</c> and a single static <c>&lt;script src&gt;</c>
///     against the FOSS UI package's vendored assets -- no inline script or style.
///     Configuration (base path, hub URL, debounce, cooldown, refresh interval) is
///     passed to the JS via <c>data-*</c> attributes on the script tag and read by
///     the coordinator at startup via <c>document.currentScript.dataset</c>.
///     </para>
///     <para>
///     Requires HTMX and the SignalR client to be loaded before this tag. Each
///     widget partial declares <c>data-sb-widget</c> and <c>data-sb-depends</c> so the
///     coordinator knows which widgets to refresh when a beacon arrives.
///     </para>
/// </summary>
/// <example>
///     <code>
///     &lt;script src="/_content/Mostlylucid.BotDetection.UI/vendor/js/htmx.min.js"&gt;&lt;/script&gt;
///     &lt;script src="/_content/Mostlylucid.BotDetection.UI/vendor/js/signalr.min.js"&gt;&lt;/script&gt;
///     &lt;sb-live-updates /&gt;
///     </code>
/// </example>
[HtmlTargetElement("sb-live-updates", TagStructure = TagStructure.WithoutEndTag)]
public class SbLiveUpdatesTagHelper : TagHelper
{
    private const string AssetCssPath       = "/_content/Mostlylucid.BotDetection.UI/vendor/css/sb-live-updates.css";
    private const string AssetJsPath        = "/_content/Mostlylucid.BotDetection.UI/vendor/js/sb-live-updates.js";
    private const string IdiomorphScriptPath = "/_content/Mostlylucid.BotDetection.UI/vendor/js/idiomorph-ext.min.js";

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
        var refreshMs = RefreshInterval * 1000;
        var nonce = _httpContextAccessor.HttpContext?.Items["CspNonce"]?.ToString();
        var nonceAttr = !string.IsNullOrEmpty(nonce) ? $" nonce=\"{nonce}\"" : "";

        output.TagName = null;

        // Vendored stylesheet -- contains the [data-sb-widget] containment + view-
        // transition opt-out rule. Inline <style> would force unsafe-inline in the
        // CSP; the <link> reference stays self-hosted and nonceable.
        output.Content.AppendHtml($@"<link rel=""stylesheet"" href=""{AssetCssPath}"" />");

        // Idiomorph -- official htmx extension that swaps DOM in place via a morph
        // algorithm (unchanged nodes are left alone, only deltas mutate). Combined
        // with hx-swap-oob="morph:innerHTML" on the server-side OOB fragments this
        // is the "mutate on update, don't replace" half of the live-updates design.
        // Self-hosted from the package's vendor/js so no CDN dependency.
        output.Content.AppendHtml($@"<script src=""{IdiomorphScriptPath}""{nonceAttr}></script>");

        if (ShowStatus)
        {
            // Live-updates toggle + status dot. The button is the operator's
            // off-switch for the SignalR connection -- pressing it sets
            // localStorage['sb:live-updates']='paused' and the coordinator
            // (sb-live-updates.js) refuses to fire any flush until it's set
            // back to 'live'. The status dot inside the button reflects the
            // SignalR connection state (connected / connecting / disconnected
            // / paused). Both the toggle and the status dot live in the same
            // span so they sit together in the dashboard header without extra
            // markup from the caller.
            output.Content.AppendHtml(
                "<button type=\"button\" id=\"sb-live-toggle\" " +
                "class=\"inline-flex items-center gap-1.5 text-[10px] font-medium px-2 py-0.5 rounded-md " +
                "bg-base-200 hover:bg-base-300 text-base-content/60 hover:text-base-content border border-base-300/60\" " +
                "data-state=\"live\" title=\"Live updates: on (click to pause)\" " +
                "aria-label=\"Toggle live updates\">" +
                "<span id=\"sb-connection-status\" class=\"w-2 h-2 rounded-full sb-disconnected\" " +
                "style=\"display:inline-block\"></span>" +
                "<span id=\"sb-live-toggle-label\">LIVE</span>" +
                "</button>\n");
        }

        // Vendored coordinator script. Config travels on data-* attrs (read by
        // sb-live-updates.js via document.currentScript.dataset) so no inline
        // <script> body is emitted -- the strict CSP only needs to allow the
        // package's own /_content/ origin.
        var basePathAttr = System.Web.HttpUtility.HtmlAttributeEncode(basePath);
        var hubUrlAttr   = System.Web.HttpUtility.HtmlAttributeEncode(hubUrl);
        output.Content.AppendHtml(
            $@"<script src=""{AssetJsPath}""" +
            $@" data-base-path=""{basePathAttr}""" +
            $@" data-hub-url=""{hubUrlAttr}""" +
            $@" data-debounce=""{DebounceMs}""" +
            $@" data-cooldown=""{cooldownMs}""" +
            $@" data-refresh-interval=""{refreshMs}""{nonceAttr}></script>");
    }
}
