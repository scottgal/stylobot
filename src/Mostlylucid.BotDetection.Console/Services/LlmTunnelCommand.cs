using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Llm;
using Mostlylucid.BotDetection.Llm.Ollama;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Mostlylucid.BotDetection.Llm.Tunnel.Extensions;

namespace Mostlylucid.BotDetection.Console.Services;

public static class LlmTunnelCommand
{
    public static async Task<int> RunAsync(string[] cmdArgs)
    {
        // ── Parse arguments ────────────────────────────────────────────────
        // llmtunnel [cloudflare-token] [--ollama <url>] [--models <csv>]
        //           [--max-concurrency <n>] [--max-context <tokens>]
        //           [--agent-port <port>]

        string? tunnelToken = null;
        string ollamaUrl = "http://127.0.0.1:11434";
        List<string> allowedModels = [];
        int maxConcurrency = 2;
        int maxContext = 8192;
        int agentPort = 0; // 0 = random loopback port

        // Find the llmtunnel positional (the command itself) and parse what follows
        int startIndex = 1; // skip exe name
        for (int i = startIndex; i < cmdArgs.Length; i++)
        {
            var a = cmdArgs[i];
            if (a.Equals("llmtunnel", StringComparison.OrdinalIgnoreCase)
                || a.Equals("-llmtunnel", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--llm-tunnel", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = i + 1;
                break;
            }
        }

        for (int i = startIndex; i < cmdArgs.Length; i++)
        {
            var a = cmdArgs[i];
            switch (a.ToLowerInvariant())
            {
                case "--ollama":
                    if (i + 1 < cmdArgs.Length) ollamaUrl = cmdArgs[++i];
                    break;
                case "--models":
                    if (i + 1 < cmdArgs.Length)
                        allowedModels = cmdArgs[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    break;
                case "--max-concurrency":
                    if (i + 1 < cmdArgs.Length) int.TryParse(cmdArgs[++i], out maxConcurrency);
                    break;
                case "--max-context":
                    if (i + 1 < cmdArgs.Length) int.TryParse(cmdArgs[++i], out maxContext);
                    break;
                case "--agent-port":
                    if (i + 1 < cmdArgs.Length) int.TryParse(cmdArgs[++i], out agentPort);
                    break;
                default:
                    // First non-option positional = Cloudflare named tunnel token
                    if (!a.StartsWith('-') && tunnelToken is null)
                        tunnelToken = a;
                    break;
            }
        }

        // ── Probe Ollama ───────────────────────────────────────────────────
        System.Console.WriteLine("Probing local Ollama instance...");

        // Bootstrap a minimal DI container just for the probe logger
        var probeServices = new ServiceCollection();
        probeServices.AddLogging();
        var probeSp = probeServices.BuildServiceProvider();

        var probe = new LocalLlmProviderProbe(
            probeSp.GetRequiredService<ILogger<LocalLlmProviderProbe>>());

        var probeResult = await probe.ProbeAsync(ollamaUrl, allowedModels, maxContext);

        if (!probeResult.IsReady)
        {
            System.Console.Error.WriteLine($"Ollama unavailable: {probeResult.Error}");
            System.Console.Error.WriteLine("Start Ollama first, or pass --allow-unready to continue anyway.");
            return 1;
        }

        var inventory = probeResult.Inventory!;
        if (inventory.Models.Count == 0)
        {
            System.Console.Error.WriteLine("No models found. Use --models <csv> to specify allowed models explicitly.");
            return 1;
        }

        System.Console.WriteLine($"Found {inventory.Models.Count} model(s):");
        foreach (var m in inventory.Models)
            System.Console.WriteLine($"  - {m.Id}");

        // ── Generate node identity ─────────────────────────────────────────
        var nodeId = "llmn_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var keyId = "k_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var signingSecret = LocalLlmTunnelCrypto.GenerateSigningSecret();
        var signingSecretB64 = LocalLlmTunnelCrypto.ToBase64Url(signingSecret);
        var agentPublicKeyB64 = LocalLlmTunnelCrypto.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var nodeName = Environment.MachineName.ToLowerInvariant();

        // ── Build Kestrel agent host ───────────────────────────────────────
        var agentContext = new LocalLlmAgentContext
        {
            NodeId = nodeId,
            KeyId = keyId,
            SigningSecret = signingSecret,
            Provider = "ollama",
            ModelInventory = inventory,
            MaxConcurrency = maxConcurrency,
            MaxContext = maxContext
        };

        // Use the first model found as the default model for the provider
        var defaultModel = inventory.Models.First().Id;

        var builder = WebApplication.CreateBuilder();
        // Bind to loopback only; port 0 = OS assigns a free port (no TOCTOU race)
        if (agentPort > 0)
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, agentPort));
        else
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        // Register our AOT-safe source-gen context so Results.Ok() and body deserialization work
        builder.Services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.TypeInfoResolverChain.Insert(0, TunnelJsonContext.Default));
        builder.Services.AddLocalLlmTunnelAgent(agentContext);

        // Register OllamaLlmProvider (already referenced by console project) via DI
        builder.Services.AddHttpClient("stylobot-ollama");
        builder.Services.Configure<OllamaProviderOptions>(opts =>
        {
            opts.Endpoint = ollamaUrl;
            opts.Model = defaultModel;
        });
        builder.Services.AddSingleton<ILlmProvider, OllamaLlmProvider>();

        var agentApp = builder.Build();
        agentApp.MapLocalLlmAgentEndpoints();

        await agentApp.StartAsync();

        // Read the actual bound port from Kestrel (works correctly with port 0)
        var serverFeatures = agentApp.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features;
        var addresses = serverFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var boundUrl = addresses?.Addresses.FirstOrDefault() ?? $"http://127.0.0.1:{agentPort}";
        var actualPort = new Uri(boundUrl).Port;

        System.Console.WriteLine($"Agent listening on http://127.0.0.1:{actualPort}");

        // ── Launch Cloudflare tunnel ───────────────────────────────────────
        System.Console.WriteLine("Starting Cloudflare tunnel...");
        var tunnelResult = await CloudflaredTunnelLauncher.LaunchAndWaitAsync(
            actualPort, "http", tunnelToken, urlDiscoveryTimeoutMs: 30_000);

        if (!tunnelResult.IsReady || (tunnelToken is null && tunnelResult.TunnelUrl is null))
        {
            System.Console.Error.WriteLine($"Failed to start tunnel: {tunnelResult.DiagnosticsError}");
            await agentApp.StopAsync();
            return 1;
        }

        // For named tunnels the URL isn't captured from stderr; inform user it comes from Cloudflare dashboard
        var tunnelUrl = tunnelResult.TunnelUrl ?? $"https://<your-named-tunnel-url>";
        var tunnelKind = tunnelToken is not null ? "cloudflare-named" : "cloudflare-quick";

        // ── Generate and print connection key ──────────────────────────────
        var payload = new LlmTunnelConnectionPayload
        {
            NodeId = nodeId,
            NodeName = nodeName,
            TunnelKind = tunnelKind,
            TunnelUrl = tunnelUrl,
            AgentPublicKey = agentPublicKeyB64,
            ControllerSharedSecret = signingSecretB64,
            KeyId = keyId,
            Provider = "ollama",
            Models = inventory.Models.Select(m => m.Id).ToList(),
            SupportsStreaming = false,
            MaxConcurrency = maxConcurrency,
            MaxContext = maxContext,
            CreatedAt = DateTime.UtcNow
        };

        var connectionKey = new LlmTunnelConnectionKey { Payload = payload }.Encode();

        System.Console.WriteLine();
        System.Console.WriteLine($"Tunnel: active ({tunnelKind})");
        System.Console.WriteLine($"Node:   {nodeName} ({nodeId})");
        System.Console.WriteLine($"Models: {string.Join(", ", payload.Models)}");
        System.Console.WriteLine();

        if (tunnelKind == "cloudflare-quick")
        {
            System.Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            System.Console.WriteLine("║  ANONYMOUS TUNNEL — KEY IS EPHEMERAL                            ║");
            System.Console.WriteLine("║  This key changes every time the process restarts with a new     ║");
            System.Console.WriteLine("║  tunnel URL. You must re-import the key after each restart.      ║");
            System.Console.WriteLine("║  Use a named Cloudflare tunnel token for a stable key.           ║");
            System.Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
            System.Console.WriteLine();
        }

        System.Console.WriteLine("Connection key (paste into Stylobot UI or FOSS config):");
        System.Console.WriteLine();
        System.Console.WriteLine(connectionKey);
        System.Console.WriteLine();
        System.Console.WriteLine("FOSS config key: BotDetection:AiDetection:LocalTunnel:ConnectionKey");
        System.Console.WriteLine();

        if (tunnelKind == "cloudflare-named")
            System.Console.WriteLine("[INFO] Named tunnel: this key is stable across restarts while the Cloudflare token is valid.");

        System.Console.WriteLine("Press Ctrl+C to stop.");

        // ── Keep alive until Ctrl+C ────────────────────────────────────────
        var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        System.Console.WriteLine("Shutting down...");

        if (tunnelKind == "cloudflare-quick")
        {
            System.Console.WriteLine();
            System.Console.WriteLine("[WARN] Anonymous tunnel stopped. The connection key above is now invalid.");
            System.Console.WriteLine("[WARN] You must run 'stylobot llmtunnel' again and re-import the new key.");
        }

        // Kill cloudflared
        try { tunnelResult.Process.Kill(); } catch { /* ignore */ }
        try { tunnelResult.Process.Dispose(); } catch { /* ignore */ }

        await agentApp.StopAsync();
        return 0;
    }

}
