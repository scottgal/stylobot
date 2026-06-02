using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Renders an inline JavaScript probe that fetches a real ad-network resource
///     and beacons back to StyloBot when the request is blocked (browser extension,
///     Pi-hole, corporate proxy). The result drives the
///     <see cref="SignalKeys.ClientSideAdblockerDetected"/> signal so
///     <c>ClientSideContributor</c> can suppress the no-fingerprint penalty for
///     legitimate users whose adblocker blocked the fingerprint script along
///     with everything else. See <c>docs/adblocker-detection.md</c>.
///
///     <para>
///     Usage:
///     <code>
///     &lt;sb:adblock-probe provider="adsense" client-id="ca-pub-1234567890" /&gt;
///     </code>
///     or with a custom URL:
///     <code>
///     &lt;sb:adblock-probe probe-url="https://static.ads-twitter.com/uwt.js" /&gt;
///     </code>
///     </para>
/// </summary>
[HtmlTargetElement("sb:adblock-probe")]
public class AdblockerProbeTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly BotDetectionOptions _options;
    // Same token contract as the fingerprint TagHelper: the beacon endpoint
    // rejects bodies without a signed token to prevent any bot from spoofing
    // "I have an adblocker" beacons. sendBeacon can't set request headers, so
    // the token rides in the JSON body and the endpoint accepts it from
    // either header or body.
    private readonly IBrowserTokenService? _tokenService;

    public AdblockerProbeTagHelper(
        IOptions<BotDetectionOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IBrowserTokenService? tokenService = null)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _tokenService = tokenService;
    }

    /// <summary>
    ///     Provider alias. Determines the probe URL template (see Providers table
    ///     below). Required when <see cref="ProbeUrl"/> is not set.
    /// </summary>
    [HtmlAttributeName("provider")]
    public string? Provider { get; set; }

    /// <summary>
    ///     Publisher client ID for the provider (e.g. <c>ca-pub-1234567890</c> for
    ///     AdSense). Provider-specific format. Required when
    ///     <see cref="ProbeUrl"/> is not set and the provider template includes
    ///     <c>{clientId}</c>.
    /// </summary>
    [HtmlAttributeName("client-id")]
    public string? ClientId { get; set; }

    /// <summary>
    ///     Exact URL to probe. Overrides <see cref="Provider"/> and
    ///     <see cref="ClientId"/>. Use this when you don't have a relationship
    ///     with one of the named providers -- any URL on a major filter list
    ///     (EasyList, EasyPrivacy, uBO filter hub) works.
    /// </summary>
    [HtmlAttributeName("probe-url")]
    public string? ProbeUrl { get; set; }

    /// <summary>
    ///     Timeout in ms before treating the probe as blocked. Overrides the
    ///     <c>BotDetection:ClientSide:AdblockerProbeTimeoutMs</c> config default
    ///     when set as a tag attribute. -1 means "use the config default".
    /// </summary>
    [HtmlAttributeName("timeout-ms")]
    public int TimeoutMs { get; set; } = -1;

    /// <summary>
    ///     Override the beacon endpoint path. Default
    ///     <c>/bot-detection/fingerprint</c> -- the same endpoint the main
    ///     fingerprint script uses. The endpoint recognises adblocker-only
    ///     beacons (Adblocker=true, Timestamp=0) and short-circuits the
    ///     integrity analysis.
    /// </summary>
    [HtmlAttributeName("beacon-path")]
    public string BeaconPath { get; set; } = "/bot-detection/fingerprint";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // Suppress for non-document hosts, when client-side detection is disabled,
        // when no HttpContext is present (dashboard-viewer hosts), or when no
        // token service is registered (the endpoint requires a signed token).
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

        var probeUrl = ResolveProbeUrl();
        if (string.IsNullOrEmpty(probeUrl))
        {
            // No usable URL -- caller misconfigured. Render nothing rather than
            // emit a script that fails. Log via the HttpContext on a defensive
            // best-effort basis; AOT cannot inject ILogger directly into a
            // TagHelper at view-execution time.
            output.SuppressOutput();
            return;
        }

        var token = _tokenService.GenerateToken(httpContext);
        var providerAlias = string.IsNullOrEmpty(Provider) ? "custom" : Provider!.ToLowerInvariant();
        var resolvedTimeoutMs = TimeoutMs > 0 ? TimeoutMs : _options.ClientSide.AdblockerProbeTimeoutMs;
        var timeout = Math.Clamp(resolvedTimeoutMs, 250, 30_000);

        output.TagName = "script";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Content.SetHtmlContent(BuildScript(probeUrl, providerAlias, BeaconPath, token, timeout));
    }

    /// <summary>
    ///     Probe URL templates for the named providers. The dictionary lives here
    ///     rather than in a config file because the URLs are the actual ad-network
    ///     endpoints -- they're stable, public, and don't belong to the operator.
    /// </summary>
    private string? ResolveProbeUrl()
    {
        if (!string.IsNullOrEmpty(ProbeUrl)) return ProbeUrl;

        var provider = Provider?.ToLowerInvariant();
        return provider switch
        {
            "adsense" when !string.IsNullOrEmpty(ClientId)
                => $"https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client={Uri.EscapeDataString(ClientId)}",
            "amazon"
                => "https://c.amazon-adsystem.com/aax2/apstag.js",
            "medianet" when !string.IsNullOrEmpty(ClientId)
                => $"https://contextual.media.net/dmedianet.js?cid={Uri.EscapeDataString(ClientId)}",
            _ => null,
        };
    }

    /// <summary>
    ///     Build the inline probe script. The IIFE attempts to fetch the probe URL
    ///     with <c>mode: 'no-cors'</c> so cross-origin responses don't trigger
    ///     errors indistinguishable from blocking. A network error or timeout
    ///     fires <c>navigator.sendBeacon</c> with <c>{adblocker: true,
    ///     adblockerProvider: ...}</c>. The endpoint analyzer recognises the
    ///     shape and skips integrity analysis. The script is inlined (not
    ///     <c>src=</c>) so it cannot itself be blocked by a filter URL match.
    /// </summary>
    private static string BuildScript(string probeUrl, string providerAlias, string beaconPath, string token, int timeoutMs)
    {
        // Use the framework's JavaScriptEncoder for the JS literals. The earlier
        // hand-rolled Replace("\\","\\\\").Replace("'","\\'") missed </script>
        // breakout sequences, < > / characters in tokens (could appear in a
        // base64url-encoded payload), and Unicode line separators (U+2028/9 that
        // JS treats as line terminators). JavaScriptEncoder.Default handles all
        // of those, is AOT-clean, and is already the standard tool for this in
        // ASP.NET Core. The IIFE wrapper expects a JS-literal-safe single-quoted
        // string for each interpolated value.
        var enc = JavaScriptEncoder.Default;
        var escUrl = enc.Encode(probeUrl);
        var escProvider = enc.Encode(providerAlias);
        var escBeacon = enc.Encode(beaconPath);
        var escToken = enc.Encode(token);

        // The token rides in the body (field `t`) because sendBeacon can't set
        // headers. The endpoint accepts token from either Authorization-style
        // header or this body field. The probe is wrapped in a try/catch so a
        // CSP block, sendBeacon absence, or JSON serialisation error never
        // escapes -- a probe failure must not break the host page.
        return $@"(function(){{
try{{
var done=false;
function report(){{
  if(done)return;done=true;
  if(!navigator.sendBeacon)return;
  var p=JSON.stringify({{t:'{escToken}',adblocker:true,adblockerProvider:'{escProvider}'}});
  var b=new Blob([p],{{type:'application/json'}});
  navigator.sendBeacon('{escBeacon}',b);
}}
var to=setTimeout(report,{timeoutMs});
fetch('{escUrl}',{{mode:'no-cors',cache:'no-store',credentials:'omit'}})
  .then(function(){{clearTimeout(to);done=true;}})
  .catch(function(){{clearTimeout(to);report();}});
}}catch(e){{}}
}})();";
    }
}
