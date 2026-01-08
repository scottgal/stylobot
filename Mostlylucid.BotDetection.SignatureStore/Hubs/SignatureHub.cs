using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.SignatureStore.Models;
using Mostlylucid.BotDetection.SignatureStore.Repositories;

namespace Mostlylucid.BotDetection.SignatureStore.Hubs;

/// <summary>
/// SignalR hub for streaming bot detection signatures in real-time.
/// Clients connect, receive initial top signatures, then receive incremental updates.
/// </summary>
public class SignatureHub : Hub
{
    private readonly ISignatureRepository _repository;
    private readonly ILogger<SignatureHub> _logger;

    public SignatureHub(
        ISignatureRepository repository,
        ILogger<SignatureHub> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Client calls this to get initial top signatures
    /// </summary>
    public async Task<List<SignatureQueryResult>> GetTopSignatures(int count = 100)
    {
        _logger.LogDebug("Client {ConnectionId} requested top {Count} signatures", Context.ConnectionId, count);

        return await _repository.GetTopByBotProbabilityAsync(count);
    }

    /// <summary>
    /// Client calls this to get recent signatures
    /// </summary>
    public async Task<List<SignatureQueryResult>> GetRecentSignatures(int count = 100)
    {
        _logger.LogDebug("Client {ConnectionId} requested recent {Count} signatures", Context.ConnectionId, count);

        return await _repository.GetRecentAsync(count);
    }

    /// <summary>
    /// Client calls this to subscribe to a specific filter
    /// (stored in per-connection state for targeted broadcasts)
    /// </summary>
    public async Task SubscribeToFilter(string? signalPath = null, object? signalValue = null)
    {
        var filter = new SignatureFilter
        {
            SignalPath = signalPath,
            SignalValue = signalValue
        };

        // Store filter in connection items for this client
        Context.Items["Filter"] = filter;

        _logger.LogInformation(
            "Client {ConnectionId} subscribed with filter: {SignalPath}={SignalValue}",
            Context.ConnectionId, signalPath, signalValue);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Client calls this to unsubscribe from filters (receive all updates)
    /// </summary>
    public async Task UnsubscribeFromFilter()
    {
        Context.Items.Remove("Filter");

        _logger.LogDebug("Client {ConnectionId} unsubscribed from filters", Context.ConnectionId);

        await Task.CompletedTask;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Client disconnected: {ConnectionId}, Exception: {Exception}",
            Context.ConnectionId, exception?.Message);

        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Filter for signature subscriptions
/// </summary>
public class SignatureFilter
{
    public string? SignalPath { get; set; }
    public object? SignalValue { get; set; }
}

/// <summary>
/// Service for broadcasting signature updates to SignalR clients.
/// Called by middleware when new signatures are stored.
/// </summary>
public interface ISignatureBroadcaster
{
    /// <summary>
    /// Broadcast a new signature to all connected clients
    /// </summary>
    Task BroadcastSignatureAsync(SignatureQueryResult signature);

    /// <summary>
    /// Broadcast multiple new signatures (batch)
    /// </summary>
    Task BroadcastSignaturesAsync(IEnumerable<SignatureQueryResult> signatures);
}

/// <summary>
/// Implementation of signature broadcaster using SignalR hub context
/// </summary>
public class SignatureBroadcaster : ISignatureBroadcaster
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

    public async Task BroadcastSignatureAsync(SignatureQueryResult signature)
    {
        try
        {
            // Broadcast to all connected clients
            await _hubContext.Clients.All.SendAsync("NewSignature", signature);

            _logger.LogDebug(
                "Broadcasted signature {SignatureId} with BotProbability={BotProb:F2}",
                signature.SignatureId, signature.BotProbability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast signature {SignatureId}", signature.SignatureId);
        }
    }

    public async Task BroadcastSignaturesAsync(IEnumerable<SignatureQueryResult> signatures)
    {
        try
        {
            var signatureList = signatures.ToList();

            if (signatureList.Count == 0)
                return;

            // Broadcast batch to all connected clients
            await _hubContext.Clients.All.SendAsync("NewSignatures", signatureList);

            _logger.LogDebug("Broadcasted {Count} signatures", signatureList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast signature batch");
        }
    }
}
