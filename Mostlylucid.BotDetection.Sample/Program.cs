using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Sample.Models;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------
// StyloBot: single call registers detection + real-time dashboard.
// Remove AllowUnauthenticatedAccess before going to production.
// ---------------------------------------------------------------
builder.Services.AddStyloBot(
    configureDashboard: dashboard =>
    {
        dashboard.BasePath = "/_stylobot";
        dashboard.AllowUnauthenticatedAccess = true;
    },
    configureDetection: detection =>
    {
        // Keep local traffic visible in the dashboard while developing
        detection.ExcludeLocalIpFromBroadcast = false;
    });

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

// Bogus-generated product catalog injected as a singleton
builder.Services.AddSingleton(StoreSeeder.Create());

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// ---------------------------------------------------------------
// UseStyloBot() wires up:
//   1. BotDetectionBroadcastMiddleware  - records every request
//   2. BotDetectionMiddleware           - runs all 49 detectors
//   3. StyloBotDashboardMiddleware      - serves /_stylobot/*
// Order is guaranteed; do not call UseBotDetection() separately.
// ---------------------------------------------------------------
app.UseStyloBot();

app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Explicitly map the SignalR hub so <sb-live-updates> can connect.
// UseStyloBot() attempts this via IEndpointRouteBuilder cast, but the explicit
// call here guarantees the hub is registered regardless of middleware ordering.
app.MapHub<StyloBotDashboardHub>("/_stylobot/hub");

app.Run();
