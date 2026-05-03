using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Base class for all sb-* content gating TagHelpers.
///     Provides access to detection data via <see cref="DetectionDataExtractor" />.
/// </summary>
public abstract class SbTagHelperBase : TagHelper
{
    private readonly DetectionDataExtractor _extractor;
    private readonly IHttpContextAccessor _httpContextAccessor;

    protected SbTagHelperBase(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
    {
        _httpContextAccessor = httpContextAccessor;
        _extractor = extractor;
    }

    protected DetectionDisplayModel GetModel()
    {
        var context = _httpContextAccessor.HttpContext;
        return context != null ? _extractor.Extract(context) : new DetectionDisplayModel();
    }

    protected bool HasDetectionData()
    {
        return GetModel().HasData;
    }

    protected static RiskBand ParseRiskBand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return RiskBand.Unknown;
        return Enum.TryParse<RiskBand>(value, ignoreCase: true, out var band) ? band : RiskBand.Unknown;
    }

    protected static RiskBand ParseRiskBandFromModel(string? modelValue)
    {
        return ParseRiskBand(modelValue);
    }
}
