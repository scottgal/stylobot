using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Tag helper that injects a lightweight browser fingerprinting script.
///     The script collects minimal, non-invasive signals to detect headless browsers
///     and automation frameworks, then posts results to a server endpoint.
///     Usage:
///     <![CDATA[
///     <bot-detection-script />
///     <!-- or with options -->
///     <bot-detection-script endpoint="/bot-detection/fingerprint" defer="true" />
///     ]]>
/// </summary>
[HtmlTargetElement("bot-detection-script")]
public class BotDetectionTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly BotDetectionOptions _options;
    private readonly IBrowserTokenService _tokenService;

    public BotDetectionTagHelper(
        IOptions<BotDetectionOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IBrowserTokenService tokenService)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _tokenService = tokenService;
    }

    /// <summary>
    ///     The endpoint to post fingerprint data to.
    ///     Default: "/bot-detection/fingerprint"
    /// </summary>
    [HtmlAttributeName("endpoint")]
    public string Endpoint { get; set; } = "/bot-detection/fingerprint";

    /// <summary>
    ///     Whether to defer script execution.
    ///     Default: true
    /// </summary>
    [HtmlAttributeName("defer")]
    public bool Defer { get; set; } = true;

    /// <summary>
    ///     Whether to use async script loading.
    ///     Default: false
    /// </summary>
    [HtmlAttributeName("async")]
    public bool Async { get; set; } = false;

    /// <summary>
    ///     Custom nonce for CSP compliance.
    ///     If set, adds nonce attribute to script tag.
    /// </summary>
    [HtmlAttributeName("nonce")]
    public string? Nonce { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!_options.ClientSide.Enabled)
        {
            output.SuppressOutput();
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            output.SuppressOutput();
            return;
        }

        // Generate a signed token to prevent spoofing
        var token = _tokenService.GenerateToken(httpContext);

        output.TagName = "script";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (Defer) output.Attributes.Add("defer", null);
        if (Async) output.Attributes.Add("async", null);
        if (!string.IsNullOrEmpty(Nonce)) output.Attributes.Add("nonce", Nonce);

        var script = GenerateScript(token);
        output.Content.SetHtmlContent(script);
    }

    private string GenerateScript(string token)
    {
        var opts = _options.ClientSide;

        return $@"(function(){{
'use strict';
var MLBotD={{
  v:'{BotDetectionScript.Version}',
  t:'{token}',
  e:'{Endpoint}',
  cfg:{{
    collectWebGL:{(opts.CollectWebGL ? "true" : "false")},
    collectCanvas:{(opts.CollectCanvas ? "true" : "false")},
    collectAudio:{(opts.CollectAudio ? "true" : "false")},
    timeout:{opts.CollectionTimeoutMs}
  }},
  h:function(s){{var h=0;for(var i=0;i<s.length;i++){{h=((h<<5)-h)+s.charCodeAt(i);h|=0;}}return h.toString(16);}},
  collect:function(){{
    var d={{}},n=navigator,w=window,s=screen;
    d.tz=Intl.DateTimeFormat().resolvedOptions().timeZone||'';
    d.lang=n.language||'';
    d.langs=(n.languages||[]).slice(0,3).join(',');
    d.platform=n.platform||'';
    d.cores=n.hardwareConcurrency||0;
    d.mem=n.deviceMemory||0;
    d.touch='ontouchstart' in w?1:0;
    d.screen=s.width+'x'+s.height+'x'+s.colorDepth;
    d.avail=s.availWidth+'x'+s.availHeight;
    d.dpr=w.devicePixelRatio||1;
    d.pdf=this.hasPdf()?1:0;
    d.webdriver=n.webdriver?1:0;
    d.phantom=w.phantom||w._phantom||w.callPhantom?1:0;
    d.nightmare=!!w.__nightmare;
    d.selenium=!!w.document.__selenium_unwrapped||!!w.document.__webdriver_evaluate||!!w.document.__driver_evaluate;
    d.cdc=this.hasCdc();
    d.plugins=n.plugins?n.plugins.length:0;
    d.chrome=!!w.chrome;
    d.permissions=this.checkPerms();
    d.outerW=w.outerWidth||0;
    d.outerH=w.outerHeight||0;
    d.innerW=w.innerWidth||0;
    d.innerH=w.innerHeight||0;
    d.evalLen=(function(){{try{{return eval.toString().length;}}catch(e){{return 0;}}}})();
    d.bindNative=Function.prototype.bind.toString().indexOf('[native code]')>-1?1:0;
    if(this.cfg.collectWebGL){{var gl=this.getWebGL();if(gl){{d.glVendor=gl.vendor||'';d.glRenderer=gl.renderer||'';}}}}
    if(this.cfg.collectCanvas){{d.canvasHash=this.getCanvasHash();}}
    d.score=this.calcScore(d);
    return d;
  }},
  hasPdf:function(){{try{{var p=navigator.plugins;for(var i=0;i<p.length;i++){{if(p[i].name.toLowerCase().indexOf('pdf')>-1)return true;}}}}catch(e){{}}return false;}},
  hasCdc:function(){{try{{for(var k in window){{if(k.match(/^cdc_|^__$|^\$cdc_/))return 1;}}}}catch(e){{}}return 0;}},
  checkPerms:function(){{try{{if(Notification&&Notification.permission==='denied'&&navigator.plugins.length===0)return 'suspicious';return Notification?Notification.permission:'unavailable';}}catch(e){{return 'error';}}}},
  getWebGL:function(){{try{{var c=document.createElement('canvas');var gl=c.getContext('webgl')||c.getContext('experimental-webgl');if(!gl)return null;var d=gl.getExtension('WEBGL_debug_renderer_info');return{{vendor:d?gl.getParameter(d.UNMASKED_VENDOR_WEBGL):'',renderer:d?gl.getParameter(d.UNMASKED_RENDERER_WEBGL):''}};}}catch(e){{return null;}}}},
  getCanvasHash:function(){{try{{var c=document.createElement('canvas');c.width=200;c.height=50;var ctx=c.getContext('2d');ctx.textBaseline='top';ctx.font='14px Arial';ctx.fillStyle='#f60';ctx.fillRect(125,1,62,20);ctx.fillStyle='#069';ctx.fillText('BotD',2,15);return this.h(c.toDataURL());}}catch(e){{return '';}}}},
  calcScore:function(d){{var score=100;if(d.webdriver)score-=50;if(d.phantom)score-=50;if(d.nightmare)score-=50;if(d.selenium)score-=50;if(d.cdc)score-=40;if(d.plugins===0&&d.chrome)score-=20;if(d.outerW===0||d.outerH===0)score-=30;if(d.innerW===d.outerW&&d.innerH===d.outerH)score-=10;if(!d.bindNative)score-=20;if(d.evalLen<30||d.evalLen>50)score-=15;if(d.permissions==='suspicious')score-=25;return Math.max(0,score);}},
  send:function(data){{var xhr=new XMLHttpRequest();xhr.open('POST',this.e,true);xhr.setRequestHeader('Content-Type','application/json');xhr.setRequestHeader('X-ML-BotD-Token',this.t);xhr.send(JSON.stringify(data));}},
  run:function(){{var self=this;setTimeout(function(){{try{{var d=self.collect();d.ts=Date.now();self.send(d);}}catch(e){{self.send({{error:e.message,ts:Date.now()}});}}}},100);}}
}};
if(document.readyState==='loading'){{document.addEventListener('DOMContentLoaded',function(){{MLBotD.run();}});}}else{{MLBotD.run();}}
}})();";
    }
}

/// <summary>
///     Script version and metadata.
/// </summary>
public static class BotDetectionScript
{
    public const string Version = "1.0.0";
}