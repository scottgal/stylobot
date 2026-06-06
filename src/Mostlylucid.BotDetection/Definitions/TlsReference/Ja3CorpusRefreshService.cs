using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     Periodically downloads a signed JA3 corpus envelope from
///     <see cref="TlsCorpusOptions.RefreshUrl"/>, verifies the Ed25519
///     signature under the configured public key, parses the YAML body,
///     and atomically replaces the live <see cref="Ja3ReferenceIndex"/>
///     contents. Refuses to start without a URL or public key configured.
///     <para>
///     Safety properties:
///     <list type="bullet">
///       <item>Response cap (<see cref="TlsCorpusOptions.MaxEnvelopeBytes"/>)
///         applied via <c>HttpClient.MaxResponseContentBufferSize</c> so a
///         malicious mirror can't OOM the host.</item>
///       <item>Refresh interval clamp: enforced minimum of 5 minutes; values
///         below that are silently raised so a misconfiguration can't hammer
///         a public mirror.</item>
///       <item>Verification before parse: YAML is only deserialised after
///         the signature validates -- a YAML parser bug can't be reached
///         via a forged envelope.</item>
///     </list>
///     </para>
/// </summary>
public sealed class Ja3CorpusRefreshService : BackgroundService
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly Ja3ReferenceIndex _index;
    private readonly Ja3CorpusEnvelopeVerifier _verifier;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Ja3CorpusRefreshService> _logger;

    public Ja3CorpusRefreshService(
        IOptionsMonitor<BotDetectionOptions> options,
        Ja3ReferenceIndex index,
        Ja3CorpusEnvelopeVerifier verifier,
        IHttpClientFactory httpClientFactory,
        ILogger<Ja3CorpusRefreshService> logger)
    {
        _options = options;
        _index = index;
        _verifier = verifier;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>HTTP client name registered in DI for this service.</summary>
    public const string HttpClientName = "stylobot.tls-corpus-refresh";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial guard: bail out cleanly with a warning if the operator
        // registered the service without a URL or key. We don't crash --
        // the embedded baseline corpus continues to serve.
        var opts = _options.CurrentValue.TlsCorpus;
        if (string.IsNullOrWhiteSpace(opts.RefreshUrl))
        {
            _logger.LogWarning("TLS corpus refresh enabled but RefreshUrl is empty; service idle");
            return;
        }
        if (opts.ResolvePublicKey() is null)
        {
            _logger.LogWarning(
                "TLS corpus refresh enabled but no public key resolvable (config + {EnvVar} both empty); service idle",
                TlsCorpusOptions.OverridePublicKeyEnvironmentVariable);
            return;
        }

        // First refresh runs immediately so the operator sees a fresh corpus
        // on boot rather than waiting RefreshInterval. Subsequent refreshes
        // honour the interval. Exceptions inside RefreshOnceAsync are caught
        // there; the loop keeps running.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(stoppingToken);
            var interval = _options.CurrentValue.TlsCorpus.RefreshInterval;
            if (interval < MinimumInterval) interval = MinimumInterval;
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    ///     Fetch once, verify, parse, swap. Public for tests; the running
    ///     service calls this from <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue.TlsCorpus;
        var url = opts.RefreshUrl;
        var publicKey = opts.ResolvePublicKey();
        if (string.IsNullOrWhiteSpace(url) || publicKey is null) return false;

        Ja3CorpusEnvelope? envelope;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.MaxResponseContentBufferSize = opts.MaxEnvelopeBytes;
            envelope = await client.GetFromJsonAsync<Ja3CorpusEnvelope>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS corpus refresh: download from {Url} failed", url);
            return false;
        }

        if (envelope is null)
        {
            _logger.LogWarning("TLS corpus refresh: empty envelope from {Url}", url);
            return false;
        }

        if (!_verifier.Verify(envelope, publicKey))
        {
            _logger.LogWarning("TLS corpus refresh: signature verification failed for envelope from {Url}", url);
            return false;
        }

        Ja3CorpusFile? corpus;
        try
        {
            corpus = YamlSerializer.Deserialize<Ja3CorpusFile>(System.Text.Encoding.UTF8.GetBytes(envelope.CorpusYaml));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS corpus refresh: YAML parse failed for envelope from {Url}", url);
            return false;
        }

        if (corpus is null) return false;

        _index.ReplaceCorpus(corpus);
        _logger.LogInformation(
            "TLS corpus refreshed from {Url} (envelope generated {GeneratedAt}, key id {KeyId})",
            url, envelope.GeneratedAt, envelope.KeyId);
        return true;
    }
}
