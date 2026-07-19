using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
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
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<StyloBotDashboardHub> _logger;

    public StyloBotDashboardHub(
        StyloBotDashboardOptions options,
        IWebHostEnvironment environment,
        ILogger<StyloBotDashboardHub> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    ///     Client connects - enforces same auth as dashboard middleware.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext != null && !await IsAuthorizedAsync(httpContext, _options, _environment))
        {
            _logger.LogWarning("SignalR connection rejected for {IP} - dashboard auth failed",
                httpContext.Connection.RemoteIpAddress);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, "Dashboard");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Matches the dashboard middleware's authorization decision so the hub
    /// cannot become an unauthenticated side door to dashboard updates.
    /// </summary>
    internal static async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        StyloBotDashboardOptions options,
        IWebHostEnvironment environment)
    {
        if (options.RequireAuthentication)
        {
            var authentication = context.RequestServices?.GetService(typeof(IAuthenticationService))
                as IAuthenticationService;
            if (authentication is null) return false;

            AuthenticateResult result;
            try
            {
                result = await authentication.AuthenticateAsync(context, scheme: null);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            if (result?.Principal != null)
                context.User = result.Principal;
            return context.User.Identity?.IsAuthenticated == true;
        }

        if (options.AuthorizationFilter != null)
            return await options.AuthorizationFilter(context);

        if (!string.IsNullOrEmpty(options.RequireAuthorizationPolicy))
        {
            var authService = context.RequestServices
                .GetService(typeof(IAuthorizationService)) as IAuthorizationService;

            if (authService != null)
            {
                var result = await authService.AuthorizeAsync(
                    context.User, null, options.RequireAuthorizationPolicy);
                return result.Succeeded;
            }

            return false;
        }

        if (options.AllowUnauthenticatedAccess) return true;
        return environment.IsDevelopment();
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
