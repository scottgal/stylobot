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