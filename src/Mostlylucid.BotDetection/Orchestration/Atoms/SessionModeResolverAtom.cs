using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Surfaces the fingerprint's ESTABLISHED interaction mode as a distinct signal so downstream
///     behavioral analysis can treat repetition as mode-relative. Runs in the [6, 20) priority gap:
///     after <c>FingerprintMatchAtom</c> (6) so identity is resolved, before <c>BehavioralAtom</c>
///     (20) so the hint is available where repetition is scored.
///
///     <para>
///         Reads the prior-persisted session for this conversation from the in-memory
///         <see cref="SessionStore"/> LFU, keyed on <c>PrimarySignature</c> (a stable per-conversation
///         identity, NEVER the spoofable peer IP -- an IP-keyed streaming latch behind an edge would
///         be a cross-client bypass). Dict-first lookback read; the DB is only touched on the ~1%
///         cold path. If any request in the current session, or any prior session snapshot's dominant
///         state, is a streaming state ({WebSocket, SignalR, ServerSentEvent}), the conversation is
///         established-streaming and repetition (low path-entropy, A→A, burst) is the EXPECTED
///         baseline, not bot-evidence.
///     </para>
///     <para>
///         This is an inference across requests, deliberately DISTINCT from
///         <see cref="SignalKeys.TransportIsStreaming"/> (this-request transport truth) -- no signal
///         conflation. It does NOT itself suppress anything; BehavioralAtom applies the deference
///         under a mode-consistency gate so it can never become a once-streamed-always-suppressed
///         latch.
///     </para>
/// </summary>
public sealed class SessionModeResolverAtom : DetectorAtomBase
{
    private readonly SessionStore _sessionStore;

    public SessionModeResolverAtom(SessionStore sessionStore)
        : base(name: "SessionModeResolver", category: "Session")
    {
        _sessionStore = sessionStore;
    }

    public override int Priority => 15;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink, string sessionId, CancellationToken ct = default)
    {
        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature)) return Task.FromResult(None());

        if (IsEstablishedStreaming(signature))
            sink.Raise($"{SignalKeys.SessionEstablishedStreaming}:true", sessionId);

        return Task.FromResult(None());
    }

    private bool IsEstablishedStreaming(string signature)
    {
        // In-progress session (this conversation's requests so far).
        var current = _sessionStore.GetCurrentSession(signature);
        if (current is not null)
            foreach (var r in current)
                if (IsStreaming(r.State))
                    return true;

        // Prior completed sessions for the same conversation.
        foreach (var snapshot in _sessionStore.GetHistory(signature))
            if (IsStreaming(snapshot.DominantState))
                return true;

        return false;
    }

    private static bool IsStreaming(RequestState state) =>
        state is RequestState.WebSocket or RequestState.SignalR or RequestState.ServerSentEvent;
}
