using System.Net;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Forwarder;

namespace Mostlylucid.BotDetection.Demo.Controllers;

/// <summary>
///     Custom proxy controller for dynamic URL forwarding with bot detection
/// </summary>
[ApiController]
public class ProxyController : ControllerBase
{
    private readonly IHttpForwarder _forwarder;
    private readonly ILogger<ProxyController> _logger;

    public ProxyController(IHttpForwarder forwarder, ILogger<ProxyController> logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    /// <summary>
    ///     Proxy any URL dynamically through YARP with bot detection
    /// </summary>
    /// <param name="url">The full URL path to proxy (e.g., www.mostlylucid.net/page or example.com)</param>
    [HttpGet("proxy/{**url}")]
    public async Task ProxyRequest(string url)
    {
        // Normalize URL - add https:// if no protocol specified
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;

        // Parse the URL to extract the destination
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destinationUri))
        {
            _logger.LogWarning("Invalid proxy URL: {Url}", url);
            Response.StatusCode = 400;
            await Response.WriteAsync("Invalid URL format");
            return;
        }

        _logger.LogInformation("Proxying request to: {Url}", destinationUri);

        // Create HTTP client for the destination
        var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false
        });

        // Create forwarder request config
        var requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(100)
        };

        // Create a transformer to rewrite the path
        var transformer = new CustomProxyTransformer(destinationUri);

        // Forward the request to the destination
        var error = await _forwarder.SendAsync(
            HttpContext,
            destinationUri.Scheme + "://" + destinationUri.Authority,
            httpClient,
            requestConfig,
            transformer);

        // Check for errors
        if (error != ForwarderError.None)
        {
            var errorFeature = HttpContext.Features.Get<IForwarderErrorFeature>();
            var exception = errorFeature?.Exception;

            _logger.LogError(exception, "Proxy error {Error} for URL: {Url}", error, destinationUri);
        }
    }
}

/// <summary>
///     Custom transformer to properly rewrite the request path for proxied URLs
/// </summary>
internal class CustomProxyTransformer : HttpTransformer
{
    private readonly Uri _destinationUri;

    public CustomProxyTransformer(Uri destinationUri)
    {
        _destinationUri = destinationUri;
    }

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        // Call base transformation first
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        // Override the request URI with the full destination URI (including path and query)
        proxyRequest.RequestUri = new Uri(_destinationUri, _destinationUri.PathAndQuery);

        // Ensure Host header matches destination
        proxyRequest.Headers.Host = _destinationUri.Host;
    }
}