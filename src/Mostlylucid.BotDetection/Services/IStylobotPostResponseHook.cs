namespace Mostlylucid.BotDetection.Services;

public interface IStylobotPostResponseHook
{
    ValueTask OnResponseCompletedAsync(ResponseContext context, CancellationToken ct);
}
