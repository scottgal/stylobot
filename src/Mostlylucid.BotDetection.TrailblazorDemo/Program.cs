using Microsoft.AspNetCore.HttpOverrides;
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

// Adaptive sitemap. The sitemap content depends on the visitor's bot
// verdict from the in-process middleware. Search engines and humans get
// the full sitemap of public URLs. Uncertain visitors get the same minus
// the api endpoints. High-probability bad bots get a single honeypot URL
// that we log as bait. Every <url> is annotated with the detection
// verdict in an XML comment so the demo punchline is visible to anyone
// who curls the endpoint.
app.MapGet("/sitemap.xml", (HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value.TrimEnd('/')}";

    var evidence = ctx.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evObj)
        ? evObj as Mostlylucid.BotDetection.Orchestration.AggregatedEvidence
        : null;

    var probability = evidence?.BotProbability ?? 0d;
    var confidence = evidence?.Confidence ?? 0d;
    var riskBand = evidence?.RiskBand.ToString() ?? "Unknown";
    var isVerifiedCrawler = evidence?.Signals.TryGetValue("ua.is_verified_bot", out var vcObj) == true
                            && vcObj is bool vcBool && vcBool;

    string verdictLabel;
    string[] publicUrls;

    if (isVerifiedCrawler || probability < 0.4d)
    {
        verdictLabel = isVerifiedCrawler ? "verified-crawler" : "human";
        publicUrls = new[] { "/", "/Home/Signals", "/Home/Privacy" };
    }
    else if (probability >= 0.7d)
    {
        verdictLabel = "high-probability-bot";
        publicUrls = new[] { "/honeypot/admin" };
    }
    else
    {
        verdictLabel = "uncertain";
        publicUrls = new[] { "/", "/Home/Privacy" };
    }

    var sb = new System.Text.StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    sb.Append($"  <!-- stylobot verdict: {verdictLabel} (risk={riskBand}, probability={probability:F2}, confidence={confidence:F2}) -->\n");
    foreach (var path in publicUrls)
    {
        sb.Append($"  <url><loc>{baseUrl}{path}</loc></url>\n");
    }
    sb.Append("</urlset>\n");

    return Results.Content(sb.ToString(), "application/xml");
});

app.MapPost("/api/login", (LoginRequest req) => Results.Ok(new { ok = true, user = req.User }))
    .BotPolicy("strict", blockThreshold: 0.5);

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Built-in diagnostic endpoints (/bot-detection/check, /stats, /health).
// Useful from .http files and the talk's live-coded probes.
app.MapBotDetectionEndpoints();

// Required so <sb-live-updates> in the dashboard can connect.
app.MapHub<StyloBotDashboardHub>("/_stylobot/hub");

app.Run();

public record LoginRequest(string User, string Password);