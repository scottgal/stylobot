using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Universal content gate. Suppresses child content when conditions are not met.
///     All specified conditions AND together. If none set, always shows.
/// </summary>
/// <example>
///     <code>&lt;sb-gate human-only&gt;Premium content&lt;/sb-gate&gt;</code>
///     <code>&lt;sb-gate bot-type="SearchEngine" verified-only&gt;Structured data&lt;/sb-gate&gt;</code>
///     <code>&lt;sb-gate min-risk="Medium"&gt;Please verify&lt;/sb-gate&gt;</code>
/// </example>
[HtmlTargetElement("sb-gate")]
public class SbGateTagHelper : SbTagHelperBase
{
    public SbGateTagHelper(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
        : base(httpContextAccessor, extractor)
    {
    }

    /// <summary>Show only to humans (not bots).</summary>
    [HtmlAttributeName("human-only")]
    public bool HumanOnly { get; set; }

    /// <summary>Show only to bots.</summary>
    [HtmlAttributeName("bot-only")]
    public bool BotOnly { get; set; }

    /// <summary>Show when risk is at or above this band (VeryLow, Low, Elevated, Medium, High, VeryHigh).</summary>
    [HtmlAttributeName("min-risk")]
    public string? MinRisk { get; set; }

    /// <summary>Show when risk is at or below this band.</summary>
    [HtmlAttributeName("max-risk")]
    public string? MaxRisk { get; set; }

    /// <summary>Comma-separated bot types to match (SearchEngine, AiBot, Scraper, etc.).</summary>
    [HtmlAttributeName("bot-type")]
    public string? BotTypeFilter { get; set; }

    /// <summary>Show only to verified bots.</summary>
    [HtmlAttributeName("verified-only")]
    public bool VerifiedOnly { get; set; }

    /// <summary>"show" (default, fail-open) or "hide" (fail-closed) when detection hasn't run.</summary>
    [HtmlAttributeName("fallback")]
    public string Fallback { get; set; } = "show";

    /// <summary>Invert all conditions.</summary>
    [HtmlAttributeName("negate")]
    public bool Negate { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null; // Don't render wrapper element

        var model = GetModel();

        if (!model.HasData)
        {
            if (string.Equals(Fallback, "hide", StringComparison.OrdinalIgnoreCase))
                output.SuppressOutput();
            return;
        }

        var result = EvaluateConditions(model);
        if (Negate) result = !result;

        if (!result)
            output.SuppressOutput();
    }

    private bool EvaluateConditions(Models.DetectionDisplayModel model)
    {
        if (HumanOnly && model.IsBot)
            return false;

        if (BotOnly && !model.IsBot)
            return false;

        if (VerifiedOnly)
        {
            if (!model.IsBot)
                return false;
            var botType = ParseBotType(model.BotType);
            if (botType != BotType.VerifiedBot)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(BotTypeFilter))
        {
            var modelBotType = ParseBotType(model.BotType);
            var allowedTypes = BotTypeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matched = false;
            foreach (var t in allowedTypes)
            {
                if (Enum.TryParse<BotType>(t, ignoreCase: true, out var parsed) && parsed == modelBotType)
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return false;
        }

        var currentRisk = ParseRiskBandFromModel(model.RiskBand);

        if (!string.IsNullOrWhiteSpace(MinRisk))
        {
            var minBand = ParseRiskBand(MinRisk);
            if (currentRisk < minBand)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(MaxRisk))
        {
            var maxBand = ParseRiskBand(MaxRisk);
            if (currentRisk > maxBand)
                return false;
        }

        return true;
    }

    private static BotType ParseBotType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BotType.Unknown;
        return Enum.TryParse<BotType>(value, ignoreCase: true, out var t) ? t : BotType.Unknown;
    }
}
