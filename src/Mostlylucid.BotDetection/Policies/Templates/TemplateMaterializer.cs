using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Runs at YAML load: walks every <see cref="TemplateApplication"/>,
///     resolves it through <see cref="TemplateResolver"/>, and returns a flat
///     list of materialized <see cref="PolicyRule"/> instances. Each rule
///     carries its <see cref="RuleOrigin"/> and an accompanying hash so the
///     store can detect when an operator has manually edited a materialized
///     rule on next reload.
///
///     <para>
///         The materializer is the single seam between the template runtime
///         (<see cref="TemplateRegistry"/> + <see cref="TemplateResolver"/>)
///         and the rule store. It is the only place templates fan out into
///         bare rules -- everything downstream (resolver, evaluator,
///         dispatcher, trigger service) sees a flat <see cref="PolicyRule"/>
///         list and is template-agnostic by design.
///     </para>
/// </summary>
public sealed class TemplateMaterializer
{
    private readonly TemplateRegistry _registry;
    private readonly TemplateResolver _resolver;

    public TemplateMaterializer(TemplateRegistry registry, TemplateResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);
        _registry = registry;
        _resolver = resolver;
    }

    /// <summary>
    ///     Resolve every application against its referenced template and
    ///     return the flat materialized rule list plus a per-rule hash map
    ///     and a list of per-application errors (missing template, parser
    ///     failure). Errors do NOT block other applications from
    ///     materializing -- one bad application never poisons the corpus.
    /// </summary>
    public MaterializationResult Materialize(IReadOnlyList<TemplateApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);

        var rules = new List<PolicyRule>();
        var errors = new List<MaterializationError>();
        var ruleHashes = new Dictionary<Guid, string>();

        foreach (var app in applications)
        {
            var template = _registry.Find(app.TemplateId);
            if (template is null)
            {
                errors.Add(new MaterializationError(app.Id, $"Template '{app.TemplateId}' not found"));
                continue;
            }

            try
            {
                var materialized = _resolver.Resolve(template, app);
                foreach (var rule in materialized)
                {
                    rules.Add(rule);
                    ruleHashes[rule.Id] = RuleHasher.Hash(rule);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new MaterializationError(app.Id, ex.Message));
            }
        }

        return new MaterializationResult(rules, ruleHashes, errors);
    }

    /// <summary>
    ///     Re-materialize with override-divergence detection. For each rule id
    ///     the new materialization produces, if an existing rule with the same
    ///     id is present in <paramref name="existing"/> AND its current hash
    ///     differs from the hash carried in <paramref name="priorHashes"/>
    ///     from the previous materialization, the operator manually edited the
    ///     rule -- preserve the existing rule, mark it diverged in
    ///     <see cref="MaterializationResult.DivergedRuleIds"/>, and do NOT
    ///     overwrite.
    ///
    ///     <para>
    ///         False-positive guarantee: a rule that re-materializes to the
    ///         same hash as before is NEVER marked diverged, regardless of
    ///         whether <paramref name="existing"/> contains it. Divergence
    ///         requires (a) the prior materialization saw the rule, (b) the
    ///         existing rule's current hash mismatches that prior hash.
    ///     </para>
    /// </summary>
    public MaterializationResult MaterializeWithDivergence(
        IReadOnlyList<TemplateApplication> applications,
        IReadOnlyDictionary<Guid, PolicyRule> existing,
        IReadOnlyDictionary<Guid, string> priorHashes)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(priorHashes);

        var fresh = Materialize(applications);
        var combinedRules = new List<PolicyRule>(fresh.Rules.Count);
        var combinedHashes = new Dictionary<Guid, string>(fresh.RuleHashes.Count);
        var divergedIds = new HashSet<Guid>();

        foreach (var rule in fresh.Rules)
        {
            // Default: take the fresh materialization.
            var emit = rule;
            var emitHash = fresh.RuleHashes[rule.Id];

            if (existing.TryGetValue(rule.Id, out var existingRule) &&
                priorHashes.TryGetValue(rule.Id, out var priorHash))
            {
                var existingHash = RuleHasher.Hash(existingRule);
                if (!string.Equals(existingHash, priorHash, StringComparison.Ordinal))
                {
                    // Operator edited the materialized rule between reloads.
                    // Preserve their edit; don't overwrite. Mark as diverged.
                    emit = existingRule;
                    emitHash = existingHash;
                    divergedIds.Add(rule.Id);
                }
            }

            combinedRules.Add(emit);
            combinedHashes[rule.Id] = emitHash;
        }

        return new MaterializationResult(combinedRules, combinedHashes, fresh.Errors, divergedIds);
    }
}

/// <summary>
///     Result of a <see cref="TemplateMaterializer.Materialize"/> call.
///     <see cref="Rules"/> is the flat materialized rule list (origin tags
///     intact). <see cref="RuleHashes"/> maps each rule id to its
///     <see cref="RuleHasher"/> digest for next-reload divergence detection.
///     <see cref="Errors"/> carries per-application failures. <see cref="DivergedRuleIds"/>
///     is populated only by <see cref="TemplateMaterializer.MaterializeWithDivergence"/>;
///     <c>null</c> on a plain <see cref="TemplateMaterializer.Materialize"/>
///     call.
/// </summary>
public sealed record MaterializationResult(
    IReadOnlyList<PolicyRule> Rules,
    IReadOnlyDictionary<Guid, string> RuleHashes,
    IReadOnlyList<MaterializationError> Errors,
    IReadOnlySet<Guid>? DivergedRuleIds = null);

/// <summary>
///     One per-application materialization failure. <see cref="ApplicationId"/>
///     identifies the application that failed; <see cref="Message"/> is the
///     surfaceable error text. Errors are reported per-application so a single
///     bad application does not block the rest of the corpus.
/// </summary>
public sealed record MaterializationError(string ApplicationId, string Message);
