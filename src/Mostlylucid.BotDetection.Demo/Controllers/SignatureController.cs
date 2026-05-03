using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Demo.Services;

namespace Mostlylucid.BotDetection.Demo.Controllers;

/// <summary>
///     API controller for retrieving bot detection signatures.
///     Used by the demo UI to display signature details.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SignatureController : ControllerBase
{
    private readonly ILogger<SignatureController> _logger;
    private readonly SignatureStore _signatureStore;

    public SignatureController(SignatureStore signatureStore, ILogger<SignatureController> logger)
    {
        _signatureStore = signatureStore;
        _logger = logger;
    }

    /// <summary>
    ///     Get a signature by ID.
    ///     This is called by YARP-proxied apps to retrieve full signature data
    ///     based on the X-Signature-ID header passed through.
    /// </summary>
    [HttpGet("{signatureId}")]
    [ProducesResponseType(typeof(StoredSignature), 200)]
    [ProducesResponseType(404)]
    public ActionResult<StoredSignature> GetSignature(string signatureId)
    {
        var signature = _signatureStore.GetSignature(signatureId);

        if (signature == null)
        {
            _logger.LogWarning("Signature not found: {SignatureId}", signatureId);
            return NotFound(new { error = $"Signature '{signatureId}' not found" });
        }

        _logger.LogTrace("Retrieved signature: {SignatureId}", signatureId);
        return Ok(signature);
    }

    /// <summary>
    ///     Get recent signatures
    /// </summary>
    [HttpGet("recent")]
    [ProducesResponseType(typeof(List<StoredSignature>), 200)]
    public ActionResult<List<StoredSignature>> GetRecentSignatures([FromQuery] int count = 50)
    {
        if (count < 1 || count > 1000) return BadRequest(new { error = "Count must be between 1 and 1000" });

        var signatures = _signatureStore.GetRecentSignatures(count);
        return Ok(signatures);
    }

    /// <summary>
    ///     Get signature store statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(SignatureStoreStats), 200)]
    public ActionResult<SignatureStoreStats> GetStats()
    {
        var stats = _signatureStore.GetStats();
        return Ok(stats);
    }

    /// <summary>
    ///     Get signature from current request headers.
    ///     This endpoint reads the X-Signature-ID header passed by YARP
    ///     and returns the full signature data.
    /// </summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(SignatureResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<SignatureResponse> GetCurrentSignature()
    {
        // Check for signature ID in headers (passed by YARP)
        if (!Request.Headers.TryGetValue("X-Signature-ID", out var signatureIdHeader))
            return NotFound(new { error = "No X-Signature-ID header found in request" });

        var signatureId = signatureIdHeader.ToString();
        var signature = _signatureStore.GetSignature(signatureId);

        if (signature == null)
        {
            _logger.LogWarning("Current signature not found: {SignatureId}", signatureId);
            return NotFound(new { error = $"Signature '{signatureId}' not found" });
        }

        // Also include all bot detection headers for display
        var botHeaders = ExtractBotDetectionHeaders();

        var response = new SignatureResponse
        {
            Signature = signature,
            DetectionHeaders = botHeaders
        };

        _logger.LogTrace("Retrieved current signature: {SignatureId}", signatureId);
        return Ok(response);
    }

    private Dictionary<string, string> ExtractBotDetectionHeaders()
    {
        var headers = new Dictionary<string, string>();

        foreach (var header in Request.Headers)
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

/// <summary>
///     Response containing signature and detection headers
/// </summary>
public class SignatureResponse
{
    public required StoredSignature Signature { get; init; }
    public required Dictionary<string, string> DetectionHeaders { get; init; }
}