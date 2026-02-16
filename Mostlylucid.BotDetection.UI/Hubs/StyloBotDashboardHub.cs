using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR hub for broadcasting real-time bot detection events to dashboard clients.
///     Enforces the same authorization rules as the dashboard middleware.
/// </summary>
public class StyloBotDashboardHub : Hub<IStyloBotDashboardHub>
{
    private readonly StyloBotDashboardOptions _options;
    private readonly ILogger<StyloBotDashboardHub> _logger;

    public StyloBotDashboardHub(
        StyloBotDashboardOptions options,
        ILogger<StyloBotDashboardHub> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Client connects — enforces same auth as dashboard middleware.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext != null && !await IsAuthorizedAsync(httpContext))
        {
            _logger.LogWarning("SignalR connection rejected for {IP} — dashboard auth failed",
                httpContext.Connection.RemoteIpAddress);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, "Dashboard");
        await base.OnConnectedAsync();
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        // Custom filter takes precedence
        if (_options.AuthorizationFilter != null)
            return await _options.AuthorizationFilter(context);

        // Policy-based auth
        if (!string.IsNullOrEmpty(_options.RequireAuthorizationPolicy))
        {
            var authService = context.RequestServices
                .GetService(typeof(Microsoft.AspNetCore.Authorization.IAuthorizationService))
                as Microsoft.AspNetCore.Authorization.IAuthorizationService;

            if (authService != null)
            {
                var result = await authService.AuthorizeAsync(
                    context.User, null, _options.RequireAuthorizationPolicy);
                return result.Succeeded;
            }
        }

        // No auth configured — allow (same as dashboard middleware default)
        return true;
    }

    /// <summary>
    ///     Client disconnects.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "Dashboard");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    ///     Client requests current summary statistics.
    /// </summary>
    public Task RequestSummary()
    {
        // Handled by DashboardSummaryBroadcaster
        return Task.CompletedTask;
    }
}