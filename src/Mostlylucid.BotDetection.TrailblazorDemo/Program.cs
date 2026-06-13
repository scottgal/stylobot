using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Hubs;

var builder = WebApplication.CreateBuilder(args);

// The demo sits behind a Cloudflare tunnel that terminates TLS and
// forwards X-Forwarded-Proto/For/Host. Without ForwardedHeaders the
// Request.Scheme would always read "http" and the adaptive sitemap
// would emit http:// URLs. Clearing KnownNetworks/KnownProxies lets us
// trust the tunnel hop without enumerating the Docker bridge subnet.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// -------------------------------------------------------------------
// StyloBot: detection + dashboard, FOSS, middleware-mode (no gateway).
// -------------------------------------------------------------------
builder.Services.AddStyloBot(
    configureDashboard: dashboard =>
    {
        dashboard.BasePath = "/_stylobot";
        dashboard.AllowUnauthenticatedAccess = true; // dev only
    },
    configureDetection: detection =>
    {
        // Keep localhost requests visible while we demo.
        detection.ExcludeLocalIpFromBroadcast = false;

        // Enable the client-side fingerprint challenge so the in-process
        // detection pipeline can see navigator.webdriver / CDP / automation
        // signals and write headless.framework. Required for the
        // <sb-all-signals /> control on /Home/Signals to surface
        // "Puppeteer" / "Playwright" / "Selenium" / "PhantomJS" names.
        detection.ClientSide.Enabled = true;
    });

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// HTTPS redirect off for the demo; we run on http so chrome-devtools / curl
// don't trip on the self-signed cert. Re-enable in production.
// app.UseHttpsRedirection();

// Honour the tunnel's X-Forwarded-* headers BEFORE any middleware that
// reads Request.Scheme / Request.Host / RemoteIpAddress (the detection
// pipeline reads RemoteIpAddress, the sitemap reads Scheme + Host).
app.UseForwardedHeaders();

app.UseStaticFiles();
app.UseRouting();

// One call wires up:
//   1. BotDetectionBroadcastMiddleware   - records every request
//   2. BotDetectionMiddleware            - runs the 49 detectors
//   3. StyloBotDashboardMiddleware       - serves /_stylobot/*
app.UseStyloBot();

app.UseAuthorization();

// -----------------------------------------------------------------------
// Traffic shaping: minimal-API endpoints that demonstrate .BlockBots() and
// .BotPolicy() at the routing layer. These run BEFORE the action runs.
// -----------------------------------------------------------------------
app.MapGet("/api/data", () => Results.Ok(new { secret = "no bots allowed" }))
    .BlockBots();

// Adaptive sitemap, served by the FOSS BotDetection extension. The
// extension reads the visitor's detection evidence and switches between
// the configured public URLs, the uncertain subset, and the honeypot
// path. Verdict comment is on by default for the demo punchline.
app.MapStyloBotSitemap(configure: options =>
{
    options.PublicUrls = new List<string>
    {
        "/",
        "/Home/Signals",
        "/Home/Privacy"
    };
    options.UncertainUrls = new List<string> { "/", "/Home/Privacy" };
    options.HoneypotPath = "/honeypot/admin";
});

// Static-per-deployment robots.txt. References the sitemap above so a
// crawler that respects robots picks it up. Disallows the honeypot from
// well-behaved crawlers (badly-behaved ones ignore the directive, which
// is what makes the honeypot effective).
app.MapStyloBotRobotsTxt(configure: options =>
{
    options.HeaderComments = new List<string>
    {
        "stylobot middleware demo. https://github.com/scottgal/stylobot",
        "Adaptive sitemap content varies per visitor; see /sitemap.xml."
    };
    options.Rules = new List<RobotsRule>
    {
        new RobotsRule
        {
            UserAgent = "*",
            Allow = new List<string> { "/" },
            Disallow = new List<string> { "/honeypot/", "/api/" }
        }
    };
});

app.MapPost("/api/login", (LoginRequest req) => Results.Ok(new { ok = true, user = req.User }))
    .BotPolicy("strict", blockThreshold: 0.5);

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Built-in diagnostic endpoints (/bot-detection/check, /stats, /health).
// Useful from .http files and the talk's live-coded probes.
app.MapBotDetectionEndpoints();

// Serves the client-side fingerprint script at /bot-detection/script.js
// and the receive endpoint at /bot-detection/fingerprint. The
// <bot-detection-script /> tag helper in _Layout.cshtml loads the JS;
// the receive endpoint takes the browser's POSTed fingerprint blob
// (navigator.webdriver, CDP probes, automation flags, etc.) and feeds
// it into the detection pipeline so headless.framework lights up.
app.MapBotDetectionScript();
app.MapBotDetectionFingerprintEndpoint();

// Required so <sb-live-updates> in the dashboard can connect.
app.MapHub<StyloBotDashboardHub>("/_stylobot/hub");

app.Run();

public record LoginRequest(string User, string Password);