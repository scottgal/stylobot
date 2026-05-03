using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Auth;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "StyloBotApiKey";
    private const string HeaderName = "X-SB-Api-Key";

    private readonly IApiKeyStore _apiKeyStore;

    public ApiKeyAuthenticationHandler(
        IApiKeyStore apiKeyStore,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _apiKeyStore = apiKeyStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var apiKeyHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var apiKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        var (result, rejection) = _apiKeyStore.ValidateKeyWithReason(apiKey, Request.Path.Value ?? "/");

        if (result is null)
        {
            var reason = rejection?.Reason.ToString() ?? "Unknown";
            return Task.FromResult(AuthenticateResult.Fail($"API key rejected: {reason}"));
        }

        var claims = new List<Claim>
        {
            new("api_key_name", result.Context.KeyName),
            new("api_key_id", result.KeyId),
            new(ClaimTypes.AuthenticationMethod, SchemeName)
        };
        foreach (var tag in result.Context.Tags)
            claims.Add(new Claim("api_key_tag", tag));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        Context.Items["BotDetection.ApiKeyContext"] = result.Context;

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
