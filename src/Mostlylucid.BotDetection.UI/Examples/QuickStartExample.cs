// Example: Minimal setup for Stylobot Dashboard
// Add this to your Program.cs

using Mostlylucid.BotDetection.UI.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add Stylobot Dashboard with authorization
builder.Services.AddStyloBotDashboard(
    authFilter: async (context) =>
    {
        // For development: allow localhost only
        var ip = context.Connection.RemoteIpAddress?.ToString();
        return ip == "127.0.0.1" || ip == "::1";

        // For production: require authentication
        // return context.User.IsInRole("Admin");
    },
    configure: options =>
    {
        options.BasePath = "/stylobot";
        options.EnableSimulator = true;          // Enable test data
        options.SimulatorEventsPerSecond = 2;    // 2 events/sec
        options.MaxEventsInMemory = 1000;
    });

var app = builder.Build();

// Enable the dashboard
app.UseStyloBotDashboard();

app.MapGet("/", () => "Navigate to /stylobot to view the dashboard");

app.Run();

/*
 * Alternative: Use with ASP.NET Core policy-based auth
 *
 * builder.Services.AddAuthorization(options =>
 * {
 *     options.AddPolicy("DashboardAccess", policy =>
 *         policy.RequireAuthenticatedUser()
 *               .RequireRole("Admin"));
 * });
 *
 * builder.Services.AddStyloBotDashboard(options =>
 * {
 *     options.RequireAuthorizationPolicy = "DashboardAccess";
 *     options.EnableSimulator = true;
 * });
 */
