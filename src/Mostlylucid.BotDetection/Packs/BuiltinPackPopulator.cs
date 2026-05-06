using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Packs;

internal sealed class BuiltinPackPopulator(
    PackRegistry<IStylobotPreActionHook> preActionRegistry,
    PackRegistry<IStylobotPostResponseHook> postResponseRegistry,
    ReactionPackContext reactionPackContext,
    DegradationAtom degradationAtom) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        preActionRegistry.Add(reactionPackContext);
        postResponseRegistry.Add(degradationAtom);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
