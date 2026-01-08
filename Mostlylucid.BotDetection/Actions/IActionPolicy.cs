using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Base interface for all action policies.
///     Action policies define HOW to respond when a bot is detected.
///     They are separate from detection policies (WHAT to detect) for composability.
/// </summary>
/// <remarks>
///     <para>
///         Action policies are pluggable - implement this interface to create custom actions.
///         Register your implementation with DI:
///         <code>
///         services.AddSingleton&lt;IActionPolicy, MyCustomActionPolicy&gt;();
///         </code>
///     </para>
///     <para>
///         Built-in action policies:
///         <list type="bullet">
///             <item><see cref="BlockActionPolicy" /> - Returns HTTP error status codes</item>
///             <item><see cref="ThrottleActionPolicy" /> - Rate limits with configurable delays and jitter</item>
///             <item><see cref="ChallengeActionPolicy" /> - Presents CAPTCHA or proof-of-work challenges</item>
///             <item><see cref="RedirectActionPolicy" /> - Redirects to a different URL</item>
///             <item><see cref="LogOnlyActionPolicy" /> - Logs but doesn't block (shadow mode)</item>
///         </list>
///     </para>
/// </remarks>
public interface IActionPolicy
{
    /// <summary>
    ///     Unique name of this action policy.
    ///     Used for referencing in configuration and detection policies.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     The type of action this policy performs.
    /// </summary>
    ActionType ActionType { get; }

    /// <summary>
    ///     Execute the action policy against the current request.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <param name="evidence">Detection result that triggered this action</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating whether to continue the pipeline or short-circuit</returns>
    Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of executing an action policy.
/// </summary>
public sealed record ActionResult
{
    /// <summary>
    ///     Whether to continue to the next middleware in the pipeline.
    ///     If false, the response has been written and pipeline should short-circuit.
    /// </summary>
    public bool Continue { get; init; }

    /// <summary>
    ///     HTTP status code that was/will be returned.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    ///     Description of the action taken (for logging/debugging).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Additional metadata about the action.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Creates a result indicating the request was blocked.
    /// </summary>
    public static ActionResult Blocked(int statusCode, string description)
    {
        return new ActionResult { Continue = false, StatusCode = statusCode, Description = description };
    }

    /// <summary>
    ///     Creates a result indicating the request should continue.
    /// </summary>
    public static ActionResult Allowed(string? description = null)
    {
        return new ActionResult { Continue = true, StatusCode = 200, Description = description ?? "Allowed" };
    }

    /// <summary>
    ///     Creates a result indicating a redirect was issued.
    /// </summary>
    public static ActionResult Redirected(string url)
    {
        return new ActionResult { Continue = false, StatusCode = 302, Description = $"Redirected to {url}" };
    }
}

/// <summary>
///     Types of actions that can be taken.
/// </summary>
public enum ActionType
{
    /// <summary>Block with HTTP error status</summary>
    Block,

    /// <summary>Throttle/rate limit with delays</summary>
    Throttle,

    /// <summary>Present a challenge (CAPTCHA, proof-of-work)</summary>
    Challenge,

    /// <summary>Redirect to another URL</summary>
    Redirect,

    /// <summary>Log only, don't actually block (shadow mode)</summary>
    LogOnly,

    /// <summary>Custom action type</summary>
    Custom
}

/// <summary>
///     Registry for named action policies.
///     Provides lookup by name and type.
/// </summary>
public interface IActionPolicyRegistry
{
    /// <summary>
    ///     Get an action policy by name.
    /// </summary>
    IActionPolicy? GetPolicy(string name);

    /// <summary>
    ///     Get all policies of a specific type.
    /// </summary>
    IEnumerable<IActionPolicy> GetPoliciesByType(ActionType type);

    /// <summary>
    ///     Get all registered policies.
    /// </summary>
    IReadOnlyDictionary<string, IActionPolicy> GetAllPolicies();

    /// <summary>
    ///     Register a new action policy.
    /// </summary>
    void RegisterPolicy(IActionPolicy policy);

    /// <summary>
    ///     Get the default policy for an action type.
    /// </summary>
    IActionPolicy GetDefaultPolicy(ActionType type);
}

/// <summary>
///     Factory for creating action policies from configuration.
///     Implement this interface to support custom action policy creation.
/// </summary>
public interface IActionPolicyFactory
{
    /// <summary>
    ///     The action type this factory creates.
    /// </summary>
    ActionType ActionType { get; }

    /// <summary>
    ///     Create an action policy from configuration options.
    /// </summary>
    /// <param name="name">Policy name</param>
    /// <param name="options">Configuration options dictionary</param>
    /// <returns>The created action policy</returns>
    IActionPolicy Create(string name, IDictionary<string, object> options);
}