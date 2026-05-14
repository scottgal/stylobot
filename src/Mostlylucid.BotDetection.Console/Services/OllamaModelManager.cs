using System.Text;
using System.Text.Json;
using SysConsole = System.Console;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Reaches into a local Ollama daemon to check that the requested model is
///     present, and pulls it if it is not. Writes live progress into a
///     <see cref="StartupStatusBoard"/> so operators can see the download happen
///     instead of staring at a quiet terminal.
///
///     Uses raw HTTP (not OllamaSharp) deliberately. The Console is published as
///     NativeAOT single-file -OllamaSharp's transitive Microsoft.Extensions.*
///     deps pull in versions that downgrade the AOT-pinned stack (NU1605). The
///     `/api/tags` + `/api/pull` surfaces are stable enough to hand-roll.
/// </summary>
public static class OllamaModelManager
{
    public static async Task EnsureModelAsync(
        string endpoint,
        string model,
        StartupStatusBoard board,
        bool autoPull = true,
        CancellationToken ct = default)
    {
        var baseUrl = endpoint.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Reachability check.
        board.Start("ollama-check", "Checking Ollama daemon", $"endpoint {baseUrl}");
        List<string> installedModels;
        try
        {
            var tags = await http.GetAsync($"{baseUrl}/api/tags", ct);
            if (!tags.IsSuccessStatusCode)
            {
                board.Fail("ollama-check", $"Ollama responded {(int)tags.StatusCode}");
                board.Skip("ollama-model", $"Model '{model}'", "skipped - Ollama not healthy");
                return;
            }

            var json = await tags.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            installedModels = doc.RootElement.TryGetProperty("models", out var arr)
                ? arr.EnumerateArray()
                     .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                     .Where(s => !string.IsNullOrEmpty(s))
                     .ToList()
                : new List<string>();
            board.Done("ollama-check", $"{installedModels.Count} model(s) cached locally");
        }
        catch (HttpRequestException)
        {
            board.Fail("ollama-check", "daemon not reachable - install from ollama.com or start `ollama serve`");
            board.Skip("ollama-model", $"Model '{model}'", "skipped - Ollama not reachable");
            return;
        }
        catch (Exception ex)
        {
            board.Fail("ollama-check", ex.Message);
            board.Skip("ollama-model", $"Model '{model}'", "skipped due to check failure");
            return;
        }

        // Presence check.
        var modelBase = model.Split(':')[0];
        var found = installedModels.Any(m =>
            m.Equals(model, StringComparison.OrdinalIgnoreCase)
            || m.StartsWith(modelBase + ":", StringComparison.OrdinalIgnoreCase)
            || m.Equals(modelBase, StringComparison.OrdinalIgnoreCase));

        if (found)
        {
            board.Skip("ollama-model", $"Model '{model}'", "already cached locally");
            return;
        }

        if (!autoPull)
        {
            board.Fail("ollama-model",
                $"Model '{model}' not cached. Run `ollama pull {model}` or restart with auto-pull enabled.");
            return;
        }

        // Pull with streamed progress.
        board.Start("ollama-model", $"Pulling '{model}' from Ollama registry", "starting");
        try
        {
            using var pullHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            // Hand-rolled JSON via Utf8JsonWriter so we never touch JsonSerializer's
            // reflection-based generic paths (RequiresUnreferencedCode / RequiresDynamicCode).
            // Utf8JsonWriter is the AOT-safe way to emit JSON without source generators.
            byte[] bodyBytes;
            using (var ms = new MemoryStream())
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("name", model);
                writer.WriteBoolean("stream", true);
                writer.WriteEndObject();
                writer.Flush();
                bodyBytes = ms.ToArray();
            }
            using var payload = new ByteArrayContent(bodyBytes);
            payload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await pullHttp.PostAsync(
                $"{baseUrl}/api/pull", payload, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var detail = ParsePullProgress(line);
                if (!string.IsNullOrEmpty(detail))
                    board.Start("ollama-model", $"Pulling '{model}'", detail);
            }
            board.Done("ollama-model", "pull complete");
        }
        catch (OperationCanceledException)
        {
            board.Fail("ollama-model", "pull cancelled");
            throw;
        }
        catch (Exception ex)
        {
            board.Fail("ollama-model", ex.Message);
        }
    }

    /// <summary>
    ///     Parse one JSONL frame from <c>/api/pull</c>. Ollama emits frames like:
    ///     <code>{"status":"downloading","digest":"sha256:...","total":12345,"completed":1000}</code>
    ///     or just <code>{"status":"pulling manifest"}</code>. Returns a short
    ///     human-readable detail string for the banner.
    /// </summary>
    private static string ParsePullProgress(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

            if (root.TryGetProperty("total", out var totalEl)
                && root.TryGetProperty("completed", out var completedEl)
                && totalEl.TryGetInt64(out var total)
                && completedEl.TryGetInt64(out var completed)
                && total > 0)
            {
                var percent = completed * 100.0 / total;
                return $"{status} {FormatBytes(completed)}/{FormatBytes(total)} ({percent:F0}%)";
            }

            return status;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1}GB",
        >= 1_000_000     => $"{bytes / 1_000_000.0:F0}MB",
        >= 1_000         => $"{bytes / 1_000.0:F0}KB",
        _                => $"{bytes}B"
    };
}
