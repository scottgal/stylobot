using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Content gate based on detection signals. Shows or hides children based on the presence
///     or value of one or more blackboard signals from the detection pipeline.
/// </summary>
/// <example>
///     <code>&lt;sb-signal signal="geo.is_vpn" value="true"&gt;You're on a VPN&lt;/sb-signal&gt;</code>
///     <code>&lt;sb-signal signal="geo.country_code" value="US"&gt;US visitor&lt;/sb-signal&gt;</code>
///     <code>&lt;sb-signal signal="ip.is_datacenter" condition="exists"&gt;Datacenter check ran&lt;/sb-signal&gt;</code>
///     <code>&lt;sb-signal signal="geo.is_vpn,geo.is_tor" condition="any-true"&gt;Anonymised traffic&lt;/sb-signal&gt;</code>
/// </example>
[HtmlTargetElement("sb-signal")]
public class SbSignalTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SbSignalTagHelper(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    ///     Comma-separated signal key(s) to check.
    ///     Use keys from <c>SignalKeys</c> (e.g. "geo.country_code", "ip.is_datacenter", "geo.is_vpn").
    /// </summary>
    [HtmlAttributeName("signal")]
    public string Signal { get; set; } = "";

    /// <summary>
    ///     Condition to evaluate:
    ///     <list type="bullet">
    ///         <item><c>exists</c> — signal key exists (default)</item>
    ///         <item><c>not-exists</c> — signal key does not exist</item>
    ///         <item><c>equals</c> — signal value equals <see cref="Value" /> (case-insensitive)</item>
    ///         <item><c>not-equals</c> — signal value does not equal <see cref="Value" /></item>
    ///         <item><c>true</c> — signal value is boolean true</item>
    ///         <item><c>false</c> — signal value is boolean false</item>
    ///         <item><c>any-true</c> — any of the comma-separated signals is boolean true</item>
    ///         <item><c>all-true</c> — all of the comma-separated signals are boolean true</item>
    ///         <item><c>gt</c> — signal numeric value &gt; <see cref="Value" /></item>
    ///         <item><c>lt</c> — signal numeric value &lt; <see cref="Value" /></item>
    ///         <item><c>gte</c> — signal numeric value &gt;= <see cref="Value" /></item>
    ///         <item><c>lte</c> — signal numeric value &lt;= <see cref="Value" /></item>
    ///         <item><c>contains</c> — signal value contains <see cref="Value" /> substring</item>
    ///     </list>
    /// </summary>
    [HtmlAttributeName("condition")]
    public string Condition { get; set; } = "exists";

    /// <summary>Comparison value for equals, not-equals, gt, lt, gte, lte, contains conditions.</summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    /// <summary>"show" (default) or "hide" when detection hasn't run.</summary>
    [HtmlAttributeName("fallback")]
    public string Fallback { get; set; } = "show";

    /// <summary>Invert the condition result.</summary>
    [HtmlAttributeName("negate")]
    public bool Negate { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            if (ShouldHideOnNoData()) output.SuppressOutput();
            return;
        }

        var signals = httpContext.GetSignals();
        if (signals.Count == 0)
        {
            if (ShouldHideOnNoData()) output.SuppressOutput();
            return;
        }

        var keys = Signal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0)
            return;

        var result = EvaluateCondition(signals, keys);
        if (Negate) result = !result;

        if (!result)
            output.SuppressOutput();
    }

    private bool EvaluateCondition(IReadOnlyDictionary<string, object> signals, string[] keys)
    {
        var cond = Condition.ToLowerInvariant().Trim();

        return cond switch
        {
            "exists" => keys.All(k => signals.ContainsKey(k)),
            "not-exists" => keys.All(k => !signals.ContainsKey(k)),
            "true" => keys.All(k => GetBool(signals, k) == true),
            "false" => keys.All(k => GetBool(signals, k) == false),
            "any-true" => keys.Any(k => GetBool(signals, k) == true),
            "all-true" => keys.All(k => GetBool(signals, k) == true),
            "equals" => keys.All(k => StringEquals(signals, k, Value)),
            "not-equals" => keys.All(k => !StringEquals(signals, k, Value)),
            "contains" => keys.Any(k => StringContains(signals, k, Value)),
            "gt" => keys.All(k => CompareNumeric(signals, k, Value) > 0),
            "lt" => keys.All(k => CompareNumeric(signals, k, Value) < 0),
            "gte" => keys.All(k => CompareNumeric(signals, k, Value) >= 0),
            "lte" => keys.All(k => CompareNumeric(signals, k, Value) <= 0),
            _ => keys.All(k => signals.ContainsKey(k)) // default to exists
        };
    }

    private bool ShouldHideOnNoData() =>
        string.Equals(Fallback, "hide", StringComparison.OrdinalIgnoreCase);

    private static bool? GetBool(IReadOnlyDictionary<string, object> signals, string key)
    {
        if (!signals.TryGetValue(key, out var val)) return null;
        if (val is bool b) return b;
        if (bool.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static bool StringEquals(IReadOnlyDictionary<string, object> signals, string key, string? expected)
    {
        if (!signals.TryGetValue(key, out var val)) return false;
        return string.Equals(val.ToString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringContains(IReadOnlyDictionary<string, object> signals, string key, string? substring)
    {
        if (string.IsNullOrEmpty(substring)) return false;
        if (!signals.TryGetValue(key, out var val)) return false;
        return val.ToString()?.Contains(substring, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static int CompareNumeric(IReadOnlyDictionary<string, object> signals, string key, string? expected)
    {
        if (!signals.TryGetValue(key, out var val)) return -2; // signal missing
        if (!double.TryParse(expected, out var expectedNum)) return -2;

        double actualNum;
        if (val is double d) actualNum = d;
        else if (val is int i) actualNum = i;
        else if (val is long l) actualNum = l;
        else if (val is float f) actualNum = f;
        else if (!double.TryParse(val.ToString(), out actualNum)) return -2;

        return actualNum.CompareTo(expectedNum);
    }
}
