using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Maps SignatureBehaviorState to BDF scenarios for replay testing.
///     This enables a closed-loop testing system where you can:
///     1. Capture real behavior from signatures
///     2. Generate synthetic BDF scenarios that mimic that behavior
///     3. Replay scenarios through the detection system
///     4. Verify classification remains consistent
/// </summary>
public sealed class SignatureToBdfMapper
{
    private readonly SignatureToBdfMapperOptions _options;

    public SignatureToBdfMapper(IOptions<SignatureToBdfMapperOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    ///     Maps a signature behavior state to a BDF scenario.
    /// </summary>
    /// <param name="state">Observed behavior metrics</param>
    /// <param name="profile">Expected behavior classification</param>
    /// <returns>BDF scenario that would generate similar behavior</returns>
    public BdfScenario Map(SignatureBehaviorState state, SignatureBehaviorProfile profile)
    {
        var timing = MapTiming(state);
        var navigation = MapNavigation(state);
        var errorInteraction = MapErrorInteraction(state);
        var expectation = MapExpectation(profile);

        var phase = new BdfPhase
        {
            Name = "auto-derived-main",
            Duration = null,
            RequestCount = MapPhaseRequestCount(state),
            Concurrency = 1, // Start simple
            BaseRateRps = timing.BaseRateRps,
            Timing = timing,
            Navigation = navigation,
            ErrorInteraction = errorInteraction,
            Content = new ContentConfig
            {
                BodyMode = "none"
            }
        };

        return new BdfScenario
        {
            Version = "1.0",
            Id = $"scenario-{profile.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            Description = $"Auto-derived scenario from SignatureBehaviorState (profile: {profile})",
            Metadata = new ScenarioMetadata
            {
                Author = "SignatureToBdfMapper",
                CreatedUtc = DateTime.UtcNow,
                Tags = new[] { "auto-derived", profile.ToString().ToLowerInvariant() }
            },
            Client = new ClientConfig
            {
                SignatureId = "derived-from-signature",
                UserAgent = "BDF-Replay/1.0"
            },
            Expectation = expectation,
            Phases = new[] { phase }
        };
    }

    /// <summary>
    ///     Maps behavioral metrics to timing configuration.
    /// </summary>
    private TimingConfig MapTiming(SignatureBehaviorState state)
    {
        var baseRps = Math.Clamp(state.AverageRps, 0.1, 10.0);

        // Bots with timer loops: strong spectral peak, low spectral entropy
        if (state.SpectralPeakToNoise >= _options.SpectralPnBot &&
            state.SpectralEntropy <= _options.SpectralEntropyBot)
            return new TimingConfig
            {
                Mode = "fixed",
                BaseRateRps = baseRps,
                JitterStdDevSeconds = 0.0,
                Burst = null
            };

        // Bursty signatures
        if (state.BurstScore >= _options.BurstScoreHigh)
            return new TimingConfig
            {
                Mode = "burst",
                BaseRateRps = Math.Clamp(state.AverageRps, 1.0, 20.0),
                JitterStdDevSeconds = 0.1,
                Burst = new BurstConfig
                {
                    BurstSize = 10, // Heuristic
                    BurstIntervalSeconds = 10.0 // Heuristic
                }
            };

        // Human-ish or jittered bots: jittered mode
        return new TimingConfig
        {
            Mode = "jittered",
            BaseRateRps = baseRps,
            JitterStdDevSeconds = MapJitterFromEntropy(state.TimingEntropy, state.CoefficientOfVariation)
        };
    }

    /// <summary>
    ///     Maps timing entropy and CV to jitter standard deviation.
    /// </summary>
    private double MapJitterFromEntropy(double timingEntropy, double cv)
    {
        // Low entropy/CV → small jitter (bot faking randomness)
        // High entropy/CV → large jitter (human-like)
        if (cv < _options.CoefficientOfVariationLow)
            return 0.1;

        if (cv > _options.CoefficientOfVariationHigh)
            return 0.6;

        // Linear interpolation between thresholds
        var ratio = (cv - _options.CoefficientOfVariationLow) /
                    (_options.CoefficientOfVariationHigh - _options.CoefficientOfVariationLow);

        return 0.1 + ratio * 0.5; // 0.1 to 0.6 seconds
    }

    /// <summary>
    ///     Maps behavioral metrics to navigation configuration.
    /// </summary>
    private NavigationConfig MapNavigation(SignatureBehaviorState state)
    {
        // Scanner / off-graph attacker
        if (state.PathEntropy >= _options.PathEntropyHigh &&
            state.NavAnomalyScore >= _options.NavAnomalyHigh)
            return new NavigationConfig
            {
                Mode = "scanner",
                StartPath = "/",
                OffGraphProbability = 0.8,
                Paths = new[]
                {
                    new PathTemplate { Template = "/wp-login.php", Weight = 1.0 },
                    new PathTemplate { Template = "/wp-admin/", Weight = 1.0 },
                    new PathTemplate { Template = "/.git/HEAD", Weight = 1.0 },
                    new PathTemplate { Template = "/phpmyadmin/", Weight = 1.0 },
                    new PathTemplate { Template = "/admin", Weight = 1.0 }
                }
            };

        // Sequential scraper (low path entropy, high nav anomaly)
        if (state.PathEntropy <= _options.PathEntropyLow &&
            state.NavAnomalyScore >= _options.NavAnomalyHigh)
            return new NavigationConfig
            {
                Mode = "sequential",
                StartPath = "/",
                OffGraphProbability = 0.6,
                Paths = new[]
                {
                    new PathTemplate
                    {
                        Template = "/items/{id}",
                        Weight = 1.0,
                        IdRange = new IdRange { Min = 1, Max = 1000 }
                    }
                }
            };

        // Normal-ish UI navigation
        if (state.AffordanceFollowThroughRatio >= _options.AffordanceHigh &&
            state.NavAnomalyScore < _options.NavAnomalyHigh)
            return new NavigationConfig
            {
                Mode = "ui_graph",
                StartPath = "/",
                OffGraphProbability = 0.05,
                UiGraphProfile = "default",
                Paths = new[]
                {
                    new PathTemplate { Template = "/", Weight = 1.0 },
                    new PathTemplate { Template = "/products", Weight = 2.0 },
                    new PathTemplate
                    {
                        Template = "/products/{id}",
                        Weight = 5.0,
                        IdRange = new IdRange { Min = 1, Max = 100 }
                    },
                    new PathTemplate { Template = "/cart", Weight = 1.0 }
                }
            };

        // Weird but not full-on scanner
        var offGraphProb = state.AffordanceFollowThroughRatio <= _options.AffordanceLow ? 0.5 : 0.2;

        return new NavigationConfig
        {
            Mode = "random",
            StartPath = "/",
            OffGraphProbability = offGraphProb,
            Paths = new[]
            {
                new PathTemplate { Template = "/", Weight = 1.0 },
                new PathTemplate { Template = "/about", Weight = 1.0 },
                new PathTemplate { Template = "/contact", Weight = 1.0 },
                new PathTemplate
                {
                    Template = "/page/{id}",
                    Weight = 3.0,
                    IdRange = new IdRange { Min = 1, Max = 50 }
                }
            }
        };
    }

    /// <summary>
    ///     Maps error ratios to error interaction configuration.
    /// </summary>
    private ErrorInteractionConfig MapErrorInteraction(SignatureBehaviorState state)
    {
        return new ErrorInteractionConfig
        {
            RetryOn4xx = state.FourOhFourRatio >= _options.FourOhFourRatioHigh,
            RetryOn5xx = state.FiveOhOhRatio >= _options.FiveOhOhRatioHigh,
            RespectRetryAfter = true,
            MaxRetries = state.FourOhFourRatio >= _options.FourOhFourRatioHigh ? 5 : 2,
            RetryDelay = "1s"
        };
    }

    /// <summary>
    ///     Maps behavior profile to expected detection outcome.
    /// </summary>
    private static ExpectationConfig MapExpectation(SignatureBehaviorProfile profile)
    {
        return profile switch
        {
            SignatureBehaviorProfile.ExpectedHuman => new ExpectationConfig
            {
                ExpectedClassification = "Human",
                MaxBotProbability = 0.3,
                MaxRiskBand = "Low"
            },
            SignatureBehaviorProfile.ExpectedBot => new ExpectationConfig
            {
                ExpectedClassification = "Bot",
                MinBotProbability = 0.8,
                MinRiskBand = "High"
            },
            SignatureBehaviorProfile.ExpectedMixed => new ExpectationConfig
            {
                ExpectedClassification = "Mixed",
                MaxBotProbability = 0.8,
                MinBotProbability = 0.2
            },
            _ => new ExpectationConfig
            {
                ExpectedClassification = "Unknown"
            }
        };
    }

    /// <summary>
    ///     Maps average session size to phase request count.
    /// </summary>
    private static int MapPhaseRequestCount(SignatureBehaviorState state)
    {
        // Use average session size as a rough request count
        return Math.Max(10, Math.Min(500, state.AverageRequestsPerSession));
    }
}