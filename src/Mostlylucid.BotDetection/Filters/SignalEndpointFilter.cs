using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Endpoint filter for minimal APIs that blocks requests based on specific signal values.
/// </summary>
public class BlockIfSignalEndpointFilter : IEndpointFilter
{
    private readonly string _signalKey;
    private readonly SignalOperator _operator;
    private readonly string? _value;
    private readonly int _statusCode;

    public BlockIfSignalEndpointFilter(string signalKey, SignalOperator op, string? value = null,
        int statusCode = 403)
    {
        _signalKey = signalKey;
        _operator = op;
        _value = value;
        _statusCode = statusCode;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var signals = context.HttpContext.GetSignals();

        if (SignalEvaluator.Evaluate(signals, _signalKey, _operator, _value))
        {
            return Results.Json(new
            {
                error = "Access denied",
                blocked = true,
                signal = _signalKey
            }, statusCode: _statusCode);
        }

        return await next(context);
    }
}

/// <summary>
///     Endpoint filter for minimal APIs that requires a specific signal condition to be met.
/// </summary>
public class RequireSignalEndpointFilter : IEndpointFilter
{
    private readonly string _signalKey;
    private readonly SignalOperator _operator;
    private readonly string? _value;
    private readonly int _statusCode;

    public RequireSignalEndpointFilter(string signalKey, SignalOperator op, string? value = null,
        int statusCode = 403)
    {
        _signalKey = signalKey;
        _operator = op;
        _value = value;
        _statusCode = statusCode;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var signals = context.HttpContext.GetSignals();

        if (!SignalEvaluator.Evaluate(signals, _signalKey, _operator, _value))
        {
            return Results.Json(new
            {
                error = "Access denied",
                blocked = true,
                signal = _signalKey
            }, statusCode: _statusCode);
        }

        return await next(context);
    }
}

/// <summary>
///     Shared signal evaluation logic used by both MVC attributes and minimal API endpoint filters.
/// </summary>
internal static class SignalEvaluator
{
    public static bool Evaluate(IReadOnlyDictionary<string, object> signals, string signalKey, SignalOperator op,
        string? value)
    {
        if (op == SignalOperator.Exists)
            return signals.ContainsKey(signalKey);

        if (!signals.TryGetValue(signalKey, out var signalValue))
            return false;

        return op switch
        {
            SignalOperator.Equals => CompareEquals(signalValue, value),
            SignalOperator.NotEquals => !CompareEquals(signalValue, value),
            SignalOperator.GreaterThan => CompareNumeric(signalValue, value) > 0,
            SignalOperator.LessThan => CompareNumeric(signalValue, value) < 0,
            SignalOperator.GreaterThanOrEqual => CompareNumeric(signalValue, value) >= 0,
            SignalOperator.LessThanOrEqual => CompareNumeric(signalValue, value) <= 0,
            SignalOperator.Contains => signalValue?.ToString()?.Contains(value ?? "",
                StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    private static bool CompareEquals(object? signalValue, string? compareValue)
    {
        if (signalValue == null && compareValue == null) return true;
        if (signalValue == null || compareValue == null) return false;
        if (signalValue is bool boolVal)
            return bool.TryParse(compareValue, out var cmp) && boolVal == cmp;
        return string.Equals(signalValue.ToString(), compareValue, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareNumeric(object? signalValue, string? compareValue)
    {
        if (signalValue == null || compareValue == null) return 0;
        if (double.TryParse(signalValue.ToString(), out var sigNum)
            && double.TryParse(compareValue, out var cmpNum))
            return sigNum.CompareTo(cmpNum);
        return string.Compare(signalValue.ToString(), compareValue, StringComparison.OrdinalIgnoreCase);
    }
}
