using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Carrier the EphemeralLlmCoordinator's picker hands to the prompter / invoker /
///     writeback for the cluster path. Contains the minimum the downstream
///     collaborators need to address the cluster and reconstruct the LLM payload.
/// </summary>
public sealed record ClusterPickItem(
    string ClusterId,
    BotCluster Cluster,
    IReadOnlyList<SignatureBehavior> Members);
