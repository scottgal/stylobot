using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Hot-path benchmark for <c>SessionModeResolverAtom</c> (priority 15) added by the
///     session-mode-aware behavioral suppression (#91). The atom runs a per-request LFU read of
///     the session by PrimarySignature to decide whether the conversation is an established
///     streaming one, so its cost and per-request allocation matter for gateway stability under
///     load. This mirrors the atom's <c>IsEstablishedStreaming</c> exactly using the public
///     <see cref="SessionStore"/> API: <c>GetCurrentSession</c> + <c>GetHistory</c>, scanning for a
///     streaming Markov state (WebSocket / SignalR / ServerSentEvent).
///
///     Three shapes bound the cost envelope:
///     - Hit: streaming state is the FIRST request -> early return (best case).
///     - Miss (full walk): a long non-streaming session -> scans every request + snapshot (worst case).
///     - No session: unknown signature -> null current + empty history (cheapest).
///
///     Run: dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release -- --filter *SessionModeResolver*
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SessionModeResolverBenchmarks
{
    private SessionStore _store = null!;
    private const string StreamingSig = "sig-streaming";
    private const string BrowsingSig = "sig-browsing";
    private const string UnknownSig = "sig-unknown";

    [GlobalSetup]
    public void Setup()
    {
        _store = new SessionStore(new MemoryCache(new MemoryCacheOptions()), NullLogger<SessionStore>.Instance);

        // Hit: a SignalR conversation -- streaming state on the first request.
        _store.RecordRequestAsync(StreamingSig,
            new SessionRequest(RequestState.SignalR, DateTimeOffset.UtcNow, "/stylobot/hub", 200)).GetAwaiter().GetResult();
        for (var i = 0; i < 30; i++)
            _store.RecordRequestAsync(StreamingSig,
                new SessionRequest(RequestState.ApiCall, DateTimeOffset.UtcNow, "/stylobot/hub", 200)).GetAwaiter().GetResult();

        // Miss (full walk): a long content-browsing session, no streaming state anywhere.
        for (var i = 0; i < 50; i++)
            _store.RecordRequestAsync(BrowsingSig,
                new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, $"/page/{i}", 200)).GetAwaiter().GetResult();
    }

    // Mirrors SessionModeResolverAtom.IsEstablishedStreaming — the per-request LFU read the atom does.
    private bool IsEstablishedStreaming(string signature)
    {
        var current = _store.GetCurrentSession(signature);
        if (current is not null)
            foreach (var r in current)
                if (IsStreaming(r.State))
                    return true;
        foreach (var snapshot in _store.GetHistory(signature))
            if (IsStreaming(snapshot.DominantState))
                return true;
        return false;
    }

    private static bool IsStreaming(RequestState state) =>
        state is RequestState.WebSocket or RequestState.SignalR or RequestState.ServerSentEvent;

    [Benchmark(Baseline = true, Description = "Established-streaming hit (SignalR first)")]
    public bool Streaming_Hit() => IsEstablishedStreaming(StreamingSig);

    [Benchmark(Description = "Non-streaming full walk (50-request browsing session)")]
    public bool NonStreaming_FullWalk() => IsEstablishedStreaming(BrowsingSig);

    [Benchmark(Description = "No session (unknown signature)")]
    public bool NoSession_Miss() => IsEstablishedStreaming(UnknownSig);
}
