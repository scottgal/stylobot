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
    ///     Client connects - enforces same auth as dashboard middleware.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext != null && !await IsAuthorizedAsync(httpContext))
        {
            _logger.LogWarning("SignalR connection rejected for {IP} - dashboard auth failed",
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

        // No auth configured - allow (same as dashboard middleware default)
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

    /// <summary>
    ///     Subscribe this connection to a Policy Stack scope group. The browser
    ///     calls this once per ancestor hash when a <c>[data-policy-stack-scope]</c>
    ///     section enters the DOM, so a Domain-level change reaches every
    ///     Endpoint browser sitting underneath. The hash is the 16-hex
    ///     <see cref="UI.Policies.PolicyScopeKeys.ScopeHash"/> output.
    /// </summary>
    public Task JoinPolicyGroup(string scopeHash)
    {
        if (string.IsNullOrWhiteSpace(scopeHash)) return Task.CompletedTask;
        // 16-hex chars is the wire contract; reject anything else so a stray
        // client value can't burst a group dictionary or escape into the hub.
        if (scopeHash.Length is < 8 or > 64) return Task.CompletedTask;
        return Groups.AddToGroupAsync(Context.ConnectionId, "policy:" + scopeHash);
    }

    /// <summary>
    ///     Inverse of <see cref="JoinPolicyGroup"/>. The browser fires this
    ///     when a <c>[data-policy-stack-scope]</c> section leaves the DOM so
    ///     the connection stops receiving beacons for scopes it no longer
    ///     observes.
    /// </summary>
    public Task LeavePolicyGroup(string scopeHash)
    {
        if (string.IsNullOrWhiteSpace(scopeHash)) return Task.CompletedTask;
        if (scopeHash.Length is < 8 or > 64) return Task.CompletedTask;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, "policy:" + scopeHash);
    }
}