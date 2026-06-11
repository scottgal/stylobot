using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry.Prometheus;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     Viewer-host implementation of <see cref="IMeterStream" />. Polls the
///     gateway's <c>/metrics</c> endpoint on a cadence and materialises the same
///     LFU sliding cache of <see cref="MeterSummaryAtom" />s that
///     <see cref="LocalMeterStream" /> builds from a local
///     <see cref="System.Diagnostics.Metrics.MeterListener" />. Dashboard widgets
///     see identical <see cref="MeterTimeSeries" /> regardless of mode.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a remote stream:</b> per the gateway-local data architecture,
///         only the gateway holds the live <c>MeterListener</c>; viewer hosts
///         (the marketing site, fleet UI, etc.) MUST NOT subscribe to a local
///         listener for gateway-owned meters. Instead they scrape the Prometheus
///         endpoint over the LAN and let the same summary-atom storage shape
///         feed the same dashboard view components.
///     </para>
///     <para>
///         <b>Inert mode:</b> when <see cref="RemoteMeterStreamOptions.BaseUrl" />
///         is empty the stream skips scheduling the poller; <see cref="ListAsync" />
///         returns an empty list and <see cref="GetAsync" /> returns null. The
///         viewer host is sometimes booted in modes where remote metrics aren't
///         required; an inert stream lets DI register unconditionally.
///     </para>
///     <para>
///         <b>HTTP failure handling:</b> a non-2xx response or transport error
///         is logged at Debug and existing atoms are left intact. The next poll
///         retries. A flapping gateway shows last-known-good data instead of
///         going dark.
///     </para>
///     <para>
///         <b>Aggregation:</b> each scrape collapses a Prometheus family's
///         many label-permutation samples to a single per-meter value (counter:
///         cumulative; gauge: sum across labels; histogram / summary: <c>_sum</c>
///         when present) and feeds it into the atom via
///         <see cref="MeterSummaryAtom.Record" /> -- the SAME bucket / reservoir
///         path the local stream uses, no duplicated logic.
///     </para>
/// </remarks>
public sealed class RemoteMeterStream : IMeterStream, IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     Named <see cref="HttpClient" /> the stream prefers to resolve from
    ///     <see cref="IHttpClientFactory" />. Hosts that want custom handlers
    ///     (retry, circuit breaker, custom certs) register against this name.
    /// </summary>
    public const string HttpClientName = "stylobot-metrics";

    private readonly RemoteMeterStreamOptions _options;
    private readonly ILogger<RemoteMeterStream> _logger;
    private readonly MeterDescriptionRegistry? _descriptions;
    private readonly IMeterSignalSink _signalSink;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpMessageHandler? _injectedHandler;

    // Mirrors LocalMeterStream's storage shape -- same atom type, same dictionary
    // semantics. We adapt our options to a synthetic LocalMeterStreamOptions
    // purely to construct atoms; no behaviour duplicated.
    private readonly ConcurrentDictionary<string, MeterCatalogEntry> _catalog = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MeterSummaryAtom> _atoms = new(StringComparer.Ordinal);
    private readonly LocalMeterStreamOptions _atomOptions;
    private readonly object _evictionGate = new();

    private readonly IDisposable _pollSubscription;
    private readonly IDisposable _decaySubscription;
    // Test-path HttpClient: built once around the injected handler. Production
    // hosts MUST resolve through IHttpClientFactory -- the bare new HttpClient()
    // fallback that used to live here was a drift trap (no socket pool reuse,
    // no factory-managed handler lifetime).
    private HttpClient? _testHttpClient;
    private int _disposed;

    /// <summary>
    ///     Production constructor. <paramref name="httpClientFactory"/> is
    ///     required; production hosts wire it via <c>services.AddHttpClient</c>
    ///     in <see cref="PrometheusPackServiceCollectionExtensions.AddRemoteMeterStream"/>.
    ///     Wave 6 of the architectural-drift remediation removed the optional
    ///     fallback to <c>new HttpClient()</c> -- every host that exercises
    ///     this stream now goes through the factory's handler pool.
    /// </summary>
    public RemoteMeterStream(
        IOptions<RemoteMeterStreamOptions> options,
        ILogger<RemoteMeterStream> logger,
        IHttpClientFactory httpClientFactory,
        IMeterSignalSink? signalSink = null)
        : this(options, logger, signalSink, httpClientFactory, injectedHandler: null, coordinator: NullScheduleCoordinator.Instance)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
    }

    /// <summary>
    ///     Production constructor that also takes a <see cref="IScheduleCoordinator"/>.
    ///     Hooks the poll cadence (<see cref="RemoteMeterStreamOptions.PollCadence"/>)
    ///     and the <see cref="TickCadence.Tick1m"/> LFU sweep into
    ///     <paramref name="coordinator"/>. Wave 2 of the architectural-drift
    ///     remediation replaced the pre-migration <see cref="PeriodicTimer"/>
    ///     + <see cref="Timer"/> pair; Wave 6 made the factory non-optional.
    /// </summary>
    public RemoteMeterStream(
        IOptions<RemoteMeterStreamOptions> options,
        ILogger<RemoteMeterStream> logger,
        IMeterSignalSink? signalSink,
        IHttpClientFactory httpClientFactory,
        IScheduleCoordinator coordinator,
        MeterDescriptionRegistry? descriptions = null)
        : this(options, logger, signalSink, httpClientFactory, injectedHandler: null, coordinator: coordinator, descriptions: descriptions)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
    }

    /// <summary>
    ///     Test-only constructor that lets the test plug a canned
    ///     <see cref="HttpMessageHandler" /> in directly without standing up an
    ///     <see cref="IHttpClientFactory" />. Production code must not call
    ///     this -- the public ctors require the factory.
    /// </summary>
    internal RemoteMeterStream(
        IOptions<RemoteMeterStreamOptions> options,
        ILogger<RemoteMeterStream> logger,
        IMeterSignalSink? signalSink,
        IHttpClientFactory? httpClientFactory,
        HttpMessageHandler? injectedHandler)
        : this(options, logger, signalSink, httpClientFactory, injectedHandler, NullScheduleCoordinator.Instance)
    {
    }

    /// <summary>
    ///     Internal constructor used by every public + test entry point. Subscribes
    ///     the poll handler to <paramref name="coordinator"/> at
    ///     <see cref="RemoteMeterStreamOptions.PollCadence"/> and the eviction
    ///     handler at <see cref="TickCadence.Tick1m"/>. If
    ///     <see cref="RemoteMeterStreamOptions.BaseUrl"/> is empty the poll
    ///     handler is still wired but every fire is a no-op (matches the inert
    ///     mode contract).
    ///     <para>
    ///         Wave 6: this is the only ctor that accepts a nullable factory
    ///         because the test entry path (<paramref name="injectedHandler"/>
    ///         non-null) supplies its own transport. Public production ctors
    ///         enforce a non-null factory at their boundary.
    ///     </para>
    /// </summary>
    internal RemoteMeterStream(
        IOptions<RemoteMeterStreamOptions> options,
        ILogger<RemoteMeterStream> logger,
        IMeterSignalSink? signalSink,
        IHttpClientFactory? httpClientFactory,
        HttpMessageHandler? injectedHandler,
        IScheduleCoordinator coordinator,
        MeterDescriptionRegistry? descriptions = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _descriptions = descriptions;
        // Test path: either an IHttpClientFactory or a HttpMessageHandler MUST
        // be supplied -- a fully-null transport is a misconfiguration that
        // would have silently created a bare HttpClient in the pre-Wave-6
        // code path. Surface it as a constructor error so the host owner
        // either registers AddHttpClient or hands in a test handler.
        if (httpClientFactory is null && injectedHandler is null)
            throw new ArgumentException(
                "RemoteMeterStream requires either an IHttpClientFactory (production) " +
                "or an injected HttpMessageHandler (test). Both were null.",
                nameof(httpClientFactory));
        _options = options.Value;
        _logger = logger;
        _signalSink = signalSink ?? new NullMeterSignalSink();
        _httpClientFactory = httpClientFactory;
        _injectedHandler = injectedHandler;

        _atomOptions = new LocalMeterStreamOptions
        {
            MaxTrackedMeters = _options.MaxTrackedMeters,
            RecentBucketCount = _options.RecentBucketCount,
            BucketWidth = _options.BucketWidth,
            PercentileReservoirSize = _options.PercentileReservoirSize,
            HitCountDecayInterval = _options.HitCountDecayInterval,
            HitCountDecayFactor = _options.HitCountDecayFactor,
            MeterNamePrefixFilter = _options.MeterNamePrefixFilter,
            MinSignalEmitInterval = _options.MinSignalEmitInterval,
        };

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning(
                "RemoteMeterStream started with empty BaseUrl; stream is inert (no polling, empty catalog).");
        }
        else if (_options.PollTimeout >= _options.PollInterval)
        {
            _logger.LogWarning(
                "RemoteMeterStream PollTimeout ({Timeout}) >= PollInterval ({Interval}); polls may overlap.",
                _options.PollTimeout,
                _options.PollInterval);
        }

        _pollSubscription = coordinator.Subscribe(
            _options.PollCadence,
            "RemoteMeterStream.Poll",
            CostHint.Medium,
            OnPollTickAsync);

        _decaySubscription = coordinator.Subscribe(
            TickCadence.Tick1m,
            "RemoteMeterStream.Decay",
            CostHint.Low,
            (_, _) => { DecayAndEvict(); return Task.CompletedTask; });

        _logger.LogDebug(
            "RemoteMeterStream started (BaseUrl={BaseUrl}, PollCadence={Cadence}, PrefixFilter={Prefix})",
            _options.BaseUrl,
            _options.PollCadence,
            string.IsNullOrEmpty(_options.MeterNamePrefixFilter) ? "<all>" : _options.MeterNamePrefixFilter);
    }

    /// <summary>
    ///     Backwards-compatible no-op kept for test rigs that called
    ///     <c>StartAsync</c> on the pre-Wave-2 <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    ///     shape. The poll/decay subscriptions are wired in the constructor.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    ///     Backwards-compatible shim that calls <see cref="DisposeCore"/>.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return Task.CompletedTask;
    }

    private Task OnPollTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)) return Task.CompletedTask;
        return PollOnceAsync(ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
    {
        var snapshot = _atoms.Values.ToArray();
        Array.Sort(snapshot, static (a, b) => b.LastTouched.CompareTo(a.LastTouched));

        var result = new MeterCatalogEntry[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
            result[i] = snapshot[i].Catalog;

        return Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(result);
    }

    /// <inheritdoc />
    public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
    {
        if (buckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(buckets), buckets, "Bucket count must be positive.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");

        if (!_atoms.TryGetValue(meterName, out var atom))
            return Task.FromResult<MeterTimeSeries?>(null);

        var ts = atom.BuildTimeSeries(window, buckets, DateTimeOffset.UtcNow);
        return Task.FromResult<MeterTimeSeries?>(ts);
    }

    /// <summary>
    ///     Test hook: forces a single scrape on the calling thread without
    ///     waiting for the <see cref="PeriodicTimer" /> tick. Mirrors
    ///     <c>LocalMeterStream.PumpObservables</c> / <c>PumpEvictionForTesting</c>.
    /// </summary>
    internal void PumpPollForTesting()
    {
        // Drive one synchronous poll. Tests pass a cancelled token to fail
        // fast if the test handler ever blocks indefinitely.
        PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Test hook: forces a decay + eviction sweep on the calling thread.
    ///     Mirrors <c>LocalMeterStream.PumpEvictionForTesting</c>.
    /// </summary>
    internal void PumpEvictionForTesting()
    {
        DecayAndEvict();
    }

    // -------------------- poll handler --------------------

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return;

        var url = BuildMetricsUrl(_options.BaseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // ASP.NET's exporter responds to the standard OpenMetrics-flavoured
        // Accept; q=0.1 fallback keeps us happy with any text/plain body.
        request.Headers.Accept.ParseAdd("text/plain; version=0.0.4");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1d));

        if (!string.IsNullOrEmpty(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var client = ResolveHttpClient();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.PollTimeout);

        HttpResponseMessage? response = null;
        string body;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "RemoteMeterStream scrape returned non-success {Status} from {Url}; existing atoms preserved.",
                    (int)response.StatusCode, url);
                return;
            }
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("RemoteMeterStream scrape timed out hitting {Url}", url);
            return;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "RemoteMeterStream scrape transport failure hitting {Url}", url);
            return;
        }
        finally
        {
            response?.Dispose();
        }

        IReadOnlyList<PrometheusMetric> families;
        try
        {
            families = PrometheusTextParser.Parse(body);
        }
        catch (Exception ex)
        {
            // Parser is designed to never throw, but be defensive.
            _logger.LogDebug(ex, "RemoteMeterStream parser threw; body length={Len}", body.Length);
            return;
        }

        if (families.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var family in families)
        {
            if (!MeterNamePrefixFilterMatcher.Matches(family.Name, _options.MeterNamePrefixFilter))
            {
                continue;
            }

            IngestFamily(family, now);
        }
    }

    private static string BuildMetricsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed + "/metrics";
    }

    private HttpClient ResolveHttpClient()
    {
        // Production path: the factory is always non-null thanks to the
        // ctor-boundary guards. Hand back a factory-managed client.
        if (_httpClientFactory != null)
            return _httpClientFactory.CreateClient(HttpClientName);

        // Test path: build a one-shot client around the injected handler the
        // first time we're asked, then reuse it. The handler is owned by the
        // test rig (disposeHandler: false) so a stream dispose doesn't tear
        // down a fixture that other tests share.
        return _testHttpClient ??= new HttpClient(_injectedHandler!, disposeHandler: false);
    }

    private void IngestFamily(PrometheusMetric family, DateTimeOffset now)
    {
        var kind = MapKind(family.Kind);
        var desc = _descriptions?.TryGet(family.Name);
        var unit = desc?.Unit ?? (string.IsNullOrEmpty(family.Unit) ? null : family.Unit);
        var description = desc?.Description ?? (string.IsNullOrEmpty(family.Help) ? null : family.Help);

        // Catalog entry: same shape as LocalMeterStream.OnInstrumentPublished
        // builds. Keyed by family name (not the suffixed sample name).
        var entry = _catalog.GetOrAdd(family.Name, _ =>
            new MeterCatalogEntry(
                family.Name,
                ExtractNamespace(family.Name),
                kind,
                unit,
                description,
                desc?.Label));

        // If the previous catalog entry had a placeholder Untyped that's since
        // been declared, refresh it. Cheap because catalog is small.
        if (entry.Kind != kind || entry.Unit != unit || entry.Description != description)
        {
            entry = new MeterCatalogEntry(
                family.Name,
                ExtractNamespace(family.Name),
                kind,
                unit,
                description,
                desc?.Label);
            _catalog[family.Name] = entry;
        }

        var value = ReduceToScalar(family, kind);
        if (!value.HasValue) return;

        var atom = _atoms.GetOrAdd(family.Name, _ => new MeterSummaryAtom(entry, _atomOptions));

        MeterSignal? signal;
        if (kind == MeterKind.Counter || kind == MeterKind.UpDownCounter)
        {
            // Prometheus counters report CUMULATIVE values. The summary atom's
            // Counter path expects DELTAS (it adds them to its running
            // cumulative). Convert here.
            var delta = ComputeCounterDelta(family.Name, value.Value);
            signal = atom.Record(delta, now);
        }
        else
        {
            signal = atom.Record(value.Value, now);
        }

        if (signal is not null)
        {
            try
            {
                _signalSink.OnMeter(signal);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MeterSignalSink threw for {Name}", family.Name);
            }
        }
    }

    private readonly ConcurrentDictionary<string, double> _lastCumulative = new(StringComparer.Ordinal);

    private double ComputeCounterDelta(string name, double cumulative)
    {
        // First observation: take the cumulative as the initial delta (matches
        // a freshly-started counter that emitted these increments since boot).
        if (!_lastCumulative.TryGetValue(name, out var prev))
        {
            _lastCumulative[name] = cumulative;
            return cumulative;
        }

        var delta = cumulative - prev;
        _lastCumulative[name] = cumulative;

        // Counter reset (e.g. gateway restart): the standard Prometheus rule
        // is to treat a downward step as a reset and adopt the new value as
        // the delta. Otherwise we'd record a large negative spike.
        if (delta < 0d) return cumulative;
        return delta;
    }

    private static double? ReduceToScalar(PrometheusMetric family, MeterKind kind)
    {
        if (family.Samples.Count == 0) return null;

        switch (kind)
        {
            case MeterKind.Counter:
            case MeterKind.UpDownCounter:
            {
                // Sum every counter sample whose name is the family base
                // (or family + _total). Skips child series like _created.
                var sum = 0d;
                var matched = 0;
                foreach (var s in family.Samples)
                {
                    if (s.Name.Equals(family.Name, StringComparison.Ordinal)
                        || s.Name.Equals(family.Name + "_total", StringComparison.Ordinal))
                    {
                        sum += s.Value;
                        matched++;
                    }
                }
                if (matched == 0)
                {
                    // Fall back to summing whatever we have.
                    foreach (var s in family.Samples) sum += s.Value;
                }
                return sum;
            }
            case MeterKind.Gauge:
            {
                var sum = 0d;
                foreach (var s in family.Samples) sum += s.Value;
                return sum;
            }
            case MeterKind.Histogram:
            {
                // Prefer _sum for the per-scrape representative value. If
                // absent (summary with only quantiles), use quantile="0.5".
                foreach (var s in family.Samples)
                {
                    if (s.Name.EndsWith("_sum", StringComparison.Ordinal))
                        return s.Value;
                }
                foreach (var s in family.Samples)
                {
                    if (s.Labels.TryGetValue("quantile", out var q)
                        && (q == "0.5" || q == "0.50"))
                    {
                        return s.Value;
                    }
                }
                // Fall back to any sample.
                return family.Samples[0].Value;
            }
            default:
                return family.Samples[0].Value;
        }
    }

    private static MeterKind MapKind(PrometheusMetricKind kind) => kind switch
    {
        PrometheusMetricKind.Counter => MeterKind.Counter,
        PrometheusMetricKind.Gauge => MeterKind.Gauge,
        PrometheusMetricKind.Histogram => MeterKind.Histogram,
        PrometheusMetricKind.Summary => MeterKind.Histogram,
        _ => MeterKind.Gauge,
    };

    private static string ExtractNamespace(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var underscore = name.IndexOf('_');
        var dot = name.IndexOf('.');
        var idx = -1;
        if (underscore >= 0 && dot >= 0) idx = Math.Min(underscore, dot);
        else if (underscore >= 0) idx = underscore;
        else if (dot >= 0) idx = dot;
        return idx > 0 ? name[..idx] : name;
    }

    // -------------------- LFU sweep (mirrors LocalMeterStream) --------------------

    private void DecayAndEvict()
    {
        if (_disposed != 0) return;

        var factor = _options.HitCountDecayFactor;
        if (factor < 0d) factor = 0d;
        if (factor > 1d) factor = 1d;

        foreach (var atom in _atoms.Values)
            atom.DecayHitCount(factor);

        var max = _options.MaxTrackedMeters;
        if (max <= 0) max = 1;
        if (_atoms.Count <= max) return;

        lock (_evictionGate)
        {
            while (_atoms.Count > max)
            {
                string? coldestKey = null;
                double coldestHit = double.MaxValue;

                foreach (var pair in _atoms)
                {
                    var hit = pair.Value.SnapshotHitCount();
                    if (hit < coldestHit)
                    {
                        coldestHit = hit;
                        coldestKey = pair.Key;
                    }
                }

                if (coldestKey is null) break;
                _atoms.TryRemove(coldestKey, out _);
            }
        }
    }

    // -------------------- lifecycle --------------------

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _pollSubscription.Dispose(); }
        catch { /* swallow -- coordinator already torn down */ }

        try { _decaySubscription.Dispose(); }
        catch { /* swallow -- coordinator already torn down */ }

        try { _testHttpClient?.Dispose(); }
        catch { /* swallow */ }

        _testHttpClient = null;
        _atoms.Clear();
        _catalog.Clear();
        _lastCumulative.Clear();
    }

    public void Dispose() => DisposeCore();

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }
}
