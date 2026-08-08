using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that immediately aborts the underlying connection
///     without writing any response, headers, or body. Mirrors HAProxy's
///     <c>silent-drop</c> primitive: the server closes its end of the
///     socket, the client (the bot) sees no acknowledgement and is left to
///     time out on its own. No HTTP status, no <c>Retry-After</c>, no
///     informational hint that throttling occurred.
/// </summary>
/// <remarks>
///     <para>
///         Use this policy when the verdict is high-confidence bot AND
///         there is no policy value in returning a 429: silent-drop saves
///         the Kestrel concurrent-connection slot, the HTTP/2 stream
///         budget, the HttpContext tree, and the response-buffer memory
///         that <see cref="ThrottleActionPolicy"/>'s stealth path holds
///         for the full delay window. At any meaningful RPS that
///         resource-hold is the actual constraint, not threadpool
///         exhaustion (<c>Task.Delay</c> doesn't burn a thread). A bot
///         operator who notices stealth and piles up connections can
///         exhaust <c>MaxConcurrentConnections</c> long before threads;
///         silent-drop is the structural fix.
///     </para>
///     <para>
///         The policy is opt-in: register it explicitly and reference it
///         from your action-policy mapping (or as <c>OverLimitAction</c>
///         on <see cref="RateLimitActionPolicy"/>). It is NOT wired in as
///         the default <c>DefaultActionPolicyName</c> so the documented
///         "throttle-stealth at pre-launch" posture is preserved until an
///         operator flips the lever.
///     </para>
///     <para>
///         Surfaces under <see cref="ActionType.Block"/> /
///         <see cref="PolicyIntent.Block"/> so dashboards group it with
///         the other hard-deny outcomes. The <see cref="ActionResult.StatusCode"/>
///         is deliberately <c>0</c> to document "no HTTP response emitted"
///         and let downstream logging / metrics distinguish a silent-drop
///         from a 200-with-no-body.
///     </para>
/// </remarks>
public class SilentDropActionPolicy : IActionPolicy
{
    private readonly ILogger<SilentDropActionPolicy>? _logger;

    public SilentDropActionPolicy(string name, ILogger<SilentDropActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Block;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Block;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation(
            "Silent-drop applied to {Path}: policy={Policy}, bot_probability={BotProbability:F2}",
            context.Request.Path, Name, evidence.BotProbability);

        // HttpContext.Abort() signals the connection-lifetime feature so
        // Kestrel resets the TCP socket without writing any response. The
        // request pipeline short-circuits naturally because Continue=false.
        context.Abort();

        return Task.FromResult(new ActionResult
        {
            Continue = false,
            StatusCode = 0,
            Description = $"silent-drop by {Name}: connection aborted, no response written",
            Metadata = new Dictionary<string, object>
            {
                ["policy"] = Name,
                ["risk"] = evidence.BotProbability
            }
        });
    }
}
