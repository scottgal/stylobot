using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests for <see cref="FingerprintPriorContributor" />.
///     Verifies that cached fingerprint verdict signals are converted into a
///     calibrated bias contribution with linear age decay.
/// </summary>
public class FingerprintPriorContributorTests
{
    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _parameters;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
            => _parameters = parameters ?? new Dictionary<string, object>();

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 0.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 0.0 },
            Confidence = new ConfidenceDefaults
                { BotDetected = 0.5, HumanIndicated = -0.5, Neutral = 0.0, StrongSignal = 0.8 },
            Parameters = new Dictionary<string, object>(_parameters)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_parameters.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(
            string detectorName, string parameterName, ConfigResolutionContext context,
            T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private static BlackboardState StateWith(params (string Key, object Value)[] signals)
    {
        var dict = new ConcurrentDictionary<string, object>(
            signals.ToDictionary(t => t.Key, t => t.Value));
        return new BlackboardState
        {
            HttpContext = new DefaultHttpContext(),
            Signals = dict,
            SignalWriter = dict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    private static FingerprintPriorContributor Build(Dictionary<string, object>? overrides = null)
        => new(NullLogger<FingerprintPriorContributor>.Instance, new StubConfigProvider(overrides));

    [Fact]
    public async Task NoPrior_ContributesNothing()
    {
        var c = Build();
        var result = await c.ContributeAsync(StateWith());
        Assert.Empty(result);
    }

    [Fact]
    public async Task HumanPrior_ContributesHumanBias()
    {
        var c = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.05),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  10.0));
        var result = await c.ContributeAsync(state);
        Assert.Single(result);
        Assert.True(result[0].ConfidenceDelta < 0,
            $"Human prior should produce negative confidence delta, got {result[0].ConfidenceDelta}");
    }

    [Fact]
    public async Task BotPrior_ContributesBotBias()
    {
        var c = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.92),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  10.0));
        var result = await c.ContributeAsync(state);
        Assert.Single(result);
        Assert.True(result[0].ConfidenceDelta > 0,
            $"Bot prior should produce positive confidence delta, got {result[0].ConfidenceDelta}");
    }

    [Fact]
    public async Task OldPrior_DecaysToZeroWeight()
    {
        var c = Build();
        var state = StateWith(
            (SignalKeys.FingerprintPriorProbability, 0.95),
            (SignalKeys.FingerprintPriorConfidence,  0.7),
            (SignalKeys.FingerprintPriorAgeSeconds,  86_400.0 * 10)); // 10x decay horizon
        var result = await c.ContributeAsync(state);
        if (result.Count > 0)
            Assert.Equal(0.0, result[0].Weight, precision: 3);
    }
}
