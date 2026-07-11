using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Renders the bootstrap pair that loads the StyloBot client-side detector:
///     <list type="number">
///         <item>An inline <c>&lt;script&gt;</c> block that publishes
///               <c>window.StyloBot = { t, e, cfg }</c> -- token, beacon endpoint,
///               feature toggles.</item>
///         <item>A <c>&lt;script src="/bot-detection/script.js"&gt;</c> tag
///               that loads the actual detector logic from the endpoint
///               registered by <c>MapBotDetectionScript</c>. The browser
///               caches it; the served bytes are exactly what the repo's
///               <c>ClientSide/botdetection.js</c> contains.</item>
///     </list>
///     <para>
///     Replaces the prior in-tag-helper inline IIFE that grew to ~80 lines of
///     JavaScript embedded in a C# string -- a maintenance liability and an
///     auditing problem (the JS in the repo's <c>botdetection.js</c> file was
///     a third, unused twin). One source of truth now: the .js file.
///     </para>
///     <para>
///     CSP: if the host site uses <c>script-src 'self' 'nonce-...' 'strict-dynamic'</c>,
///     pass the nonce via the <c>nonce</c> attribute and both emitted script
///     tags will carry it. With <c>'strict-dynamic'</c> the nonce on the
///     bootstrap propagates to the loaded <c>/bot-detection/script.js</c>.
///     </para>
///     <para>
///     Usage:
///     <code>
///     &lt;bot-detection-script /&gt;
///     &lt;!-- with overrides --&gt;
///     &lt;bot-detection-script endpoint="/bot-detection/fingerprint"
///                            script-path="/bot-detection/script.js"
///                            nonce="@nonce" /&gt;
///     </code>
///     </para>
/// </summary>
[HtmlTargetElement("bot-detection-script")]
public class BotDetectionTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly BotDetectionOptions _options;
    // Optional: token service is registered by AddBotDetection. In pure
    // dashboard-viewer hosts (no detection pipeline) it is absent -- the
    // tag helper suppresses output so the page renders nothing for the
    // client-side fingerprint script. That matches the architecture
    // contract: viewers don't collect or issue tokens.
    private readonly IBrowserTokenService? _tokenService;

    public BotDetectionTagHelper(
        IOptions<BotDetectionOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IBrowserTokenService? tokenService = null)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _tokenService = tokenService;
    }

    /// <summary>Beacon endpoint the detector posts the payload to.</summary>
    [HtmlAttributeName("endpoint")]
    public string Endpoint { get; set; } = "/bot-detection/fingerprint";

    /// <summary>Static-script path served by <c>MapBotDetectionScript</c>.</summary>
    [HtmlAttributeName("script-path")]
    public string ScriptPath { get; set; } = "/bot-detection/script.js";

    /// <summary>Defer the external script (default true -- runs after parse).</summary>
    [HtmlAttributeName("defer")]
    public bool Defer { get; set; } = true;

    /// <summary>Async-load the external script (default false; defer is usually preferable).</summary>
    [HtmlAttributeName("async")]
    public bool Async { get; set; } = false;

    /// <summary>
    ///     CSP nonce -- attached to BOTH emitted script tags. With
    ///     <c>'strict-dynamic'</c> the nonce on the inline bootstrap propagates
    ///     to the dynamically-loaded <c>script.js</c> anyway, but emitting it
    ///     on both is the safe default.
    /// </summary>
    [HtmlAttributeName("nonce")]
    public string? Nonce { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // Three suppress conditions: client-side detection disabled, no
        // active HttpContext (dashboard-viewer hosts), or no token service
        // registered (token signs the bootstrap; without it the server
        // rejects the beacon as spoofed).
        if (!_options.ClientSide.Enabled || _tokenService is null)
        {
            output.SuppressOutput();
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            output.SuppressOutput();
            return;
        }

        var token = _tokenService.GenerateToken(httpContext);
        var opts = _options.ClientSide;

        // The bootstrap script publishes the config object and is the ONLY
        // inline JS in this surface; the detector itself loads from the
        // /bot-detection/script.js endpoint and can be CSP-restricted to
        // 'self'. The bootstrap is a single object literal -- no logic, no
        // function definitions -- so it stays trivially auditable.
        // Auto-pick the request's CSP nonce when no explicit `nonce=` attribute
        // was passed -- the dashboard middleware writes one to
        // HttpContext.Items["CspNonce"] and emits `script-src 'self'
        // 'nonce-...' 'unsafe-eval'` so an inline script without the nonce is
        // blocked outright. Honour an explicit attribute first; otherwise fall
        // back to the shared per-request nonce.
        var resolvedNonce = Nonce;
        if (string.IsNullOrEmpty(resolvedNonce)
            && httpContext.Items.TryGetValue("CspNonce", out var nonceObj)
            && nonceObj is string ctxNonce
            && !string.IsNullOrEmpty(ctxNonce))
        {
            resolvedNonce = ctxNonce;
        }
        var nonceAttr = string.IsNullOrEmpty(resolvedNonce) ? "" : $" nonce=\"{System.Net.WebUtility.HtmlEncode(resolvedNonce)}\"";
        var deferAttr = Defer ? " defer" : "";
        var asyncAttr = Async ? " async" : "";

        // BotD URL only ships in the bootstrap when enabled; default off so
        // existing CSP setups don't suddenly attempt a cross-origin fetch.
        var botdUrl = opts.Botd.Enabled ? opts.Botd.ScriptUrl ?? "" : "";
        var bootstrap = $"window.StyloBot={{t:'{JsEscape(token)}',e:'{JsEscape(Endpoint)}',cfg:{{collectWebGL:{B(opts.CollectWebGL)},collectCanvas:{B(opts.CollectCanvas)},collectAudio:{B(opts.CollectAudio)},collectInteraction:{B(true)},timeout:{opts.CollectionTimeoutMs},iceStun:'{JsEscape(opts.IceStunServerUrl ?? "")}',botdUrl:'{JsEscape(botdUrl)}',th:'{JsEscape(opts.TokenHeader)}',sh:'{JsEscape(opts.SignatureHeader)}'}}}};";

        var html =
            $"<script{nonceAttr}>{bootstrap}</script>" +
            $"<script src=\"{System.Net.WebUtility.HtmlEncode(ScriptPath)}\"{deferAttr}{asyncAttr}{nonceAttr}></script>";

        output.TagName = null; // erase the custom-element wrapper; we emit raw <script> tags
        output.Content.SetHtmlContent(html);
    }

    private static string B(bool v) => v ? "true" : "false";

    /// <summary>
    ///     JS literal escaping for the bootstrap config values. Tokens are
    ///     base64-shaped and endpoint paths are operator-controlled; we still
    ///     defensively escape backslashes + quotes + the line-separator chars
    ///     U+2028/U+2029 that JS treats as line terminators (the same
    ///     hardening applied to <c>AdblockerProbeTagHelper</c>).
    /// </summary>
    private static string JsEscape(string s)
        => System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(s);
}

/// <summary>
///     Script version metadata. The actual script source lives in
///     <c>ClientSide/botdetection.js</c>; this constant is referenced by
///     telemetry / logging only.
/// </summary>
public static class BotDetectionScript
{
    public const string Version = "2.0.0";
}
