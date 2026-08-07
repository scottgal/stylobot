using Microsoft.AspNetCore.SignalR;

namespace Mostlylucid.SignalShingle.AspNetCore;

/// <summary>Small dirty-beacon hub; values continue to travel via normal HTTP requests.</summary>
public sealed class SignalShingleHub : Hub
{
    public Task Join(string key) => Groups.AddToGroupAsync(Context.ConnectionId, key);
    public Task Leave(string key) => Groups.RemoveFromGroupAsync(Context.ConnectionId, key);
}

public interface ISignalShingleNotifier
{
    Task NotifyAsync(string key, long generation, CancellationToken cancellationToken = default);
    Task MarkDirtyAndNotifyAsync(string key, long generation, CancellationToken cancellationToken = default);
}

internal sealed class SignalShingleNotifier(
    ISignalShingleCache<string, string> cache,
    IHubContext<SignalShingleHub> hub) : ISignalShingleNotifier
{
    public Task NotifyAsync(string key, long generation, CancellationToken cancellationToken = default)
        => hub.Clients.Group(key).SendAsync("Dirty", key, generation, cancellationToken);

    public async Task MarkDirtyAndNotifyAsync(string key, long generation, CancellationToken cancellationToken = default)
    {
        if (cache.MarkDirty(key)) await NotifyAsync(key, generation, cancellationToken);
    }
}
