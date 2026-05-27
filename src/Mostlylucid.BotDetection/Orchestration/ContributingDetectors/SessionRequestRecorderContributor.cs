using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Priority-2 SessionStore writer. Splits the "record this request"
///     concern out of <see cref="SessionVectorContributor"/> (priority 30)
///     so the SessionStore ring populates on every detection regardless
///     of whether the orchestrator quorum-exits at priority 3 for clear
///     humans. Without this, <see cref="Mostlylucid.BotDetection.Services.SessionAtomizerService"/>
///     finds no live sessions to finalise on its 2-minute tick, the
///     dashboard's per-signature <c>LatestSessionVector</c> stays null,
///     and the home-card radar calibrates indefinitely.
///     <para>
///     Owns every signal derived from the act of recording: request
///     counts, current state, completion boundaries. Analysis lives in
///     SessionVectorContributor at priority 30, which only READS from
///     the now-populated store.
///     </para>
/// </summary>
public sealed class SessionRequestRecorderContributor : ConfiguredContributorBase
{
    private readonly ILogger<SessionRequestRecorderContributor> _logger;
    private readonly SessionStore _sessionStore;

    public SessionRequestRecorderContributor(
        ILogger<SessionRequestRecorderContributor> logger,
        IDetectorConfigProvider configProvider,
        SessionStore sessionStore)
        : base(configProvider)
    {
        _logger = logger;
        _sessionStore = sessionStore;
    }

    public override string Name => "SessionRequestRecorder";
    public override int Priority => Manifest?.Priority ?? 2;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Signatures evolve. SignatureContributor writes a raw hash at
        // priority 1; FingerprintMatchContributor refines it at priority 6
        // when fingerprint matching catches a known identity; background
        // slow-path learning can rewrite it between requests. We record
        // under whichever signature exists AT THIS POINT (priority 2). On
        // early-exit paths (priority-3 FastPathReputation) that means the
        // raw signature; on full-pipeline paths a later contributor may
        // refine the same actor's signature, so subsequent requests for
        // that actor will record under the refined value. Same semantics
        // SessionVectorContributor had when it was the only writer at
        // priority 30 -- this is a write-site move, not a behaviour change.
        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return [NeutralContribution("SessionRequestRecorder", "No signature to record")];

        try
        {
            var requestState = RequestMarkovClassifier.Classify(state);
            var statusCode = state.HttpContext.Response.StatusCode;
            var path = SessionVectorContributor.TemplatizePath(state.HttpContext.Request.Path.Value ?? "/");

            var sessionRequest = new SessionRequest(
                requestState,
                DateTimeOffset.UtcNow,
                path,
                statusCode > 0 ? statusCode : 200);

            var fpContext = SessionVectorContributor.BuildFingerprintContext(state);

            // Push to the ring buffer. Returns a completed-session snapshot
            // when a retrogressive boundary just closed -- consumed below to
            // emit the boundary signals every downstream wave reads.
            var completedSession = await _sessionStore.RecordRequestAsync(signature, sessionRequest, fpContext);

            // First-request of a fresh session: stash the per-session header
            // hashes for progressive identity (same logic SessionVectorContributor
            // used to perform).
            var currentSession = _sessionStore.GetCurrentSession(signature);
            if (currentSession is { Count: 1 })
            {
                var headerHashesJson = state.GetSignal<string>(SignalKeys.HeaderHashes);
                if (!string.IsNullOrEmpty(headerHashesJson))
                    _sessionStore.SetHeaderHashes(signature, headerHashesJson);
            }

            var sessionHistory = _sessionStore.GetHistory(signature);

            state.WriteSignals([
                new(SignalKeys.SessionRequestCount, currentSession?.Count ?? 1),
                new(SignalKeys.SessionHistoryCount, sessionHistory.Count),
                new(SignalKeys.SessionCurrentState, requestState.ToString())
            ]);

            if (completedSession != null)
            {
                state.WriteSignals([
                    new(SignalKeys.SessionBoundaryDetected, true),
                    new(SignalKeys.SessionCompletedMaturity, completedSession.Maturity),
                    new(SignalKeys.SessionCompletedRequestCount, completedSession.RequestCount),
                    new(SignalKeys.SessionDominantState, completedSession.DominantState.ToString())
                ]);

                _logger.LogDebug(
                    "Session boundary detected for {Signature}: {Count} requests, maturity={Maturity:F2}",
                    signature, completedSession.RequestCount, completedSession.Maturity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record session request for {Signature}", signature);
        }

        return [NeutralContribution("SessionRequestRecorder", "Request recorded to session ring")];
    }
}
