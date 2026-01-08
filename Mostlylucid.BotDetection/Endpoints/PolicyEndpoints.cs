using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Endpoints;

/// <summary>
///     Policy management and testing endpoints.
/// </summary>
public static class PolicyEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Maps policy management endpoints to the specified route prefix.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder</param>
    /// <param name="prefix">Route prefix (default: /bot-detection/policies)</param>
    /// <returns>The route group builder for further configuration</returns>
    public static RouteGroupBuilder MapBotPolicyEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bot-detection/policies")
    {
        var group = endpoints.MapGroup(prefix)
            .WithTags("Bot Detection Policies");

        // List all policies
        group.MapGet("/", ListPolicies)
            .WithName("ListPolicies")
            .WithSummary("List all registered policies")
            .Produces<PolicyListResponse>();

        // Get policy by name
        group.MapGet("/{name}", GetPolicy)
            .WithName("GetPolicy")
            .WithSummary("Get a specific policy by name")
            .Produces<PolicyDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        // Get policy for a path
        group.MapGet("/for-path", GetPolicyForPath)
            .WithName("GetPolicyForPath")
            .WithSummary("Get the policy that would be applied to a given path")
            .Produces<PolicyDetailResponse>();

        // Test a path against policies
        group.MapPost("/test", TestPolicy)
            .WithName("TestPolicy")
            .WithSummary("Test detection with a specific policy (dry run)")
            .Produces<PolicyTestResponse>();

        // Simulate policy evaluation
        group.MapPost("/simulate", SimulatePolicyEvaluation)
            .WithName("SimulatePolicyEvaluation")
            .WithSummary("Simulate policy transitions for given risk scores/signals")
            .Produces<PolicySimulationResponse>();

        // Register a new policy (runtime)
        group.MapPost("/", RegisterPolicy)
            .WithName("RegisterPolicy")
            .WithSummary("Register a new policy at runtime")
            .Produces<PolicyDetailResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        // Update an existing policy
        group.MapPut("/{name}", UpdatePolicy)
            .WithName("UpdatePolicy")
            .WithSummary("Update an existing policy")
            .Produces<PolicyDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        // Delete a policy
        group.MapDelete("/{name}", DeletePolicy)
            .WithName("DeletePolicy")
            .WithSummary("Delete a policy (cannot delete built-in policies)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        // Get default weights
        group.MapGet("/weights", GetDefaultWeights)
            .WithName("GetDefaultWeights")
            .WithSummary("Get default detector weights")
            .Produces<WeightsResponse>();

        return group;
    }

    #region Endpoint Handlers

    private static IResult ListPolicies(IPolicyRegistry registry)
    {
        var policies = registry.GetAllPolicies();
        var response = new PolicyListResponse
        {
            DefaultPolicy = registry.DefaultPolicy.Name,
            Policies = policies.Values.Select(ToPolicySummary).ToList()
        };
        return Results.Json(response, JsonOptions);
    }

    private static IResult GetPolicy(string name, IPolicyRegistry registry)
    {
        var policy = registry.GetPolicy(name);
        if (policy == null)
            return Results.NotFound(new { error = $"Policy '{name}' not found" });

        return Results.Json(ToDetailResponse(policy), JsonOptions);
    }

    private static IResult GetPolicyForPath(
        [FromQuery] string path,
        IPolicyRegistry registry)
    {
        var policy = registry.GetPolicyForPath(path);
        return Results.Json(new
        {
            requestedPath = path,
            policy = ToDetailResponse(policy)
        }, JsonOptions);
    }

    private static async Task<IResult> TestPolicy(
        HttpContext httpContext,
        [FromBody] PolicyTestRequest request,
        IPolicyRegistry registry,
        BlackboardOrchestrator orchestrator)
    {
        // Determine which policy to use
        DetectionPolicy policy;
        if (!string.IsNullOrEmpty(request.PolicyName))
            policy = registry.GetPolicy(request.PolicyName) ?? registry.DefaultPolicy;
        else if (!string.IsNullOrEmpty(request.Path))
            policy = registry.GetPolicyForPath(request.Path);
        else
            policy = registry.DefaultPolicy;

        // Run detection with the policy
        var result = await orchestrator.DetectWithPolicyAsync(httpContext, policy);

        return Results.Json(new PolicyTestResponse
        {
            PolicyUsed = policy.Name,
            BotProbability = result.BotProbability,
            Confidence = result.Confidence,
            RiskBand = result.RiskBand.ToString(),
            PolicyAction = result.PolicyAction?.ToString(),
            ContributingDetectors = result.ContributingDetectors.ToList(),
            FailedDetectors = result.FailedDetectors.ToList(),
            ProcessingTimeMs = result.TotalProcessingTimeMs,
            EarlyExit = result.EarlyExit,
            Contributions = result.Contributions.Select(c => new ContributionSummary
            {
                Detector = c.DetectorName,
                Category = c.Category,
                ConfidenceDelta = c.ConfidenceDelta,
                Weight = c.Weight,
                Reason = c.Reason
            }).ToList()
        }, JsonOptions);
    }

    private static IResult SimulatePolicyEvaluation(
        [FromBody] PolicySimulationRequest request,
        IPolicyRegistry registry,
        IPolicyEvaluator evaluator)
    {
        var policy = registry.GetPolicy(request.PolicyName);
        if (policy == null)
            return Results.NotFound(new { error = $"Policy '{request.PolicyName}' not found" });

        var results = new List<SimulationStep>();
        var currentPolicy = policy;

        foreach (var step in request.Steps)
        {
            var state = new BlackboardState
            {
                HttpContext = null!,
                Signals = step.Signals ?? new Dictionary<string, object>(),
                CurrentRiskScore = step.RiskScore,
                CompletedDetectors = step.CompletedDetectors?.ToHashSet() ?? new HashSet<string>(),
                FailedDetectors = new HashSet<string>(),
                Contributions = [],
                RequestId = "simulation",
                Elapsed = TimeSpan.Zero
            };

            var evalResult = evaluator.Evaluate(currentPolicy, state);

            results.Add(new SimulationStep
            {
                InputRiskScore = step.RiskScore,
                InputSignals = step.Signals,
                PolicyName = currentPolicy.Name,
                ShouldContinue = evalResult.ShouldContinue,
                NextPolicy = evalResult.NextPolicy,
                Action = evalResult.Action?.ToString(),
                Reason = evalResult.Reason
            });

            // Follow transition for next step
            if (!string.IsNullOrEmpty(evalResult.NextPolicy))
            {
                var next = registry.GetPolicy(evalResult.NextPolicy);
                if (next != null) currentPolicy = next;
            }

            // Stop if action taken
            if (evalResult.Action.HasValue)
                break;
        }

        return Results.Json(new PolicySimulationResponse
        {
            InitialPolicy = policy.Name,
            Steps = results
        }, JsonOptions);
    }

    private static IResult RegisterPolicy(
        [FromBody] PolicyCreateRequest request,
        IPolicyRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Policy name is required" });

        if (registry.GetPolicy(request.Name) != null)
            return Results.BadRequest(new { error = $"Policy '{request.Name}' already exists" });

        var policy = new DetectionPolicy
        {
            Name = request.Name,
            Description = request.Description,
            FastPathDetectors = [.. request.FastPath ?? []],
            SlowPathDetectors = [.. request.SlowPath ?? []],
            AiPathDetectors = [.. request.AiPath ?? []],
            UseFastPath = request.UseFastPath ?? true,
            ForceSlowPath = request.ForceSlowPath ?? false,
            EscalateToAi = request.EscalateToAi ?? false,
            AiEscalationThreshold = request.AiEscalationThreshold ?? 0.6,
            EarlyExitThreshold = request.EarlyExitThreshold ?? 0.3,
            ImmediateBlockThreshold = request.ImmediateBlockThreshold ?? 0.95,
            WeightOverrides = (request.Weights ?? new Dictionary<string, double>()).ToImmutableDictionary(),
            Timeout = TimeSpan.FromMilliseconds(request.TimeoutMs ?? 5000),
            Enabled = request.Enabled ?? true
        };

        registry.RegisterPolicy(policy);

        return Results.Created($"/bot-detection/policies/{policy.Name}", ToDetailResponse(policy));
    }

    private static IResult UpdatePolicy(
        string name,
        [FromBody] PolicyCreateRequest request,
        IPolicyRegistry registry)
    {
        var existing = registry.GetPolicy(name);
        if (existing == null)
            return Results.NotFound(new { error = $"Policy '{name}' not found" });

        // Create updated policy (keeping name)
        var policy = new DetectionPolicy
        {
            Name = name,
            Description = request.Description ?? existing.Description,
            FastPathDetectors = request.FastPath != null ? [.. request.FastPath] : existing.FastPathDetectors,
            SlowPathDetectors = request.SlowPath != null ? [.. request.SlowPath] : existing.SlowPathDetectors,
            AiPathDetectors = request.AiPath != null ? [.. request.AiPath] : existing.AiPathDetectors,
            UseFastPath = request.UseFastPath ?? existing.UseFastPath,
            ForceSlowPath = request.ForceSlowPath ?? existing.ForceSlowPath,
            EscalateToAi = request.EscalateToAi ?? existing.EscalateToAi,
            AiEscalationThreshold = request.AiEscalationThreshold ?? existing.AiEscalationThreshold,
            EarlyExitThreshold = request.EarlyExitThreshold ?? existing.EarlyExitThreshold,
            ImmediateBlockThreshold = request.ImmediateBlockThreshold ?? existing.ImmediateBlockThreshold,
            WeightOverrides = request.Weights != null
                ? request.Weights.ToImmutableDictionary()
                : existing.WeightOverrides,
            Timeout = request.TimeoutMs.HasValue
                ? TimeSpan.FromMilliseconds(request.TimeoutMs.Value)
                : existing.Timeout,
            Enabled = request.Enabled ?? existing.Enabled
        };

        registry.RegisterPolicy(policy);

        return Results.Json(ToDetailResponse(policy), JsonOptions);
    }

    private static IResult DeletePolicy(string name, IPolicyRegistry registry)
    {
        var builtIn = new[] { "default", "strict", "relaxed", "allowVerifiedBots" };
        if (builtIn.Contains(name, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"Cannot delete built-in policy '{name}'" });

        if (registry.GetPolicy(name) == null)
            return Results.NotFound(new { error = $"Policy '{name}' not found" });

        registry.RemovePolicy(name);
        return Results.NoContent();
    }

    private static IResult GetDefaultWeights(IPolicyEvaluator evaluator)
    {
        // Get weights for known detectors
        var detectors = new[]
        {
            "UserAgent", "Header", "Ip", "Behavioral",
            "Inconsistency", "ClientSide", "Onnx", "Llm", "IpReputation"
        };

        var emptyPolicy = new DetectionPolicy { Name = "_temp", FastPathDetectors = [] };
        var weights = detectors.ToDictionary(
            d => d,
            d => evaluator.GetEffectiveWeight(emptyPolicy, d));

        return Results.Json(new WeightsResponse { DefaultWeights = weights }, JsonOptions);
    }

    #endregion

    #region Response Models

    private static PolicySummary ToPolicySummary(DetectionPolicy policy)
    {
        return new PolicySummary
        {
            Name = policy.Name,
            Description = policy.Description,
            Enabled = policy.Enabled,
            DetectorCount = policy.FastPathDetectors.Count +
                            policy.SlowPathDetectors.Count +
                            policy.AiPathDetectors.Count,
            UseFastPath = policy.UseFastPath,
            ForceSlowPath = policy.ForceSlowPath,
            EscalateToAi = policy.EscalateToAi
        };
    }

    private static PolicyDetailResponse ToDetailResponse(DetectionPolicy policy)
    {
        return new PolicyDetailResponse
        {
            Name = policy.Name,
            Description = policy.Description,
            Enabled = policy.Enabled,
            FastPath = policy.FastPathDetectors.ToList(),
            SlowPath = policy.SlowPathDetectors.ToList(),
            AiPath = policy.AiPathDetectors.ToList(),
            UseFastPath = policy.UseFastPath,
            ForceSlowPath = policy.ForceSlowPath,
            EscalateToAi = policy.EscalateToAi,
            AiEscalationThreshold = policy.AiEscalationThreshold,
            EarlyExitThreshold = policy.EarlyExitThreshold,
            ImmediateBlockThreshold = policy.ImmediateBlockThreshold,
            TimeoutMs = (int)policy.Timeout.TotalMilliseconds,
            Weights = policy.WeightOverrides.ToDictionary(kv => kv.Key, kv => kv.Value),
            Transitions = policy.Transitions.Select(t => new TransitionResponse
            {
                WhenRiskExceeds = t.WhenRiskExceeds,
                WhenRiskBelow = t.WhenRiskBelow,
                WhenSignal = t.WhenSignal,
                WhenReputationState = t.WhenReputationState,
                GoToPolicy = t.GoToPolicy,
                Action = t.Action?.ToString(),
                Description = t.Description
            }).ToList()
        };
    }

    #endregion
}

#region Request/Response DTOs

public class PolicyListResponse
{
    public string DefaultPolicy { get; set; } = "";
    public List<PolicySummary> Policies { get; set; } = [];
}

public class PolicySummary
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public int DetectorCount { get; set; }
    public bool UseFastPath { get; set; }
    public bool ForceSlowPath { get; set; }
    public bool EscalateToAi { get; set; }
}

public class PolicyDetailResponse
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public List<string> FastPath { get; set; } = [];
    public List<string> SlowPath { get; set; } = [];
    public List<string> AiPath { get; set; } = [];
    public bool UseFastPath { get; set; }
    public bool ForceSlowPath { get; set; }
    public bool EscalateToAi { get; set; }
    public double AiEscalationThreshold { get; set; }
    public double EarlyExitThreshold { get; set; }
    public double ImmediateBlockThreshold { get; set; }
    public int TimeoutMs { get; set; }
    public Dictionary<string, double> Weights { get; set; } = new();
    public List<TransitionResponse> Transitions { get; set; } = [];
}

public class TransitionResponse
{
    public double? WhenRiskExceeds { get; set; }
    public double? WhenRiskBelow { get; set; }
    public string? WhenSignal { get; set; }
    public string? WhenReputationState { get; set; }
    public string? GoToPolicy { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
}

public class PolicyTestRequest
{
    public string? PolicyName { get; set; }
    public string? Path { get; set; }
}

public class PolicyTestResponse
{
    public string PolicyUsed { get; set; } = "";
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public string RiskBand { get; set; } = "";
    public string? PolicyAction { get; set; }
    public List<string> ContributingDetectors { get; set; } = [];
    public List<string> FailedDetectors { get; set; } = [];
    public double ProcessingTimeMs { get; set; }
    public bool EarlyExit { get; set; }
    public List<ContributionSummary> Contributions { get; set; } = [];
}

public class ContributionSummary
{
    public string Detector { get; set; } = "";
    public string Category { get; set; } = "";
    public double ConfidenceDelta { get; set; }
    public double Weight { get; set; }
    public string Reason { get; set; } = "";
}

public class PolicySimulationRequest
{
    public string PolicyName { get; set; } = "";
    public List<SimulationInput> Steps { get; set; } = [];
}

public class SimulationInput
{
    public double RiskScore { get; set; }
    public Dictionary<string, object>? Signals { get; set; }
    public List<string>? CompletedDetectors { get; set; }
}

public class PolicySimulationResponse
{
    public string InitialPolicy { get; set; } = "";
    public List<SimulationStep> Steps { get; set; } = [];
}

public class SimulationStep
{
    public double InputRiskScore { get; set; }
    public Dictionary<string, object>? InputSignals { get; set; }
    public string PolicyName { get; set; } = "";
    public bool ShouldContinue { get; set; }
    public string? NextPolicy { get; set; }
    public string? Action { get; set; }
    public string? Reason { get; set; }
}

public class PolicyCreateRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? FastPath { get; set; }
    public List<string>? SlowPath { get; set; }
    public List<string>? AiPath { get; set; }
    public bool? UseFastPath { get; set; }
    public bool? ForceSlowPath { get; set; }
    public bool? EscalateToAi { get; set; }
    public double? AiEscalationThreshold { get; set; }
    public double? EarlyExitThreshold { get; set; }
    public double? ImmediateBlockThreshold { get; set; }
    public int? TimeoutMs { get; set; }
    public Dictionary<string, double>? Weights { get; set; }
    public bool? Enabled { get; set; }
}

public class WeightsResponse
{
    public Dictionary<string, double> DefaultWeights { get; set; } = new();
}

#endregion