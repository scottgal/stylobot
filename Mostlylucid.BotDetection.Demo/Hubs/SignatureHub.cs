using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.Demo.Services;

namespace Mostlylucid.BotDetection.Demo.Hubs;

/// <summary>
///     SignalR hub for streaming bot detection signatures in real-time.
///     Clients can subscribe to receive new signatures as they're detected.
/// </summary>
public class SignatureHub : Hub
{
    private readonly ILogger<SignatureHub> _logger;
    private readonly SignatureStore _signatureStore;

    public SignatureHub(SignatureStore signatureStore, ILogger<SignatureHub> logger)
    {
        _signatureStore = signatureStore;
        _logger = logger;
    }

    /// <summary>
    ///     Client calls this to join the signature stream
    /// </summary>
    public async Task SubscribeToSignatures()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "SignatureSubscribers");

        _logger.LogInformation(
            "Client {ConnectionId} subscribed to signature stream",
            Context.ConnectionId);

        // Send recent signatures to new subscriber
        var recent = _signatureStore.GetRecentSignatures(20);
        await Clients.Caller.SendAsync("ReceiveRecentSignatures", recent);
    }

    /// <summary>
    ///     Client calls this to leave the signature stream
    /// </summary>
    public async Task UnsubscribeFromSignatures()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "SignatureSubscribers");

        _logger.LogInformation(
            "Client {ConnectionId} unsubscribed from signature stream",
            Context.ConnectionId);
    }

    /// <summary>
    ///     Get current statistics
    /// </summary>
    public SignatureStoreStats GetStats()
    {
        return _signatureStore.GetStats();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Client {ConnectionId} disconnected",
            Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
///     Service for broadcasting new signatures to all connected clients
/// </summary>
public class SignatureBroadcaster
{
    private readonly IHubContext<SignatureHub> _hubContext;
    private readonly ILogger<SignatureBroadcaster> _logger;

    public SignatureBroadcaster(
        IHubContext<SignatureHub> hubContext,
        ILogger<SignatureBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    ///     Broadcast a new signature to all subscribers
    /// </summary>
    public async Task BroadcastSignature(StoredSignature signature)
    {
        try
        {
            await _hubContext.Clients.Group("SignatureSubscribers")
                .SendAsync("ReceiveNewSignature", signature);

            _logger.LogTrace(
                "Broadcasted signature {SignatureId} to subscribers",
                signature.SignatureId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast signature {SignatureId}",
                signature.SignatureId);
        }
    }

    /// <summary>
    ///     Broadcast updated stats to all subscribers
    /// </summary>
    public async Task BroadcastStats(SignatureStoreStats stats)
    {
        try
        {
            await _hubContext.Clients.All
                .SendAsync("ReceiveStats", stats);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast stats");
        }
    }
}