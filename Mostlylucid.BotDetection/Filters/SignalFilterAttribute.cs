using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Comparison operators for signal-based filtering.
/// </summary>
public enum SignalOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Contains,
    Exists
}

/// <summary>
///     Action filter that blocks requests based on specific detection signal values.
///     Use <see cref="SignalKeys" /> for available signal key constants.
///     Combine multiple attributes on a single action for AND logic.
/// </summary>
/// <example>
///     // Block requests from VPNs
///     [BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True")]
///     public IActionResult Payment() { ... }
///
///     // Block requests from specific countries
///     [BlockIfSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "CN")]
///     public IActionResult Restricted() { ... }
///
///     // Block if datacenter IP detected
///     [BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True")]
///     public IActionResult ApiEndpoint() { ... }
///
///     // Block if heuristic confidence is very high
///     [BlockIfSignal(SignalKeys.HeuristicConfidence, SignalOperator.GreaterThan, "0.95")]
///     public IActionResult SensitiveAction() { ... }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class BlockIfSignalAttribute : ActionFilterAttribute
{
    public string SignalKey { get; }
    public SignalOperator Operator { get; }
    public string? Value { get; }
    public int StatusCode { get; set; } = 403;
    public string Message { get; set; } = "Access denied";

    public BlockIfSignalAttribute(string signalKey, SignalOperator op, string? value = null)
    {
        SignalKey = signalKey;
        Operator = op;
        Value = value;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (SignalEvaluator.Evaluate(context.HttpContext.GetSignals(), SignalKey, Operator, Value))
        {
            context.Result = new ObjectResult(new { error = Message, blocked = true, signal = SignalKey })
                { StatusCode = StatusCode };
            return;
        }

        base.OnActionExecuting(context);
    }
}

/// <summary>
///     Action filter that allows requests only when a specific signal condition is met.
///     Inverse of <see cref="BlockIfSignalAttribute" /> — blocks when the condition is NOT met.
/// </summary>
/// <example>
///     // Only allow requests from the US
///     [RequireSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "US")]
///     public IActionResult UsOnly() { ... }
///
///     // Require that geo detection ran (signal exists)
///     [RequireSignal(SignalKeys.GeoCountryCode, SignalOperator.Exists)]
///     public IActionResult GeoRequired() { ... }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequireSignalAttribute : ActionFilterAttribute
{
    public string SignalKey { get; }
    public SignalOperator Operator { get; }
    public string? Value { get; }
    public int StatusCode { get; set; } = 403;
    public string Message { get; set; } = "Access denied";

    public RequireSignalAttribute(string signalKey, SignalOperator op, string? value = null)
    {
        SignalKey = signalKey;
        Operator = op;
        Value = value;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // Block if condition is NOT met (inverse of BlockIfSignal)
        if (!SignalEvaluator.Evaluate(context.HttpContext.GetSignals(), SignalKey, Operator, Value))
        {
            context.Result = new ObjectResult(new { error = Message, blocked = true, signal = SignalKey })
                { StatusCode = StatusCode };
            return;
        }

        base.OnActionExecuting(context);
    }
}
