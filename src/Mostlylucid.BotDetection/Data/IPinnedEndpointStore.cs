namespace Mostlylucid.BotDetection.Data;

public sealed record PinnedEndpoint(
    long Id,
    string Method,
    string Path,
    bool IsHoneypot,
    string? Note,
    DateTimeOffset CreatedAt);

public interface IPinnedEndpointStore
{
    Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default);
    Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default);
    Task<bool> RemoveAsync(long id, CancellationToken ct = default);
}
