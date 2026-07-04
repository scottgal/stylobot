using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Migration adapter that wraps a legacy <see cref="IContributingDetector"/>
///     as an <see cref="IDetectorAtom"/> so the pack path can run
///     old + new detectors side-by-side while the conversion arc completes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Deleted with the last legacy contributor.</b> This adapter
///         exists solely to enable A/B testing of native atoms against their
///         legacy counterparts during the incremental migration. It is a
///         migration harness, not a production shape -- do not build new
///         detectors against it.
///     </para>
///     <para>
///         Per-request shared blackboard-shaped signal dictionary lives on
///         <see cref="HttpContext.Items"/> under
///         <see cref="SharedSignalsKey"/>. All adapter instances in the same
///         request share the same dictionary, matching the way
///         <c>BlackboardOrchestrator</c> already wires contributors. Native
///         atoms downstream see a best-effort Model-2 hint mirror of the dict
///         entries the adapter's wrapped contributor produced.
///     </para>
///     <para>
///         Type round-trip fidelity is best-effort:
///         <list type="bullet">
///             <item>
///                 Sink hint <c>"key:value"</c> lands in the dict as
///                 <see cref="string"/>. Legacy contributors calling
///                 <c>state.GetSignal&lt;bool&gt;(k)</c> against a bool signal
///                 that only reached us via a string hint will read
///                 <c>default</c>. Native atoms upstream that own that
///                 signal should write the typed value directly to the dict
///                 or expose an alternative retrieval path.
///             </item>
///             <item>
///                 Object payloads written by a contributor (e.g.
///                 <c>MultiFactorSignatures</c>) stay in the dict and are
///                 readable by other adapted contributors that follow.
///                 Downstream native atoms retrieve rich payloads via
///                 <see cref="HttpContext.Items"/>, not the sink -- so the
///                 adapter does not attempt to stringify object payloads
///                 back to the sink.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class ContributingDetectorAdapter : DetectorAtomBase
{
    /// <summary>
    ///     HttpContext.Items key for the per-request shared signals dictionary
    ///     that all adapters within one request read/write.
    /// </summary>
    public const string SharedSignalsKey = "stylobot.adapter.blackboard_signals";

    private readonly IContributingDetector _contributor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ContributingDetectorAdapter> _logger;

    public ContributingDetectorAdapter(
        IContributingDetector contributor,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ContributingDetectorAdapter> logger)
        : base(name: contributor.Name, category: contributor.Name)
    {
        _contributor = contributor;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public override int Priority => _contributor.Priority;
    public override bool IsEnabled => _contributor.IsEnabled;

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var signals = GetOrCreateSharedSignals(context);

        // Prime the shared dict with any hints the sink already carries,
        // ONE-WAY. Existing dict entries win -- an upstream adapted
        // contributor may already have written a typed object under a key
        // that a sink hint would silently overwrite as a string.
        HydrateFromSink(sink, signals);

        var beforeCount = signals.Count;
        var beforeKeys = beforeCount == 0
            ? Array.Empty<string>()
            : signals.Keys.ToArray();

        var state = new BlackboardState
        {
            HttpContext = context,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = new HashSet<string>(StringComparer.Ordinal),
            FailedDetectors = new HashSet<string>(StringComparer.Ordinal),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = sessionId
        };

        try
        {
            var contributions = await _contributor.ContributeAsync(state, ct).ConfigureAwait(false);

            // Mirror newly-added entries back to the sink so native atoms
            // downstream can see the contributor's output as Model-2 hints.
            ReplayNewEntriesToSink(sink, sessionId, signals, beforeKeys);

            return contributions ?? Array.Empty<DetectionContribution>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ContributingDetectorAdapter caught exception from wrapped contributor {Name}",
                _contributor.Name);
            return None();
        }
    }

    private static ConcurrentDictionary<string, object> GetOrCreateSharedSignals(HttpContext context)
    {
        if (context.Items[SharedSignalsKey] is ConcurrentDictionary<string, object> existing)
            return existing;

        var created = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        context.Items[SharedSignalsKey] = created;
        return created;
    }

    private static void HydrateFromSink(SignalSink sink, ConcurrentDictionary<string, object> dict)
    {
        var signals = sink.Sense(_ => true);
        for (var i = 0; i < signals.Count; i++)
        {
            var raw = signals[i].Signal;
            var colon = raw.IndexOf(':');

            if (colon <= 0)
            {
                dict.TryAdd(raw, true);
                continue;
            }

            var key = raw[..colon];
            var value = raw[(colon + 1)..];
            dict.TryAdd(key, value);
        }
    }

    private static void ReplayNewEntriesToSink(
        SignalSink sink,
        string sessionId,
        ConcurrentDictionary<string, object> dict,
        string[] beforeKeys)
    {
        var beforeSet = beforeKeys.Length == 0
            ? null
            : new HashSet<string>(beforeKeys, StringComparer.Ordinal);

        foreach (var (key, value) in dict)
        {
            if (beforeSet is not null && beforeSet.Contains(key)) continue;
            if (value is null) continue;

            // Rich object payloads (records / classes) stay on the dict --
            // stringifying them into the sink defeats the atom-holds-rich-
            // state rule. Emit only primitives / strings.
            var payload = value switch
            {
                bool b => b ? key : $"{key}:false",
                string s => $"{key}:{s}",
                double d => $"{key}:{d}",
                float f => $"{key}:{f}",
                int i => $"{key}:{i}",
                long l => $"{key}:{l}",
                _ => null
            };
            if (payload is null) continue;
            sink.Raise(payload, sessionId);
        }
    }
}

/// <summary>
///     DI helpers for opting into the migration adapter path.
/// </summary>
public static class ContributingDetectorAdapterExtensions
{
    /// <summary>
    ///     Walks every already-registered <see cref="IContributingDetector"/>
    ///     at container-build time and adds one
    ///     <see cref="ContributingDetectorAdapter"/> per contributor whose
    ///     <c>Name</c> is not already claimed by a native atom (detected via
    ///     <see cref="INativeAtomNameMarker"/> registrations). Contributors
    ///     that a native atom has migrated resolve to an inert placeholder
    ///     atom (IsEnabled = false) so the pack orchestrator drops them
    ///     during the enabled-atoms filter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Call this AFTER
    ///         <see cref="BotDetectionOrchestratorExtensions.AddNativeDetectorAtoms"/>
    ///         so the native atoms are the source of truth for their names.
    ///         The skip set is derived at DI-resolution time from the
    ///         <see cref="INativeAtomNameMarker"/> registrations that
    ///         <see cref="BotDetectionOrchestratorExtensions.AddDetectorAtom{TAtom}(IServiceCollection)"/>
    ///         adds alongside each native atom -- no hand-maintained list.
    ///     </para>
    ///     <para>
    ///         Adapter registrations are singleton -- the wrapped contributor
    ///         is already singleton in the existing DI wire-up, so re-using
    ///         the atom instance across requests is safe. Per-request state
    ///         lives on <see cref="HttpContext.Items"/>.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddContributingDetectorAdapters(
        this IServiceCollection services)
    {
        // Count the contributor descriptors at add time and register one
        // IDetectorAtom factory per index. Index-based mapping (instead of
        // ImplementationType) covers factory-registered contributors too --
        // ServiceDescriptor.ImplementationType is null for those, but
        // sp.GetServices<T>() still enumerates them in registration order,
        // which MS DI guarantees is stable.
        var contributorCount = services
            .Count(d => d.ServiceType == typeof(IContributingDetector));

        for (var i = 0; i < contributorCount; i++)
        {
            var index = i;
            services.AddSingleton<IDetectorAtom>(sp =>
            {
                var contributors = sp.GetServices<IContributingDetector>().ToList();
                if (index >= contributors.Count)
                    return NullAdapterAtom.Instance;
                var contributor = contributors[index];

                // Skip set derived at resolution time from the native atoms
                // that AddDetectorAtom<T>() registered a marker for. Distinct
                // service type so enumerating markers doesn't re-run atom
                // factories.
                var claimedNames = sp
                    .GetServices<INativeAtomNameMarker>()
                    .Select(m => m.AtomName)
                    .ToHashSet(StringComparer.Ordinal);
                if (claimedNames.Contains(contributor.Name))
                    return NullAdapterAtom.Instance;

                return new ContributingDetectorAdapter(
                    contributor,
                    sp.GetRequiredService<IHttpContextAccessor>(),
                    sp.GetRequiredService<ILogger<ContributingDetectorAdapter>>());
            });
        }

        return services;
    }

    /// <summary>
    ///     Inert placeholder atom returned when the skip set excludes a
    ///     wrapped contributor. IsEnabled = false so the pack orchestrator's
    ///     "atoms.Where(d => d.IsEnabled)" filter drops it.
    /// </summary>
    private sealed class NullAdapterAtom : IDetectorAtom
    {
        public static readonly NullAdapterAtom Instance = new();
        private NullAdapterAtom() { }
        public string Name => "__null_adapter__";
        public string Category => "__null_adapter__";
        public int Priority => int.MaxValue;
        public bool IsEnabled => false;
        public TimeSpan Timeout => TimeSpan.Zero;
        public bool IsOptional => true;
        public IReadOnlyList<string> RequiredSignals => Array.Empty<string>();
        public Task<IReadOnlyList<DetectionContribution>> DetectAsync(
            SignalSink sink, string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
    }
}
