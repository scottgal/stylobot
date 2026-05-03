using System.Text;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.Demo.Services;
using Mostlylucid.BotDetection.Orchestration;
using StoredSignature = Mostlylucid.BotDetection.Demo.Services.StoredSignature;

namespace Mostlylucid.BotDetection.Demo.TagHelpers;

/// <summary>
///     TagHelper for displaying bot detection signature information.
///     Usage:
///     &lt;bot-signature-display signature-id="@ViewData["SignatureId"]" /&gt;
///     Or for current request:
///     &lt;bot-signature-display mode="current" /&gt;
/// </summary>
[HtmlTargetElement("bot-signature-display")]
public class BotSignatureDisplayTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SignatureStore _signatureStore;

    public BotSignatureDisplayTagHelper(
        SignatureStore signatureStore,
        IHttpContextAccessor httpContextAccessor)
    {
        _signatureStore = signatureStore;
        _httpContextAccessor = httpContextAccessor;
    }

    [HtmlAttributeName("signature-id")] public string? SignatureId { get; set; }

    [HtmlAttributeName("mode")] public string Mode { get; set; } = "id"; // "id" or "current"

    [HtmlAttributeName("show-headers")] public bool ShowHeaders { get; set; } = true;

    [HtmlAttributeName("show-contributions")]
    public bool ShowContributions { get; set; } = true;

    [HtmlAttributeName("show-signals")] public bool ShowSignals { get; set; } = false;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.Attributes.SetAttribute("class", "bot-signature-display");

        StoredSignature? signature = null;
        Dictionary<string, string>? headers = null;

        // Get signature based on mode
        if (Mode == "current")
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext != null)
            {
                if (httpContext.Request.Headers.TryGetValue("X-Signature-ID", out var signatureIdHeader))
                {
                    SignatureId = signatureIdHeader.ToString();
                    signature = _signatureStore.GetSignature(SignatureId);
                }

                // Extract headers
                headers = ExtractBotDetectionHeaders(httpContext);
            }
        }
        else if (!string.IsNullOrEmpty(SignatureId))
        {
            signature = _signatureStore.GetSignature(SignatureId);
        }

        if (signature == null)
        {
            output.Content.SetHtmlContent(
                $"<div class='alert alert-warning'>Signature not found: {SignatureId ?? "none"}</div>");
            return;
        }

        // Build HTML display
        var html = new StringBuilder();

        html.AppendLine("<div class='card bot-signature-card'>");
        html.AppendLine("<div class='card-header'>");
        html.AppendLine($"<h5>Bot Detection Signature: <code>{signature.SignatureId}</code></h5>");
        html.AppendLine($"<small class='text-muted'>Detected at {signature.Timestamp:yyyy-MM-dd HH:mm:ss} UTC</small>");
        html.AppendLine("</div>");

        html.AppendLine("<div class='card-body'>");

        // Detection Result
        html.AppendLine("<div class='row mb-3'>");
        html.AppendLine("<div class='col-md-6'>");
        html.AppendLine("<h6>Detection Result</h6>");
        html.AppendLine(
            $"<p><strong>Bot Probability:</strong> <span class='badge bg-{GetProbabilityColor(signature.Evidence.BotProbability)}'>{signature.Evidence.BotProbability:P1}</span></p>");
        html.AppendLine($"<p><strong>Confidence:</strong> {signature.Evidence.Confidence:P1}</p>");
        html.AppendLine(
            $"<p><strong>Risk Band:</strong> <span class='badge bg-{GetRiskBandColor(signature.Evidence.RiskBand)}'>{signature.Evidence.RiskBand}</span></p>");

        if (signature.Evidence.PrimaryBotType.HasValue)
            html.AppendLine($"<p><strong>Bot Type:</strong> {signature.Evidence.PrimaryBotType}</p>");

        if (!string.IsNullOrEmpty(signature.Evidence.PrimaryBotName))
            html.AppendLine($"<p><strong>Bot Name:</strong> {signature.Evidence.PrimaryBotName}</p>");

        html.AppendLine("</div>");

        // Request Info
        html.AppendLine("<div class='col-md-6'>");
        html.AppendLine("<h6>Request Information</h6>");
        html.AppendLine($"<p><strong>Path:</strong> <code>{signature.RequestMetadata.Path}</code></p>");
        html.AppendLine($"<p><strong>Method:</strong> {signature.RequestMetadata.Method}</p>");
        html.AppendLine($"<p><strong>IP:</strong> {signature.RequestMetadata.RemoteIp}</p>");
        html.AppendLine($"<p><strong>Protocol:</strong> {signature.RequestMetadata.Protocol}</p>");
        html.AppendLine(
            $"<p><strong>User-Agent:</strong><br/><small>{signature.RequestMetadata.UserAgent}</small></p>");
        html.AppendLine("</div>");
        html.AppendLine("</div>");

        // Headers
        if (ShowHeaders && (headers?.Any() == true || signature.RequestMetadata.Headers.Any()))
        {
            html.AppendLine("<div class='mb-3'>");
            html.AppendLine("<h6>Detection Headers</h6>");
            html.AppendLine("<table class='table table-sm table-striped'>");
            html.AppendLine("<thead><tr><th>Header</th><th>Value</th></tr></thead>");
            html.AppendLine("<tbody>");

            var allHeaders = headers ?? signature.RequestMetadata.Headers;
            foreach (var header in allHeaders.OrderBy(h => h.Key))
                html.AppendLine($"<tr><td><code>{header.Key}</code></td><td>{header.Value}</td></tr>");

            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");
        }

        // Contributions
        if (ShowContributions && signature.Evidence.Contributions.Any())
        {
            html.AppendLine("<div class='mb-3'>");
            html.AppendLine("<h6>Detector Contributions</h6>");
            html.AppendLine("<table class='table table-sm'>");
            html.AppendLine(
                "<thead><tr><th>Detector</th><th>Category</th><th>Δ Confidence</th><th>Weight</th><th>Impact</th><th>Reason</th></tr></thead>");
            html.AppendLine("<tbody>");

            foreach (var contrib in signature.Evidence.Contributions
                         .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight)))
            {
                var impact = contrib.ConfidenceDelta * contrib.Weight;
                var impactColor = impact > 0.3 ? "danger" :
                    impact > 0.1 ? "warning" :
                    impact < -0.1 ? "success" : "secondary";

                html.AppendLine("<tr>");
                html.AppendLine($"<td><strong>{contrib.DetectorName}</strong></td>");
                html.AppendLine($"<td><span class='badge bg-secondary'>{contrib.Category}</span></td>");
                html.AppendLine(
                    $"<td class='text-{(contrib.ConfidenceDelta > 0 ? "danger" : "success")}'>{contrib.ConfidenceDelta:+0.00;-0.00;0.00}</td>");
                html.AppendLine($"<td>{contrib.Weight:F2}</td>");
                html.AppendLine($"<td><span class='badge bg-{impactColor}'>{impact:+0.00;-0.00;0.00}</span></td>");
                html.AppendLine($"<td><small>{contrib.Reason}</small></td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</tbody></table>");
            html.AppendLine("</div>");
        }

        // Signals
        if (ShowSignals)
        {
            html.AppendLine("<details class='mb-3'>");
            html.AppendLine("<summary><h6>Signals (Debug)</h6></summary>");
            html.AppendLine("<pre class='bg-light p-2'>");

            foreach (var contrib in signature.Evidence.Contributions)
                if (contrib.Signals.Any())
                {
                    html.AppendLine($"[{contrib.DetectorName}]");
                    foreach (var signal in contrib.Signals) html.AppendLine($"  {signal.Key} = {signal.Value}");
                }

            html.AppendLine("</pre>");
            html.AppendLine("</details>");
        }

        html.AppendLine("</div>"); // card-body
        html.AppendLine("</div>"); // card

        output.Content.SetHtmlContent(html.ToString());
    }

    private static string GetProbabilityColor(double prob)
    {
        return prob switch
        {
            >= 0.8 => "danger",
            >= 0.5 => "warning",
            _ => "success"
        };
    }

    private static string GetRiskBandColor(RiskBand band)
    {
        return band switch
        {
            RiskBand.VeryHigh => "danger",
            RiskBand.High => "danger",
            RiskBand.Medium => "warning",
            RiskBand.Elevated => "info",
            RiskBand.Low => "success",
            RiskBand.VeryLow => "success",
            _ => "secondary"
        };
    }

    private Dictionary<string, string> ExtractBotDetectionHeaders(HttpContext context)
    {
        var headers = new Dictionary<string, string>();

        foreach (var header in context.Request.Headers)
        {
            var key = header.Key.ToLowerInvariant();
            if (key.StartsWith("x-bot-") || key.StartsWith("x-tls-") ||
                key.StartsWith("x-tcp-") || key.StartsWith("x-http-") ||
                key == "x-signature-id")
                headers[header.Key] = header.Value.ToString();
        }

        return headers;
    }
}