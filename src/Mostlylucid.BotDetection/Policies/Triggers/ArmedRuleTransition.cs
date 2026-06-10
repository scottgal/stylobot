namespace Mostlylucid.BotDetection.Policies.Triggers;

/// <summary>
///     Event payload for <see cref="ArmedRuleRegistry.OnTransition"/>. Raised
///     whenever a rule transitions UNARMED -> ARMED or ARMED -> UNARMED. The
///     audit log subscribes to the registry rather than the atom directly so
///     transitions across the whole corpus stream through one observable.
/// </summary>
/// <param name="RuleId">The <see cref="Rules.PolicyRule.Id"/> that transitioned.</param>
/// <param name="NowArmed"><c>true</c> when the new state is ARMED; <c>false</c> when it is UNARMED.</param>
/// <param name="At">Wall-clock instant of the transition (the <c>now</c> the trigger service fed in).</param>
public sealed record ArmedRuleTransition(Guid RuleId, bool NowArmed, DateTimeOffset At);
