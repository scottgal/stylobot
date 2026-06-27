using System.Text.Json;
using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Builds the <see cref="EphemeralPrompt"/> for the cluster-naming path.
///     The legacy <c>LlmDescriptionCoordinator.GenerateClusterDescriptionAsync</c>
///     owns the full prompt template internally (it derives aggregates from the
///     members list before composing the string) and dispatches via a reflective
///     <c>ILlmRequest</c> handle. There is no caller-side prompt to surface on the
///     <see cref="EphemeralPrompt"/> payload. To preserve that behaviour verbatim
///     we serialize the cluster + members as JSON into
///     <see cref="EphemeralPrompt.UserPrompt"/> and let
///     <see cref="ClusterLlmInvoker"/> deserialize them back before invoking the
///     reflective provider. MaxTokens/Temperature surface as the plan-specified
///     defaults (256 / 0.2) — the reflective provider sets its own values, but the
///     EphemeralPrompt contract requires values here.
/// </summary>
public sealed class ClusterNamingPrompter : IEphemeralPrompter<ClusterPickItem>
{
    private const string ClusterPromptSystem = "stylobot.cluster-naming";
    private const int DefaultMaxTokens = 256;
    private const double DefaultTemperature = 0.2;

    public EphemeralPrompt Build(ClusterPickItem item)
    {
        var payload = JsonSerializer.Serialize(new ClusterPromptPayload(
            item.ClusterId,
            item.Cluster,
            item.Members));

        return new EphemeralPrompt(
            SystemPrompt: ClusterPromptSystem,
            UserPrompt: payload,
            MaxTokens: DefaultMaxTokens,
            Temperature: DefaultTemperature);
    }

    /// <summary>
    ///     Wire shape the prompter serializes and the invoker deserializes. Kept
    ///     internal to the LLM-namer subsystem; the EphemeralPrompt contract treats
    ///     <c>UserPrompt</c> as an opaque string.
    /// </summary>
    internal sealed record ClusterPromptPayload(
        string ClusterId,
        Mostlylucid.BotDetection.Services.BotCluster Cluster,
        IReadOnlyList<Mostlylucid.BotDetection.Orchestration.SignatureBehavior> Members);
}
