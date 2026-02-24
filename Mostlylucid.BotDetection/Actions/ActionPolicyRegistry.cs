using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Default implementation of <see cref="IActionPolicyRegistry" />.
///     Manages named action policies and provides lookup by name and type.
/// </summary>
/// <remarks>
///     <para>
///         The registry is populated from:
///         <list type="number">
///             <item>Built-in default policies (block, throttle, challenge, logOnly)</item>
///             <item>Configuration-defined policies from appsettings.json</item>
///             <item>Programmatically registered policies via RegisterPolicy()</item>
///         </list>
///     </para>
///     <para>
///         Configuration example:
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "hardBlock": {
///                 "Type": "Block",
///                 "StatusCode": 403,
///                 "Message": "Access denied"
///               },
///               "softThrottle": {
///                 "Type": "Throttle",
///                 "BaseDelayMs": 500,
///                 "JitterPercent": 0.25
///               }
///             }
///           }
///         }
///         </code>
///     </para>
/// </remarks>
public class ActionPolicyRegistry : IActionPolicyRegistry
{
    private readonly IEnumerable<IActionPolicyFactory> _factories;
    private readonly ILogger<ActionPolicyRegistry>? _logger;
    private readonly BotDetectionOptions _options;
    private readonly ConcurrentDictionary<string, IActionPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Creates a new action policy registry.
    /// </summary>
    public ActionPolicyRegistry(
        IOptions<BotDetectionOptions> options,
        IEnumerable<IActionPolicyFactory> factories,
        IEnumerable<IActionPolicy>? additionalPolicies = null,
        ILogger<ActionPolicyRegistry>? logger = null)
    {
        _options = options.Value;
        _factories = factories;
        _logger = logger;

        // Register built-in defaults
        RegisterBuiltInPolicies();

        // Register policies from configuration
        RegisterConfiguredPolicies();

        // Register any additional policies from DI
        if (additionalPolicies != null)
            foreach (var policy in additionalPolicies)
                RegisterPolicy(policy);
    }

    /// <inheritdoc />
    public IActionPolicy? GetPolicy(string name)
    {
        return _policies.TryGetValue(name, out var policy) ? policy : null;
    }

    /// <inheritdoc />
    public IEnumerable<IActionPolicy> GetPoliciesByType(ActionType type)
    {
        return _policies.Values.Where(p => p.ActionType == type);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IActionPolicy> GetAllPolicies()
    {
        return _policies.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <inheritdoc />
    public void RegisterPolicy(IActionPolicy policy)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));

        _policies[policy.Name] = policy;
        _logger?.LogDebug("Registered action policy: {Name} ({Type})", policy.Name, policy.ActionType);
    }

    /// <inheritdoc />
    public IActionPolicy GetDefaultPolicy(ActionType type)
    {
        // Return the first policy of this type, or the built-in default
        var policy = _policies.Values.FirstOrDefault(p => p.ActionType == type);

        if (policy != null) return policy;

        // Create and register a default
        return type switch
        {
            ActionType.Block => GetOrCreateDefault("default-block",
                () => new BlockActionPolicy("default-block", BlockActionOptions.Hard)),
            ActionType.Throttle => GetOrCreateDefault("default-throttle",
                () => new ThrottleActionPolicy("default-throttle", ThrottleActionOptions.Moderate)),
            ActionType.Challenge => GetOrCreateDefault("default-challenge",
                () => new ChallengeActionPolicy("default-challenge", new ChallengeActionOptions())),
            ActionType.Redirect => GetOrCreateDefault("default-redirect",
                () => new RedirectActionPolicy("default-redirect", RedirectActionOptions.BlockedPage)),
            ActionType.LogOnly => GetOrCreateDefault("default-logonly",
                () => new LogOnlyActionPolicy("default-logonly", LogOnlyActionOptions.Minimal)),
            _ => throw new ArgumentException($"No default policy for action type: {type}")
        };
    }

    private IActionPolicy GetOrCreateDefault(string name, Func<IActionPolicy> factory)
    {
        if (_policies.TryGetValue(name, out var existing))
            return existing;

        var policy = factory();
        RegisterPolicy(policy);
        return policy;
    }

    private void RegisterBuiltInPolicies()
    {
        // Block policies
        RegisterPolicy(new BlockActionPolicy("block", BlockActionOptions.Hard));
        RegisterPolicy(new BlockActionPolicy("block-hard", BlockActionOptions.Hard));
        RegisterPolicy(new BlockActionPolicy("block-soft", BlockActionOptions.Soft));
        RegisterPolicy(new BlockActionPolicy("block-debug", BlockActionOptions.Debug));

        // Block policies - stealth (fake success)
        RegisterPolicy(new BlockActionPolicy("block-fake-success", BlockActionOptions.FakeSuccess));
        RegisterPolicy(new BlockActionPolicy("block-fake-html", BlockActionOptions.FakeHtml));

        // Throttle policies
        RegisterPolicy(new ThrottleActionPolicy("throttle", ThrottleActionOptions.Moderate));
        RegisterPolicy(new ThrottleActionPolicy("throttle-gentle", ThrottleActionOptions.Gentle));
        RegisterPolicy(new ThrottleActionPolicy("throttle-moderate", ThrottleActionOptions.Moderate));
        RegisterPolicy(new ThrottleActionPolicy("throttle-aggressive", ThrottleActionOptions.Aggressive));
        RegisterPolicy(new ThrottleActionPolicy("throttle-stealth", ThrottleActionOptions.Stealth));
        RegisterPolicy(new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools));
        RegisterPolicy(new ThrottleActionPolicy("throttle-escalating", ThrottleActionOptions.Escalating));

        // Redirect policies
        RegisterPolicy(new RedirectActionPolicy("redirect", RedirectActionOptions.BlockedPage));
        RegisterPolicy(new RedirectActionPolicy("redirect-honeypot", RedirectActionOptions.Honeypot));
        RegisterPolicy(new RedirectActionPolicy("redirect-tarpit", RedirectActionOptions.Tarpit));
        RegisterPolicy(new RedirectActionPolicy("redirect-error", RedirectActionOptions.ErrorPage));

        // Challenge policies
        RegisterPolicy(new ChallengeActionPolicy("challenge", new ChallengeActionOptions()));
        RegisterPolicy(new ChallengeActionPolicy("challenge-captcha", new ChallengeActionOptions
        {
            ChallengeType = ChallengeType.Captcha
        }));
        RegisterPolicy(new ChallengeActionPolicy("challenge-js", new ChallengeActionOptions
        {
            ChallengeType = ChallengeType.JavaScript
        }));
        RegisterPolicy(new ChallengeActionPolicy("challenge-pow", new ChallengeActionOptions
        {
            ChallengeType = ChallengeType.ProofOfWork
        }));

        // Log-only policies - basic
        RegisterPolicy(new LogOnlyActionPolicy("logonly", LogOnlyActionOptions.Minimal));
        RegisterPolicy(new LogOnlyActionPolicy("shadow", LogOnlyActionOptions.ShadowWithHeaders));
        RegisterPolicy(new LogOnlyActionPolicy("debug", LogOnlyActionOptions.Debug));
        RegisterPolicy(new LogOnlyActionPolicy("full-log", LogOnlyActionOptions.FullLog));

        // Log-only policies - action markers (set HttpContext.Items for downstream)
        RegisterPolicy(new LogOnlyActionPolicy("degrade", LogOnlyActionOptions.Degrade));
        RegisterPolicy(new LogOnlyActionPolicy("rate-limit-headers", LogOnlyActionOptions.RateLimitHeaders));
        RegisterPolicy(new LogOnlyActionPolicy("quarantine", LogOnlyActionOptions.Quarantine));

        // Log-only policies - sandbox/probation (YARP deep analysis)
        RegisterPolicy(new LogOnlyActionPolicy("sandbox", LogOnlyActionOptions.Sandbox));

        // Log-only policies - specialized (these are templates/examples - override in config)
        // Note: Forward/File logging options require configuration to be useful
        RegisterPolicy(new LogOnlyActionPolicy("shadow-production", new LogOnlyActionOptions
        {
            LogLevel = LogLevel.Information,
            LogFullEvidence = false,
            AddResponseHeaders = false,
            AddToContextItems = true,
            WouldBlockThreshold = 0.7
        }));

        RegisterPolicy(new LogOnlyActionPolicy("strict-block-log", new LogOnlyActionOptions
        {
            LogLevel = LogLevel.Warning,
            LogFullEvidence = true,
            AddResponseHeaders = false,
            AddToContextItems = true,
            WouldBlockThreshold = 0.85
        }));

        // Response mutation actions (allow request, sanitize response payload)
        RegisterPolicy(new LogOnlyActionPolicy("mask-pii", new LogOnlyActionOptions
        {
            LogLevel = LogLevel.Warning,
            LogFullEvidence = true,
            AddResponseHeaders = false,
            AddToContextItems = true,
            ActionMarker = "mask-pii",
            WouldBlockThreshold = 0.85
        }));
        RegisterPolicy(new LogOnlyActionPolicy("strip-pii", new LogOnlyActionOptions
        {
            LogLevel = LogLevel.Warning,
            LogFullEvidence = true,
            AddResponseHeaders = false,
            AddToContextItems = true,
            ActionMarker = "mask-pii",
            WouldBlockThreshold = 0.85
        }));

        _logger?.LogDebug("Registered {Count} built-in action policies", _policies.Count);
    }

    private void RegisterConfiguredPolicies()
    {
        if (_options.ActionPolicies == null || _options.ActionPolicies.Count == 0)
            return;

        var factoryByType = _factories.ToDictionary(f => f.ActionType);

        foreach (var (name, config) in _options.ActionPolicies)
            try
            {
                // Skip disabled policies
                if (!config.Enabled)
                {
                    _logger?.LogDebug("Skipping disabled action policy: {Name}", name);
                    continue;
                }

                if (string.IsNullOrEmpty(config.Type))
                {
                    _logger?.LogWarning("Action policy '{Name}' missing Type property", name);
                    continue;
                }

                if (!Enum.TryParse<ActionType>(config.Type, true, out var actionType))
                {
                    _logger?.LogWarning("Action policy '{Name}' has invalid Type: {Type}", name, config.Type);
                    continue;
                }

                if (factoryByType.TryGetValue(actionType, out var factory))
                {
                    // Convert typed config to dictionary for factory consumption
                    var policy = factory.Create(name, config.ToDictionary());
                    RegisterPolicy(policy);
                    _logger?.LogDebug("Created action policy from config: {Name} ({Type})", name, actionType);
                }
                else
                {
                    _logger?.LogWarning("No factory found for action type: {Type}", actionType);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating action policy '{Name}'", name);
            }
    }
}

/// <summary>
///     Extension methods for <see cref="IActionPolicyRegistry" />.
/// </summary>
public static class ActionPolicyRegistryExtensions
{
    /// <summary>
    ///     Gets a policy by name, or returns the default for the specified type.
    /// </summary>
    public static IActionPolicy GetPolicyOrDefault(
        this IActionPolicyRegistry registry,
        string? name,
        ActionType defaultType = ActionType.Block)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var policy = registry.GetPolicy(name);
            if (policy != null) return policy;
        }

        return registry.GetDefaultPolicy(defaultType);
    }

    /// <summary>
    ///     Gets all block policies.
    /// </summary>
    public static IEnumerable<IActionPolicy> GetBlockPolicies(this IActionPolicyRegistry registry)
    {
        return registry.GetPoliciesByType(ActionType.Block);
    }

    /// <summary>
    ///     Gets all throttle policies.
    /// </summary>
    public static IEnumerable<IActionPolicy> GetThrottlePolicies(this IActionPolicyRegistry registry)
    {
        return registry.GetPoliciesByType(ActionType.Throttle);
    }

    /// <summary>
    ///     Gets all challenge policies.
    /// </summary>
    public static IEnumerable<IActionPolicy> GetChallengePolicies(this IActionPolicyRegistry registry)
    {
        return registry.GetPoliciesByType(ActionType.Challenge);
    }

    /// <summary>
    ///     Gets all redirect policies.
    /// </summary>
    public static IEnumerable<IActionPolicy> GetRedirectPolicies(this IActionPolicyRegistry registry)
    {
        return registry.GetPoliciesByType(ActionType.Redirect);
    }

    /// <summary>
    ///     Gets all log-only policies.
    /// </summary>
    public static IEnumerable<IActionPolicy> GetLogOnlyPolicies(this IActionPolicyRegistry registry)
    {
        return registry.GetPoliciesByType(ActionType.LogOnly);
    }
}
