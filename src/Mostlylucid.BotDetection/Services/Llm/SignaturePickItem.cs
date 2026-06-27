namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Carrier the EphemeralLlmCoordinator's picker hands to the prompter / invoker /
///     writeback for the signature path. Contains the minimum the downstream
///     collaborators need to build the prompt and address the result.
/// </summary>
public sealed record SignaturePickItem(
    string Signature,
    IReadOnlyDictionary<string, object?> Signals);
