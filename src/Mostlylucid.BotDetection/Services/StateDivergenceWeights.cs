using System.Collections.Frozen;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Per-<see cref="RequestState"/> weights for divergence scoring.
///     Static assets are noise (browser side-effect): near-zero weight.
///     Auth / NotFound / Search are highly meaningful: high weight.
///     Loaded from YAML defaults via <see cref="FromParameters"/>.
/// </summary>
public sealed class StateDivergenceWeights
{
    public static readonly StateDivergenceWeights Default = new(new Dictionary<RequestState, double>
    {
        [RequestState.StaticAsset] = 0.05,
        [RequestState.PageView] = 0.10,
        [RequestState.ApiCall] = 0.25,
        [RequestState.SignalR] = 0.20,
        [RequestState.WebSocket] = 0.20,
        [RequestState.ServerSentEvent] = 0.20,
        [RequestState.FormSubmit] = 0.40,
        [RequestState.AuthAttempt] = 0.60,
        [RequestState.NotFound] = 0.50,
        [RequestState.Search] = 0.40,
    }.ToFrozenDictionary());

    private readonly FrozenDictionary<RequestState, double> _weights;

    private StateDivergenceWeights(FrozenDictionary<RequestState, double> weights)
        => _weights = weights;

    public double For(RequestState state)
        => _weights.TryGetValue(state, out var w) ? w : 0.25;

    /// <summary>
    ///     Build a weight set from a resolver callback. Resolver receives the state and a default fallback
    ///     (the value from <see cref="Default"/>) and returns the configured value.
    /// </summary>
    public static StateDivergenceWeights FromParameters(Func<RequestState, double, double> resolve)
    {
        var dict = new Dictionary<RequestState, double>(Default._weights.Count);
        foreach (var state in Enum.GetValues<RequestState>())
            dict[state] = resolve(state, Default.For(state));
        return new StateDivergenceWeights(dict.ToFrozenDictionary());
    }
}
