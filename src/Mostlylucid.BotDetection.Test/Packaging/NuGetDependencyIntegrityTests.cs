using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Packaging;

/// <summary>
///     Regression guard for the dangling-dependency bug (issue #124).
///     <para>
///         The published <c>Mostlylucid.BotDetection.UI</c> nuspec once hard-depended on
///         <c>Mostlylucid.BotDetection.PrometheusPack</c> + <c>Mostlylucid.BotDetection.OpenApi</c>
///         at the same version while NEITHER package existed on nuget.org -- so every
///         <c>dotnet add package mostlylucid.botdetection.ui</c> failed with NU1101 for every
///         customer. This test packs the real projects into a temp feed and asserts the UI
///         nuspec's declared dependency graph:
///         <list type="bullet">
///             <item>declares core + OpenApi (its real hard dependencies);</item>
///             <item>does NOT declare PrometheusPack (Prometheus is an optional add-on pack
///             that now owns its widget surface and references UI, not the reverse);</item>
///             <item>every declared dependency has a matching .nupkg packed alongside it
///             (no dangling deps).</item>
///         </list>
///     </para>
///     <para>
///         Slow by design (build and pack the publish closure, then publish and run three consumers) and deliberately kept in
///         the default CI gate so a future dependency-graph regression fails the build the
///         day it is introduced, not at the next release.
///     </para>
/// </summary>
public sealed class NuGetDependencyIntegrityTests
{
    // A fixed version can restore an OLD first-party package from the global
    // NuGet cache instead of the artifacts just packed by this test.
    private static readonly string TestVersion = "9.9.9-test.g" + Guid.NewGuid().ToString("N");

    /// <summary>
    ///     Pack the SAME configuration this test assembly was built in (2026-09-12).
    ///     <para>
    ///     This was hard-coded to <c>Release</c>, which made the test pass only where a Release
    ///     build had already happened — CI builds Release before testing, so CI was green, but a
    ///     plain <c>dotnet test</c> is Debug, <c>bin/Release/&lt;assembly&gt;.dll</c> does not exist,
    ///     and pack died with NU5026 ("the file to be packed was not found on disk") for a reason
    ///     that has nothing to do with the nuspec dependency graph this test asserts.
    ///     </para>
    ///     <para>
    ///     That is an instrument defect of the familiar shape: a test that fails for a reason it
    ///     does not claim to test, so its red misleads every developer and agent who runs the
    ///     default suite. It was carried as a "pre-existing environmental failure" for days. The
    ///     dependency graph is configuration-independent, so packing whatever configuration the
    ///     suite is actually running is both correct and sufficient.
    ///     </para>
    /// </summary>
#if DEBUG
    private const string PackConfiguration = "Debug";
#else
    private const string PackConfiguration = "Release";
#endif

    // The FULL first-party publish closure: every packable project + the referenced
    // published projects (Common, Llm). A dangling dep anywhere in this set (a nuspec
    // declaring a co-packable first-party package with no matching nupkg) reproduces the
    // issue #124 NU1101 class for consumers of THAT package -- e.g. the 8.12.0 Api ->
    // Llm.Tunnel dangling dep this test now catches.
    private static readonly string[] PackedProjects =
    {
        "src/Mostlylucid.Common/Mostlylucid.Common.csproj",
        "src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj",
        "src/Mostlylucid.BotDetection.Dashboard.Contracts/Mostlylucid.BotDetection.Dashboard.Contracts.csproj",
        "src/Mostlylucid.BotDetection.Dashboard.Data/Mostlylucid.BotDetection.Dashboard.Data.csproj",
        "src/Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj",
        "src/Mostlylucid.BotDetection.Llm/Mostlylucid.BotDetection.Llm.csproj",
        "src/Mostlylucid.BotDetection.Llm.Holodeck/Mostlylucid.BotDetection.Llm.Holodeck.csproj",
        "src/Mostlylucid.BotDetection.Llm.Tunnel/Mostlylucid.BotDetection.Llm.Tunnel.csproj",
        "src/Mostlylucid.BotDetection.OpenApi/Mostlylucid.BotDetection.OpenApi.csproj",
        "src/Mostlylucid.BotDetection.PrometheusPack/Mostlylucid.BotDetection.PrometheusPack.csproj",
        "src/Mostlylucid.BotDetection.StyloExtract/Mostlylucid.BotDetection.StyloExtract.csproj",
        "src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj",
        "src/Mostlylucid.GeoDetection/Mostlylucid.GeoDetection.csproj",
    };

    // Package ids as they appear in the nupkg FILENAMES (NuGet lowercases the core package
    // id; the others keep the PascalCase id). Used by ReadNuspec / the description guard.
    private static readonly string[] PackedPackageIds =
    {
        "mostlylucid.botdetection",
        "Mostlylucid.BotDetection.Api",
        "Mostlylucid.BotDetection.Dashboard.Contracts",
        "Mostlylucid.BotDetection.Dashboard.Data",
        "Mostlylucid.BotDetection.Llm",
        "Mostlylucid.BotDetection.Llm.Holodeck",
        "Mostlylucid.BotDetection.Llm.Tunnel",
        "Mostlylucid.BotDetection.OpenApi",
        "Mostlylucid.BotDetection.PrometheusPack",
        "Mostlylucid.BotDetection.StyloExtract",
        "Mostlylucid.BotDetection.UI",
        "Mostlylucid.Common",
        "Mostlylucid.GeoDetection",
    };

    /// <summary>
    ///     True for a first-party dependency that is itself CO-PACKED at the release version.
    ///     External first-party packages that come from nuget.org at their own published
    ///     versions -- Ephemeral atoms, StyloFlow, Notify, StyloExtract.AspNetCore (2.x) --
    ///     are NOT co-packed and are out of scope for the co-existence check. A dep is
    ///     co-packable iff its id matches one of the packages in this test's pack closure.
    /// </summary>
    private static bool IsCoPackableFirstParty(string id)
        => PackedPackageIds.Any(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));

    // NuGet lowercases the PackageId of some packages in the nupkg filename (core ->
    // mostlylucid.botdetection, GeoDetection -> mostlylucid.geodetection). The feed can
    // hold mixed casing (Mostlylucid.Common keeps its casing), so lookups MUST be
    // case-insensitive -- CI is Linux (case-sensitive FS), where an exact-case File.Exists
    // silently misses a lowercase filename.
    private static string? FindNupkg(string packDir, string packageId)
        => Directory.EnumerateFiles(packDir, $"*.{TestVersion}.nupkg")
            .FirstOrDefault(f => string.Equals(
                Path.GetFileName(f),
                $"{packageId}.{TestVersion}.nupkg",
                StringComparison.OrdinalIgnoreCase));

    private static bool NupkgExists(string packDir, string packageId)
        => FindNupkg(packDir, packageId) is not null;

    [Fact]
    public async Task Packed_packages_restore_publish_and_run_ASPNET_consumers()
    {
        var repoRoot = FindRepoRoot();
        var packDir = Path.Combine(Path.GetTempPath(), "sb-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packDir);

        try
        {
            foreach (var project in PackedProjects)
            {
                var csproj = Path.Combine(repoRoot, project);
                await DotnetPackAsync(csproj, packDir, repoRoot);
            }

            // Comprehensive dangling-dep guard: for EVERY co-packed package, every first-party
            // dependency that is itself co-packable must have a matching nupkg in the output.
            // This is what catches the Api -> Llm.Tunnel dangling dep (found on 8.12.0) and any
            // future recurrence across ALL packages, not just UI/PrometheusPack. NuGet emits
            // dep ids lowercase; the nupkg filenames preserve the id's casing, so compare
            // case-insensitively.
            foreach (var packageId in PackedPackageIds)
            {
                var deps = GetDependencyIds(ReadNuspec(packDir, packageId));
                foreach (var dep in deps.Where(IsCoPackableFirstParty))
                {
                    NupkgExists(packDir, dep).Should().BeTrue(
                        $"the {packageId} nuspec declares co-packable first-party dependency " +
                        $"'{dep}' but no '{dep}.{TestVersion}.nupkg' was co-packed alongside it -- " +
                        "the dangling-dependency class from issue #124.");
                }
            }

            // Optionality contracts worth asserting explicitly:
            var apiDeps = GetDependencyIds(ReadNuspec(packDir, "Mostlylucid.BotDetection.Api"));
            apiDeps.Should().NotContain(id => id.Equals("Mostlylucid.BotDetection.UI", StringComparison.OrdinalIgnoreCase),
                "API-only hosts must not pull Razor views or dashboard static assets.");
            apiDeps.Should().Contain(id => id.Equals("Mostlylucid.BotDetection.Dashboard.Data", StringComparison.OrdinalIgnoreCase),
                "API query services and SQLite schemas belong to the backend data package.");
            var uiDeps = GetDependencyIds(ReadNuspec(packDir, "Mostlylucid.BotDetection.UI"));
            uiDeps.Should().Contain(
                id => id.Equals("Mostlylucid.BotDetection", StringComparison.OrdinalIgnoreCase),
                "the dashboard depends on the core detection assembly.");
            uiDeps.Should().Contain(
                id => id.Equals("Mostlylucid.BotDetection.OpenApi", StringComparison.OrdinalIgnoreCase),
                "the routes tab is backed by the OpenApi catalog pack.");
            uiDeps.Should().NotContain(
                id => id.Equals("Mostlylucid.BotDetection.PrometheusPack", StringComparison.OrdinalIgnoreCase),
                "Prometheus is an optional add-on -- UI must NOT hard-depend on it.");

            // The UI package must NOT ship Razor view SOURCE as contentFiles. The views are
            // compiled into the RCL assembly; if the .cshtml were packed as contentFiles a
            // consumer's Razor SDK recompiles them into the consumer assembly, where the
            // internal helpers the views reference are inaccessible -> CS0122/CS0117 on every
            // consumer build (found by the new-app verification of 8.11.1).
            var cshtmlCount = CountContentFiles(packDir, "Mostlylucid.BotDetection.UI", ".cshtml");
            cshtmlCount.Should().Be(0,
                $"UI package ships {cshtmlCount} .cshtml contentFiles -- consumers recompile them " +
                "against internal members and fail to build. Views are compiled into the RCL dll; " +
                "they must not be packed as contentFiles.");

            // NuGet gallery guideline: a URL-like token in the nuspec <description> trips the
            // "potentially invalid URL" validation warning (we hit it with the .bot TLD on the
            // core package). Descriptions must be URL-free prose; the project/repo links belong
            // in PackageProjectUrl / RepositoryUrl.
            foreach (var packageId in PackedPackageIds)
            {
                var desc = GetDescription(ReadNuspec(packDir, packageId));
                desc.Should().NotMatchRegex(
                    @"https?://|\b[a-z0-9-]+\.(bot|com|net|org|io|dev|cloud)\b",
                    $"the {packageId} nuspec <description> must not contain URL-like tokens " +
                    "(NuGet gallery validation guideline).");
            }

            // The definitive consumer guard: pack -> create temp consumer apps referencing ONLY
            // the UI package and ONLY the Api package -> restore + build against the pack feed +
            // nuget.org. This is the ONLY check that reproduces what customers actually hit
            // (NU1101 at restore, and the contentFiles CS0122/CS0117 at consumer build) against
            // the real packed artifact. The Api consumer specifically guards the Api ->
            // Llm.Tunnel dangling dep (8.12.0): it fails to restore if Llm.Tunnel isn't there.
            await BuildConsumerAppAsync(packDir, "mostlylucid.botdetection");
            await BuildConsumerAppAsync(packDir, "Mostlylucid.BotDetection.UI");
            await BuildConsumerAppAsync(packDir, "Mostlylucid.BotDetection.Api");
        }
        finally
        {
            try { Directory.Delete(packDir, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task DotnetPackAsync(string csproj, string outputDir, string repoRoot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "build", csproj, "-c", PackConfiguration, "--no-restore", "-v:minimal",
                     "-p:MinVerSkip=true", "-p:Version=" + TestVersion,
                     "/nodeReuse:false", "-p:UseSharedCompilation=false", "-p:UseRazorBuildServer=false"
                 })
            psi.ArgumentList.Add(argument);

        // GeneratePackageOnBuild=true makes dotnet pack default to NoBuild.
        // Explicitly build EVERY package at the test version first, or core's
        // nupkg can contain an old assembly while UI references the new version.
        await RunDotnetAsync(psi, "package build " + Path.GetFileName(csproj));
        psi.ArgumentList[0] = "pack";
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);
        await RunDotnetAsync(psi, "package pack " + Path.GetFileName(csproj));
    }

    /// <summary>
    ///     Create a throwaway ASP.NET Core consumer referencing ONLY the given package from the
    ///     pack feed, then publish and RUN it. Restore + publish + HTTP smoke checks must succeed -- restore catches the
    ///     NU1101 class (missing first-party deps, e.g. the Api -> Llm.Tunnel dangling dep), and
    ///     the build catches the contentFiles class (consumer Razor recompile failing on internal
    ///     members). This is the customer experience, end to end, against the real packed artifacts.
    /// </summary>
    private static async Task BuildConsumerAppAsync(string feedDir, string packageId)
    {
        var packageImports = packageId switch
        {
            "Mostlylucid.BotDetection.Api" => "using Mostlylucid.BotDetection.Api;",
            "Mostlylucid.BotDetection.UI" => "using Mostlylucid.BotDetection.UI.Extensions;",
            _ => ""
        };
        var registration = packageId switch
        {
            "Mostlylucid.BotDetection.Api" => """
                builder.Services.AddBotDetection();
                builder.Services.AddStyloBotApi(options => options.InjectResponseHeaders = true);
                builder.Configuration["BotDetection:Classification:BotFloor"] = "0.95";
                """,
            "Mostlylucid.BotDetection.UI" => """
                builder.Services.AddStyloBot(d => {
                    d.AllowUnauthenticatedAccess = true;
                    d.RequireAuthentication = false;
                    d.BasePath = "/_stylobot";
                });
                """,
            _ => "builder.Services.AddBotDetection();"
        };
        var middleware = packageId switch
        {
            "Mostlylucid.BotDetection.Api" => """
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseBotDetection();
                app.MapStyloBotApi();
                """,
            "Mostlylucid.BotDetection.UI" => "app.UseStyloBot();",
            _ => "app.UseBotDetection();"
        };
        var packageChecks = packageId switch
        {
            "Mostlylucid.BotDetection.Api" => """
                using var anonymous = await client.GetAsync("/api/v1/summary");
                if (anonymous.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                    throw new Exception($"Anonymous API request returned {anonymous.StatusCode}, expected 401");
                client.DefaultRequestHeaders.Add("X-SB-Api-Key", "package-smoke");
                using var authenticated = await client.GetAsync("/api/v1/summary");
                authenticated.EnsureSuccessStatusCode();
                foreach (var path in new[] { "/api/v1/detections", "/api/v1/signatures", "/api/v1/timeseries", "/api/v1/countries", "/api/v1/endpoints", "/api/v1/topbots" })
                {
                    using var read = await client.GetAsync(path);
                    read.EnsureSuccessStatusCode();
                }
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PackageTransport/1.0");
                var curl = new { method = "GET", path = "/target", remoteIp = "203.0.113.17", headers = new Dictionary<string, string> { ["User-Agent"] = "curl/8.0" } };
                var sqlmap = new { method = "GET", path = "/target", remoteIp = "203.0.113.18", headers = new Dictionary<string, string> { ["User-Agent"] = "sqlmap/1.8" } };
                void VerifyVerdict(JsonElement response, string target)
                {
                    var reasons = response.GetProperty("reasons").ToString();
                    if (!reasons.Contains(target, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Synthetic {target} request was not analyzed: {response}");
                    var verdict = response.GetProperty("verdict");
                    if (verdict.GetProperty("isBot").GetBoolean() != (verdict.GetProperty("botProbability").GetDouble() >= 0.95))
                        throw new Exception("API verdict ignored Classification.BotFloor");
                }
                using var detect = await client.PostAsJsonAsync("/api/v1/detect", curl);
                detect.EnsureSuccessStatusCode();
                VerifyVerdict(await detect.Content.ReadFromJsonAsync<JsonElement>(), "curl");
                using var batch = await client.PostAsJsonAsync("/api/v1/detect/batch", new[] { curl, sqlmap });
                batch.EnsureSuccessStatusCode();
                var batchResult = await batch.Content.ReadFromJsonAsync<JsonElement>();
                VerifyVerdict(batchResult[0], "curl");
                VerifyVerdict(batchResult[1], "sqlmap");
                """,
            "Mostlylucid.BotDetection.UI" => """
                using var page = await client.GetAsync("/_stylobot/dashboard/traffic");
                page.EnsureSuccessStatusCode();
                var html = await page.Content.ReadAsStringAsync();
                if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Dashboard did not render HTML");
                using var script = await client.GetAsync("/_content/Mostlylucid.BotDetection.UI/vendor/js/signalr.min.js");
                script.EnsureSuccessStatusCode();
                if ((await script.Content.ReadAsStringAsync()).Length < 1000)
                    throw new Exception("Dashboard's packaged static asset is missing or empty");
                """,
            _ => ""
        };
        var entry = $$"""
            using Microsoft.AspNetCore.Hosting.Server;
            using Microsoft.AspNetCore.Hosting.Server.Features;
            using Microsoft.Data.Sqlite;
            using System.Net.Http.Json;
            using System.Collections.Concurrent;
            using System.Text.Json;
            using Mostlylucid.BotDetection.Extensions;
            using Mostlylucid.BotDetection.Middleware;
            {{packageImports}}
            var builder = WebApplication.CreateBuilder(args);
            var diagnostics = new ConsumerLogProvider();
            builder.Logging.AddProvider(diagnostics);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var data = Path.Combine(builder.Environment.ContentRootPath, "data");
            builder.Configuration["BotDetection:DatabasePath"] = Path.Combine(data, "botdetection.db");
            builder.Configuration["BotDetection:ApiKeys:package-smoke:Name"] = "Package consumer smoke";
            {{registration}}
            if (args.Contains("--invalid-database-path"))
                builder.Services.PostConfigure<Mostlylucid.BotDetection.Models.BotDetectionOptions>(options => options.DatabasePath = null);
            await using var app = builder.Build();
            app.UseRouting();
            {{middleware}}
            app.MapGet("/consumer-smoke", (HttpContext context) =>
                context.GetBotDetectionResult() is { } result
                    ? (IResult)Results.Json(new { detected = true, probability = result.ConfidenceScore })
                    : Results.Problem("Detection middleware did not supply a result"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await app.StartAsync(deadline.Token);
            try
            {
                var address = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
                foreach (var agent in new[] { "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36", "curl/8.0" })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
                    var result = await client.GetFromJsonAsync<JsonElement>("/consumer-smoke", deadline.Token);
                    if (!result.GetProperty("detected").GetBoolean()) throw new Exception("No detection result");
                    client.DefaultRequestHeaders.UserAgent.Clear();
                }
                foreach (var name in new[] { "waveform_history", "pool_collisions" })
                {
                    await using var connection = new SqliteConnection($"Data Source={Path.Combine(data, name + ".db")};Mode=ReadOnly");
                    await connection.OpenAsync(deadline.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
                    command.Parameters.AddWithValue("$name", name);
                    if (Convert.ToInt64(await command.ExecuteScalarAsync(deadline.Token)) != 1)
                        throw new Exception($"Missing consumer startup schema: {name}");
                }
                {{packageChecks}}
            }
            finally
            {
                await app.StopAsync(deadline.Token);
            }
            if (!diagnostics.Failures.IsEmpty)
                throw new Exception("Unexpected consumer failure logs: " + string.Join(Environment.NewLine, diagnostics.Failures));
            Console.WriteLine("PACKAGED ASP.NET CONSUMER PASSED");
            return Environment.ExitCode;

            sealed class ConsumerLogProvider : ILoggerProvider
            {
                public ConcurrentQueue<string> Failures { get; } = new();
                public ILogger CreateLogger(string category) => new ConsumerLogger(category, Failures);
                public void Dispose() { }
                private sealed class ConsumerLogger(string category, ConcurrentQueue<string> failures) : ILogger
                {
                    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                    public bool IsEnabled(LogLevel level) => true;
                    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                        Func<TState, Exception?, string> formatter)
                    {
                        var message = formatter(state, error);
                        if (level >= LogLevel.Error || message.Contains("Session persistence channel FULL", StringComparison.Ordinal))
                            failures.Enqueue($"{category}: {message} {error}");
                    }
                }
            }
            """;

        var appDir = Path.Combine(Path.GetTempPath(), "sb-consume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(appDir, "Consumer.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{{packageId}}" Version="{{TestVersion}}" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Program.cs"), entry);

            var restorePsi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = appDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            restorePsi.ArgumentList.Add("restore");
            restorePsi.ArgumentList.Add("--source");
            restorePsi.ArgumentList.Add(feedDir);
            restorePsi.ArgumentList.Add("--source");
            restorePsi.ArgumentList.Add("https://api.nuget.org/v3/index.json");
            await RunDotnetAsync(restorePsi, "consumer restore");

            var buildPsi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = appDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            buildPsi.ArgumentList.Add("publish");
            buildPsi.ArgumentList.Add("-c");
            buildPsi.ArgumentList.Add("Release");
            buildPsi.ArgumentList.Add("--no-restore");
            buildPsi.ArgumentList.Add("--output");
            buildPsi.ArgumentList.Add(Path.Combine(appDir, "publish"));
            buildPsi.ArgumentList.Add("/nodeReuse:false");
            await RunDotnetAsync(buildPsi, "consumer publish");
            if (packageId == "Mostlylucid.BotDetection.Api")
                File.Exists(Path.Combine(appDir, "publish", "Mostlylucid.BotDetection.UI.dll")).Should().BeFalse(
                    "an API-only consumer must not ship the Razor UI assembly");

            var runPsi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.Combine(appDir, "publish"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            runPsi.ArgumentList.Add(Path.Combine(appDir, "publish", "Consumer.dll"));
            await RunDotnetAsync(runPsi, packageId + " consumer runtime");
            runPsi.ArgumentList.Add("--invalid-database-path");
            await RunDotnetAsync(runPsi, packageId + " invalid startup diagnostics", expectConfigurationFailure: true);
        }
        finally
        {
            try { Directory.Delete(appDir, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task RunDotnetAsync(ProcessStartInfo psi, string label, bool expectConfigurationFailure = false)
    {
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best-effort kill */ }
            throw new InvalidOperationException($"{label} timed out after 4 minutes.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (expectConfigurationFailure)
        {
            if (process.ExitCode == 0 || !(stdout + stderr).Contains("DatabasePath is not configured", StringComparison.Ordinal))
                throw new InvalidOperationException($"{label} did not fail with useful configuration diagnostics (exit {process.ExitCode}):\n{stdout}\n{stderr}");
            return;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{label} failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }

    private static XDocument ReadNuspec(string packDir, string packageId)
    {
        var nupkg = FindNupkg(packDir, packageId);
        File.Exists(nupkg).Should().BeTrue(
            $"expected {packageId}.{TestVersion}.nupkg in the pack output, got nothing.");

        using var archive = ZipFile.OpenRead(nupkg!);
        var entry = archive.Entries.First(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return XDocument.Load(reader);
    }

    private static string GetDescription(XDocument nuspec)
    {
        var ns = nuspec.Root!.Name.Namespace;
        return nuspec.Descendants(ns + "description").FirstOrDefault()?.Value ?? "";
    }

    private static int CountContentFiles(string packDir, string packageId, string suffix)
    {
        using var archive = ZipFile.OpenRead(FindNupkg(packDir, packageId)!);
        return archive.Entries.Count(e => e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> GetDependencyIds(XDocument nuspec)
    {
        return nuspec.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => e.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "mostlylucid.stylobot.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (mostlylucid.stylobot.sln) above " + AppContext.BaseDirectory);
    }
}
