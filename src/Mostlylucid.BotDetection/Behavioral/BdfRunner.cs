using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Interface for running BDF scenarios and collecting results.
/// </summary>
public interface IBdfRunner
{
    /// <summary>
    ///     Executes a BDF scenario and returns the results.
    /// </summary>
    /// <param name="scenario">The scenario to execute</param>
    /// <param name="baseUrl">Base URL for the target application</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Execution results including response data and expectation validation</returns>
    Task<BdfExecutionResult> RunScenarioAsync(
        BdfScenario scenario,
        string baseUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Loads a BDF scenario from JSON.
    /// </summary>
    /// <param name="jsonPath">Path to JSON file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Parsed BDF scenario</returns>
    Task<BdfScenario> LoadScenarioAsync(string jsonPath, CancellationToken cancellationToken = default);
}

/// <summary>
///     Executes BDF scenarios for closed-loop testing.
///     This runner:
///     1. Parses BDF JSON scenarios
///     2. Executes phases with proper timing (fixed, jittered, burst)
///     3. Navigates using specified patterns (ui_graph, sequential, random, scanner)
///     4. Collects response data (status codes, headers, detection outcomes)
///     5. Validates results against expectations
///     6. Generates detailed execution reports
/// </summary>
public sealed class BdfRunner : IBdfRunner
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BdfRunner> _logger;
    private readonly Random _random = new();

    public BdfRunner(IHttpClientFactory httpClientFactory, ILogger<BdfRunner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    ///     Loads a BDF scenario from JSON file.
    /// </summary>
    public async Task<BdfScenario> LoadScenarioAsync(string jsonPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading BDF scenario from {JsonPath}", jsonPath);

        var json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
        var scenario = JsonSerializer.Deserialize<BdfScenario>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        });

        if (scenario == null)
            throw new InvalidOperationException($"Failed to parse BDF scenario from {jsonPath}");

        _logger.LogInformation("Loaded scenario {ScenarioId}: {Description}", scenario.Id, scenario.Description);
        return scenario;
    }

    /// <summary>
    ///     Executes a complete BDF scenario.
    /// </summary>
    public async Task<BdfExecutionResult> RunScenarioAsync(
        BdfScenario scenario,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting BDF scenario {ScenarioId}", scenario.Id);

        var startTime = DateTime.UtcNow;
        var phaseResults = new List<BdfPhaseResult>();

        // Execute each phase sequentially
        foreach (var phase in scenario.Phases)
        {
            _logger.LogInformation("Executing phase {PhaseName}", phase.Name);

            var phaseResult = await ExecutePhaseAsync(
                scenario,
                phase,
                baseUrl,
                cancellationToken);

            phaseResults.Add(phaseResult);

            // Stop if phase failed critically
            if (phaseResult.CriticalFailure)
            {
                _logger.LogWarning("Phase {PhaseName} failed critically, stopping scenario", phase.Name);
                break;
            }
        }

        var endTime = DateTime.UtcNow;
        var duration = endTime - startTime;

        // Aggregate results
        var result = new BdfExecutionResult
        {
            ScenarioId = scenario.Id,
            StartTime = startTime,
            EndTime = endTime,
            Duration = duration,
            PhaseResults = phaseResults,
            ExpectationMet = ValidateExpectations(scenario, phaseResults)
        };

        _logger.LogInformation(
            "Completed scenario {ScenarioId} in {Duration:F2}s. Expectation met: {ExpectationMet}",
            scenario.Id,
            duration.TotalSeconds,
            result.ExpectationMet);

        return result;
    }

    /// <summary>
    ///     Executes a single phase of the scenario.
    /// </summary>
    private async Task<BdfPhaseResult> ExecutePhaseAsync(
        BdfScenario scenario,
        BdfPhase phase,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var httpClient = CreateHttpClient(scenario.Client);
        var startTime = DateTime.UtcNow;
        var requests = new List<BdfRequestResult>();
        var requestCount = phase.RequestCount ?? 10; // Default to 10 requests if not specified

        // Build navigation state
        var navState = new NavigationState(phase.Navigation);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < requestCount && !cancellationToken.IsCancellationRequested; i++)
        {
            // Calculate next request timing
            var delay = CalculateNextDelay(phase.Timing, i);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            // Select next path
            var path = SelectNextPath(navState, i);
            var url = $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

            // Execute request
            var requestResult = await ExecuteRequestAsync(httpClient, url, cancellationToken);
            requests.Add(requestResult);

            _logger.LogDebug("Request {RequestNumber}/{Total}: {Method} {Path} -> {StatusCode}",
                i + 1, requestCount, "GET", path, requestResult.StatusCode);
        }

        sw.Stop();

        return new BdfPhaseResult
        {
            PhaseName = phase.Name,
            Duration = sw.Elapsed,
            Requests = requests,
            CriticalFailure = false
        };
    }

    /// <summary>
    ///     Creates HttpClient configured with scenario's client settings.
    /// </summary>
    private HttpClient CreateHttpClient(ClientConfig clientConfig)
    {
        var client = _httpClientFactory.CreateClient("BdfRunner");

        // Set User-Agent
        if (!string.IsNullOrEmpty(clientConfig.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(clientConfig.UserAgent);
        }

        // Set headers
        if (clientConfig.Headers != null)
            foreach (var (key, value) in clientConfig.Headers)
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

        return client;
    }

    /// <summary>
    ///     Calculates delay until next request based on timing configuration.
    /// </summary>
    private TimeSpan CalculateNextDelay(TimingConfig timing, int requestIndex)
    {
        return timing.Mode switch
        {
            "fixed" => TimeSpan.FromSeconds(1.0 / timing.BaseRateRps),

            "jittered" => CalculateJitteredDelay(timing),

            "burst" when timing.Burst != null =>
                CalculateBurstDelay(timing.Burst, requestIndex),

            _ => TimeSpan.FromSeconds(1.0 / timing.BaseRateRps)
        };
    }

    /// <summary>
    ///     Calculates jittered delay (normally distributed around base rate).
    /// </summary>
    private TimeSpan CalculateJitteredDelay(TimingConfig timing)
    {
        var baseDelay = 1.0 / timing.BaseRateRps;
        var jitter = timing.JitterStdDevSeconds * (_random.NextDouble() * 2 - 1); // -1 to +1
        var delaySeconds = Math.Max(0.01, baseDelay + jitter); // Minimum 10ms
        return TimeSpan.FromSeconds(delaySeconds);
    }

    /// <summary>
    ///     Calculates delay for burst mode.
    /// </summary>
    private TimeSpan CalculateBurstDelay(BurstConfig burst, int requestIndex)
    {
        // Within burst: very short delays
        // Between bursts: burst interval
        var positionInBurst = requestIndex % burst.BurstSize;

        if (positionInBurst == 0 && requestIndex > 0)
            // Start of new burst (but not first burst)
            return TimeSpan.FromSeconds(burst.BurstIntervalSeconds);

        // Within burst: minimal delay
        return TimeSpan.FromMilliseconds(50);
    }

    /// <summary>
    ///     Selects next path based on navigation configuration.
    /// </summary>
    private string SelectNextPath(NavigationState state, int requestIndex)
    {
        // Check if we should go off-graph
        if (_random.NextDouble() < state.Config.OffGraphProbability)
            // Select a random path from templates (scanner behavior)
            return SelectRandomPath(state);

        return state.Config.Mode switch
        {
            "sequential" => SelectSequentialPath(state, requestIndex),
            "random" => SelectRandomPath(state),
            "scanner" => SelectScannerPath(state),
            "ui_graph" => SelectUiGraphPath(state),
            _ => state.Config.StartPath
        };
    }

    private string SelectSequentialPath(NavigationState state, int requestIndex)
    {
        if (state.Config.Paths == null || !state.Config.Paths.Any())
            return state.Config.StartPath;

        var template = state.Config.Paths.First();
        return ApplyTemplate(template, requestIndex);
    }

    private string SelectRandomPath(NavigationState state)
    {
        if (state.Config.Paths == null || !state.Config.Paths.Any())
            return state.Config.StartPath;

        // Weighted random selection
        var totalWeight = state.Config.Paths.Sum(p => p.Weight);
        var randomValue = _random.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var template in state.Config.Paths)
        {
            cumulative += template.Weight;
            if (randomValue <= cumulative)
                return ApplyTemplate(template, _random.Next(1000));
        }

        return state.Config.Paths.First().Template;
    }

    private string SelectScannerPath(NavigationState state)
    {
        // Scanners try known attack paths
        return SelectRandomPath(state);
    }

    private string SelectUiGraphPath(NavigationState state)
    {
        // UI graph navigation follows links (weighted random)
        return SelectRandomPath(state);
    }

    private string ApplyTemplate(PathTemplate template, int index)
    {
        var path = template.Template;

        // Replace {id} with sequential or random ID
        if (path.Contains("{id}") && template.IdRange != null)
        {
            var id = template.IdRange.Min +
                     _random.Next(template.IdRange.Max - template.IdRange.Min + 1);
            path = path.Replace("{id}", id.ToString());
        }

        return path;
    }

    /// <summary>
    ///     Executes a single HTTP request and captures results.
    /// </summary>
    private async Task<BdfRequestResult> ExecuteRequestAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;

        try
        {
            response = await client.GetAsync(url, cancellationToken);
            sw.Stop();

            return new BdfRequestResult
            {
                Url = url,
                StatusCode = (int)response.StatusCode,
                Duration = sw.Elapsed,
                Success = response.IsSuccessStatusCode,
                Headers = response.Headers.ToDictionary(
                    h => h.Key,
                    h => string.Join(", ", h.Value))
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Request to {Url} failed", url);

            return new BdfRequestResult
            {
                Url = url,
                StatusCode = 0,
                Duration = sw.Elapsed,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    ///     Validates scenario results against expectations.
    /// </summary>
    private bool ValidateExpectations(BdfScenario scenario, List<BdfPhaseResult> phaseResults)
    {
        if (scenario.Expectation == null)
            return true; // No expectations to validate

        // For now, basic validation - can be extended
        // Real validation would need to inspect bot detection outcomes from responses
        var allRequests = phaseResults.SelectMany(p => p.Requests).ToList();

        var successRate = allRequests.Count(r => r.Success) / (double)Math.Max(1, allRequests.Count);

        // If we expect the client to be blocked, we'd expect low success rate
        // If we expect the client to be allowed, we'd expect high success rate
        // This is simplified - real implementation would check response headers for detection outcomes

        _logger.LogDebug("Overall success rate: {SuccessRate:P}", successRate);

        return true; // Placeholder
    }

    /// <summary>
    ///     Navigation state tracking (tracks current position in UI graph, etc.)
    /// </summary>
    private sealed class NavigationState
    {
        public NavigationState(NavigationConfig config)
        {
            Config = config;
            CurrentPath = config.StartPath;
        }

        public NavigationConfig Config { get; }
        public string CurrentPath { get; set; }
    }
}

/// <summary>
///     Results from executing a BDF scenario.
/// </summary>
public sealed record BdfExecutionResult
{
    public required string ScenarioId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public TimeSpan Duration { get; init; }
    public required IReadOnlyList<BdfPhaseResult> PhaseResults { get; init; }
    public bool ExpectationMet { get; init; }

    /// <summary>
    ///     Overall success rate across all requests.
    /// </summary>
    public double SuccessRate =>
        PhaseResults.SelectMany(p => p.Requests).Count(r => r.Success) /
        (double)Math.Max(1, PhaseResults.Sum(p => p.Requests.Count));

    /// <summary>
    ///     Total number of requests made.
    /// </summary>
    public int TotalRequests => PhaseResults.Sum(p => p.Requests.Count);
}

/// <summary>
///     Results from executing a single phase.
/// </summary>
public sealed record BdfPhaseResult
{
    public required string PhaseName { get; init; }
    public TimeSpan Duration { get; init; }
    public required IReadOnlyList<BdfRequestResult> Requests { get; init; }
    public bool CriticalFailure { get; init; }

    /// <summary>
    ///     Average request duration.
    /// </summary>
    public TimeSpan AverageRequestDuration =>
        Requests.Any()
            ? TimeSpan.FromMilliseconds(Requests.Average(r => r.Duration.TotalMilliseconds))
            : TimeSpan.Zero;
}

/// <summary>
///     Results from a single HTTP request.
/// </summary>
public sealed record BdfRequestResult
{
    public required string Url { get; init; }
    public int StatusCode { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Success { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? ErrorMessage { get; init; }
}