namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Carrier the EphemeralLlmCoordinator picker hands to the prompter / invoker /
///     writeback for the per-FINGERPRINT naming path. Carries only the identity +
///     the matcher-projected <c>InducedName</c> that triggered the drift pick;
///     the invoker resolves the latest signal snapshot from
///     <see cref="Identity.IFingerprintStore.GetDisplayNameHistoryAsync"/> when it
///     actually needs to call the synthesizer (the prompter is sync and the picker
///     stays atom-only per the EC6c hot-path discipline).
/// </summary>
public sealed record FingerprintPickItem(
    string FingerprintId,
    string? InducedName);
