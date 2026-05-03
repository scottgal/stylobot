namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Options for API-mode detection data extraction.
///     When configured, <see cref="Services.DetectionDataExtractor"/> will call the StyloBot API
///     to retrieve detection results when neither inline middleware (HttpContext.Items) nor
///     YARP headers are present on the request.
/// </summary>
/// <remarks>
///     Register via <c>services.AddStyloBotApiMode("http://gateway:8080/api/v1/me")</c>.
///     The endpoint should return a <c>DetectionDisplayModel</c>-compatible JSON response.
/// </remarks>
public record DetectionApiOptions
{
    /// <summary>
    ///     The URL of the StyloBot API detection endpoint.
    ///     Typically <c>http://gateway:8080/api/v1/me</c> or <c>http://localhost:8080/api/v1/me</c>.
    /// </summary>
    public string? Endpoint { get; set; }
}
