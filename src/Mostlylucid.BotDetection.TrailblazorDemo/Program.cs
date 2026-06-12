using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Hubs;

var builder = WebApplication.CreateBuilder(args);

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

app.MapGet("/sitemap.xml", () => Results.Content(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
        "  <url><loc>http://localhost:5050/</loc></url>\n" +
        "</urlset>",
        "application/xml"))
    .BlockBots(allowVerifiedBots: true, allowSearchEngines: true);

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