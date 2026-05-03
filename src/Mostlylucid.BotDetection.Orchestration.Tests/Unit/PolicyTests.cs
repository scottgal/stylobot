using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Tests for the policy system
/// </summary>
public class PolicyTests
{
    #region Helper Methods

    private static BlackboardState CreateState(double riskScore, Dictionary<string, object>? signals = null)
    {
        return new BlackboardState
        {
            HttpContext = null!,
            Signals = signals ?? new Dictionary<string, object>(),
            CurrentRiskScore = riskScore,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = [],
            RequestId = "test-request",
            Elapsed = TimeSpan.Zero
        };
    }

    #endregion

    #region DetectionPolicy Tests

    [Fact]
    public void DefaultPolicy_HasExpectedConfiguration()
    {
        var policy = DetectionPolicy.Default;

        Assert.Equal("default", policy.Name);
        Assert.Contains("UserAgent", policy.FastPathDetectors);
        Assert.Contains("Header", policy.FastPathDetectors);
        Assert.Contains("Ip", policy.FastPathDetectors);
        Assert.True(policy.UseFastPath);
        Assert.False(policy.ForceSlowPath);
        Assert.False(policy.EscalateToAi);
        Assert.Equal(0.95, policy.ImmediateBlockThreshold);
    }

    [Fact]
    public void StrictPolicy_HasStrongerThresholds()
    {
        var policy = DetectionPolicy.Strict;

        Assert.Equal("strict", policy.Name);
        Assert.True(policy.ForceSlowPath);
        Assert.True(policy.EscalateToAi);
        Assert.Equal(0.4, policy.AiEscalationThreshold);
        Assert.Equal(0.9, policy.ImmediateBlockThreshold);
        Assert.Contains("Behavioral", policy.WeightOverrides.Keys);
        Assert.Equal(2.0, policy.WeightOverrides["Behavioral"]);
    }

    [Fact]
    public void RelaxedPolicy_HasHigherTolerances()
    {
        var policy = DetectionPolicy.Relaxed;

        Assert.Equal("relaxed", policy.Name);
        Assert.Single(policy.FastPathDetectors);
        Assert.Contains("UserAgent", policy.FastPathDetectors);
        Assert.Empty(policy.SlowPathDetectors);
        Assert.Equal(0.5, policy.EarlyExitThreshold);
        Assert.Equal(0.99, policy.ImmediateBlockThreshold);
    }

    [Fact]
    public void AllowVerifiedBotsPolicy_HasTransitions()
    {
        var policy = DetectionPolicy.AllowVerifiedBots;

        Assert.Equal("allowVerifiedBots", policy.Name);
        Assert.Single(policy.Transitions);
        Assert.Equal("VerifiedGoodBot", policy.Transitions[0].WhenSignal);
        Assert.Equal(PolicyAction.Allow, policy.Transitions[0].Action);
    }

    #endregion

    #region PolicyTransition Tests

    [Fact]
    public void PolicyTransition_OnSignal_CreatesCorrectTransition()
    {
        var transition = PolicyTransition.OnSignal("TestSignal", "targetPolicy");

        Assert.Equal("TestSignal", transition.WhenSignal);
        Assert.Equal("targetPolicy", transition.GoToPolicy);
        Assert.Null(transition.Action);
    }

    [Fact]
    public void PolicyTransition_OnSignalWithAction_CreatesCorrectTransition()
    {
        var transition = PolicyTransition.OnSignal("VerifiedBot", PolicyAction.Allow);

        Assert.Equal("VerifiedBot", transition.WhenSignal);
        Assert.Equal(PolicyAction.Allow, transition.Action);
        Assert.Null(transition.GoToPolicy);
    }

    [Fact]
    public void PolicyTransition_OnHighRisk_CreatesCorrectTransition()
    {
        var transition = PolicyTransition.OnHighRisk(0.8, "strict");

        Assert.Equal(0.8, transition.WhenRiskExceeds);
        Assert.Equal("strict", transition.GoToPolicy);
    }

    [Fact]
    public void PolicyTransition_OnLowRisk_CreatesCorrectTransition()
    {
        var transition = PolicyTransition.OnLowRisk(0.2, "relaxed");

        Assert.Equal(0.2, transition.WhenRiskBelow);
        Assert.Equal("relaxed", transition.GoToPolicy);
    }

    #endregion

    #region PolicyRegistry Tests

    [Fact]
    public void PolicyRegistry_RegistersBuiltInPolicies()
    {
        var options = Options.Create(new BotDetectionOptions());
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        Assert.NotNull(registry.GetPolicy("default"));
        Assert.NotNull(registry.GetPolicy("strict"));
        Assert.NotNull(registry.GetPolicy("relaxed"));
        Assert.NotNull(registry.GetPolicy("allowVerifiedBots"));
    }

    [Fact]
    public void PolicyRegistry_GetPolicy_IsCaseInsensitive()
    {
        var options = Options.Create(new BotDetectionOptions());
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var policy1 = registry.GetPolicy("DEFAULT");
        var policy2 = registry.GetPolicy("Default");
        var policy3 = registry.GetPolicy("default");

        Assert.Equal(policy1, policy2);
        Assert.Equal(policy2, policy3);
    }

    [Fact]
    public void PolicyRegistry_GetPolicyForPath_ReturnsMatchingPolicy()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            UseFileExtensionStaticDetection = false, // Disable to test path-based matching only
            PathPolicies = new Dictionary<string, string>
            {
                ["/api/login"] = "strict",
                ["/static/*"] = "relaxed"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var loginPolicy = registry.GetPolicyForPath("/api/login");
        var staticPolicy = registry.GetPolicyForPath("/static/image.png");
        var otherPolicy = registry.GetPolicyForPath("/other");

        Assert.Equal("strict", loginPolicy.Name);
        Assert.Equal("relaxed", staticPolicy.Name);
        Assert.Equal("default", otherPolicy.Name); // Falls back to default
    }

    [Fact]
    public void PolicyRegistry_RegisterPolicy_AddsNewPolicy()
    {
        var options = Options.Create(new BotDetectionOptions());
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var customPolicy = new DetectionPolicy
        {
            Name = "custom",
            Description = "Custom test policy",
            FastPathDetectors = ["UserAgent"]
        };

        registry.RegisterPolicy(customPolicy);

        var retrieved = registry.GetPolicy("custom");
        Assert.NotNull(retrieved);
        Assert.Equal("Custom test policy", retrieved.Description);
    }

    [Fact]
    public void PolicyRegistry_RemovePolicy_RemovesPolicy()
    {
        var options = Options.Create(new BotDetectionOptions());
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var customPolicy = new DetectionPolicy
        {
            Name = "toRemove",
            FastPathDetectors = ["UserAgent"]
        };

        registry.RegisterPolicy(customPolicy);
        Assert.NotNull(registry.GetPolicy("toRemove"));

        var removed = registry.RemovePolicy("toRemove");

        Assert.True(removed);
        Assert.Null(registry.GetPolicy("toRemove"));
    }

    [Fact]
    public void PolicyRegistry_RemovePolicy_CannotRemoveDefault()
    {
        var options = Options.Create(new BotDetectionOptions());
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var removed = registry.RemovePolicy("default");

        Assert.False(removed);
        Assert.NotNull(registry.GetPolicy("default"));
    }

    [Fact]
    public void PolicyRegistry_LoadsCustomPoliciesFromOptions()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            Policies = new Dictionary<string, DetectionPolicyConfig>
            {
                ["customFromConfig"] = new()
                {
                    Description = "From config",
                    FastPath = ["UserAgent", "Header"],
                    EarlyExitThreshold = 0.25,
                    ImmediateBlockThreshold = 0.9
                }
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        var policy = registry.GetPolicy("customFromConfig");

        Assert.NotNull(policy);
        Assert.Equal("From config", policy.Description);
        Assert.Equal(0.25, policy.EarlyExitThreshold);
        Assert.Equal(0.9, policy.ImmediateBlockThreshold);
    }

    #endregion

    #region PolicyEvaluator Tests

    [Fact]
    public void PolicyEvaluator_Evaluate_ContinuesWhenNoTransitions()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"]
        };
        var state = CreateState(0.5);

        var result = evaluator.Evaluate(policy, state);

        Assert.True(result.ShouldContinue);
        Assert.Null(result.NextPolicy);
        Assert.Null(result.Action);
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_TriggersImmediateBlock()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            ImmediateBlockThreshold = 0.9,
            FastPathDetectors = ["UserAgent"]
        };
        var state = CreateState(0.95);

        var result = evaluator.Evaluate(policy, state);

        Assert.False(result.ShouldContinue);
        Assert.Equal(PolicyAction.Block, result.Action);
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_TriggersEarlyExit()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            UseFastPath = true,
            EarlyExitThreshold = 0.3,
            FastPathDetectors = ["UserAgent"]
        };
        var state = CreateState(0.1);

        var result = evaluator.Evaluate(policy, state);

        Assert.False(result.ShouldContinue);
        Assert.Equal(PolicyAction.Allow, result.Action);
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_TriggersSignalTransition()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"],
            Transitions =
            [
                PolicyTransition.OnSignal("VerifiedGoodBot", PolicyAction.Allow)
            ]
        };
        var state = CreateState(0.5, new Dictionary<string, object>
        {
            ["VerifiedGoodBot"] = true
        });

        var result = evaluator.Evaluate(policy, state);

        Assert.False(result.ShouldContinue);
        Assert.Equal(PolicyAction.Allow, result.Action);
    }

    [Fact]
    public void PolicyEvaluator_Evaluate_TriggersRiskTransition()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"],
            ImmediateBlockThreshold = 1.0, // Disable built-in block
            Transitions =
            [
                PolicyTransition.OnHighRisk(0.7, "strict")
            ]
        };
        var state = CreateState(0.8);

        var result = evaluator.Evaluate(policy, state);

        Assert.False(result.ShouldContinue);
        Assert.Equal("strict", result.NextPolicy);
    }

    [Fact]
    public void PolicyEvaluator_GetEffectiveWeight_UsesPolicyOverride()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"],
            WeightOverrides = new Dictionary<string, double>
            {
                ["UserAgent"] = 3.0
            }.ToImmutableDictionary()
        };

        var weight = evaluator.GetEffectiveWeight(policy, "UserAgent");

        Assert.Equal(3.0, weight);
    }

    [Fact]
    public void PolicyEvaluator_GetEffectiveWeight_FallsBackToGlobal()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"]
        };

        var weight = evaluator.GetEffectiveWeight(policy, "Heuristic");

        Assert.Equal(2.0, weight); // Global default for Heuristic
    }

    [Fact]
    public void PolicyEvaluator_GetEffectiveWeight_DefaultsToOne()
    {
        var evaluator = new PolicyEvaluator(NullLogger<PolicyEvaluator>.Instance);
        var policy = new DetectionPolicy
        {
            Name = "test",
            FastPathDetectors = ["UserAgent"]
        };

        var weight = evaluator.GetEffectiveWeight(policy, "UnknownDetector");

        Assert.Equal(1.0, weight);
    }

    #endregion

    #region PathPolicyMapping Tests

    [Fact]
    public void PathPolicyMapping_ExactMatch_Works()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            PathPolicies = new Dictionary<string, string>
            {
                ["/api/login"] = "strict"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        Assert.Equal("strict", registry.GetPolicyForPath("/api/login").Name);
        Assert.Equal("default", registry.GetPolicyForPath("/api/login/extra").Name);
    }

    [Fact]
    public void PathPolicyMapping_SingleWildcard_Works()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            UseFileExtensionStaticDetection = false, // Disable to test path-based matching only
            PathPolicies = new Dictionary<string, string>
            {
                ["/static/*"] = "relaxed"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        Assert.Equal("relaxed", registry.GetPolicyForPath("/static/image.png").Name);
        Assert.Equal("relaxed", registry.GetPolicyForPath("/static/css/style.css").Name);
    }

    [Fact]
    public void PathPolicyMapping_DoubleWildcard_Works()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            PathPolicies = new Dictionary<string, string>
            {
                ["/api/**"] = "strict"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        Assert.Equal("strict", registry.GetPolicyForPath("/api").Name);
        Assert.Equal("strict", registry.GetPolicyForPath("/api/users").Name);
        Assert.Equal("strict", registry.GetPolicyForPath("/api/users/123/posts").Name);
    }

    [Fact]
    public void PolicyRegistry_FileExtensionDetection_ReturnsStaticPolicy()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            UseFileExtensionStaticDetection = true, // Enable file extension detection (default)
            PathPolicies = new Dictionary<string, string>
            {
                ["/api/**"] = "strict"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        // File extension detection should take priority
        Assert.Equal("static", registry.GetPolicyForPath("/anywhere/image.png").Name);
        Assert.Equal("static", registry.GetPolicyForPath("/api/data/styles.css").Name);
        Assert.Equal("static", registry.GetPolicyForPath("/bundle.js").Name);
        Assert.Equal("static", registry.GetPolicyForPath("/fonts/arial.woff2").Name);
        Assert.Equal("static", registry.GetPolicyForPath("/img/logo.svg").Name);

        // Query strings should be ignored
        Assert.Equal("static", registry.GetPolicyForPath("/image.png?v=123").Name);

        // Non-static files should use path matching or default
        Assert.Equal("strict", registry.GetPolicyForPath("/api/users").Name);
        Assert.Equal("default", registry.GetPolicyForPath("/page.html").Name);
    }

    [Fact]
    public void PolicyRegistry_CustomStaticExtensions_Works()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            UseFileExtensionStaticDetection = true,
            StaticAssetExtensions = new List<string> { ".custom", ".xyz" }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        // Default extensions still work
        Assert.Equal("static", registry.GetPolicyForPath("/file.png").Name);

        // Custom extensions work
        Assert.Equal("static", registry.GetPolicyForPath("/file.custom").Name);
        Assert.Equal("static", registry.GetPolicyForPath("/file.xyz").Name);
    }

    [Fact]
    public void PathPolicyMapping_MoreSpecificMatchesFirst()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            PathPolicies = new Dictionary<string, string>
            {
                ["/api/*"] = "default",
                ["/api/login"] = "strict"
            }
        });
        var registry = new PolicyRegistry(
            NullLogger<PolicyRegistry>.Instance,
            options);

        // Exact match should win over wildcard
        Assert.Equal("strict", registry.GetPolicyForPath("/api/login").Name);
        Assert.Equal("default", registry.GetPolicyForPath("/api/users").Name);
    }

    #endregion
}