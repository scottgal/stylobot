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
///         Slow by design (~1-2 min: three <c>dotnet pack</c> calls) and deliberately kept in
///         the default CI gate so a future dependency-graph regression fails the build the
///         day it is introduced, not at the next release.
///     </para>
/// </summary>
public sealed class NuGetDependencyIntegrityTests
{
    private const string TestVersion = "9.9.9-test";

    private static readonly string[] PackedProjects =
    {
        "src/Mostlylucid.Common/Mostlylucid.Common.csproj",
        "src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj",
        "src/Mostlylucid.BotDetection.OpenApi/Mostlylucid.BotDetection.OpenApi.csproj",
        "src/Mostlylucid.BotDetection.PrometheusPack/Mostlylucid.BotDetection.PrometheusPack.csproj",
        "src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj",
    };

    [Fact]
    public async Task Ui_nuspec_declares_only_present_packs_and_never_prometheus()
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

            var uiNuspec = ReadNuspec(packDir, "Mostlylucid.BotDetection.UI");
            var dependencyIds = GetDependencyIds(uiNuspec);

            // The UI package's real hard deps are present. NuGet emits dependency ids
            // lowercase, so compare case-insensitively.
            dependencyIds.Should().Contain(
                id => id.Equals("Mostlylucid.BotDetection", StringComparison.OrdinalIgnoreCase),
                "the dashboard depends on the core detection assembly.");
            dependencyIds.Should().Contain(
                id => id.Equals("Mostlylucid.BotDetection.OpenApi", StringComparison.OrdinalIgnoreCase),
                "the routes tab is backed by the OpenApi catalog pack.");

            // Prometheus is an optional add-on: its widget surface moved INTO the pack
            // (the pack references UI), so the UI nuspec must NOT hard-depend on it.
            dependencyIds.Should().NotContain(
                id => id.Equals("Mostlylucid.BotDetection.PrometheusPack", StringComparison.OrdinalIgnoreCase),
                "Prometheus must be optional -- issue #124's dangling dependency came from UI " +
                "hard-depending on a pack that was never published.");

            // No dangling FIRST-PARTY deps: every declared Mostlylucid.* dependency must have
            // a matching nupkg in the pack output (the publish workflow co-packs them at the
            // same version). Third-party deps (Fluid.Core etc.) come from nuget.org and are
            // not co-packed, so they are out of scope.
            foreach (var dep in dependencyIds.Where(
                         id => id.StartsWith("mostlylucid.botdetection", StringComparison.OrdinalIgnoreCase)))
            {
                var nupkg = Path.Combine(packDir, $"{dep}.{TestVersion}.nupkg");
                File.Exists(nupkg).Should().BeTrue(
                    $"UI nuspec declares first-party dependency '{dep}' but no " +
                    $"'{dep}.{TestVersion}.nupkg' was packed alongside it -- this is exactly " +
                    "the dangling-dependency bug from issue #124.");
            }

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

            // PrometheusPack now depends on UI (the pack owns its widget surface via the UI
            // seam). A release that publishes the pack but fails to co-publish UI (or bumps it
            // out of sync) reproduces issue #124 for PACK consumers -- so the pack's own nuspec
            // must declare UI with a matching co-packed nupkg.
            var promNuspec = ReadNuspec(packDir, "Mostlylucid.BotDetection.PrometheusPack");
            var promDeps = GetDependencyIds(promNuspec);
            promDeps.Should().Contain(
                id => id.Equals("Mostlylucid.BotDetection.UI", StringComparison.OrdinalIgnoreCase),
                "PrometheusPack references UI for its widget surface -- its nuspec must declare it.");
            foreach (var dep in promDeps.Where(
                         id => id.StartsWith("mostlylucid", StringComparison.OrdinalIgnoreCase)))
            {
                File.Exists(Path.Combine(packDir, $"{dep}.{TestVersion}.nupkg")).Should().BeTrue(
                    $"PrometheusPack declares first-party dependency '{dep}' but no matching nupkg " +
                    "was co-packed -- the dangling-dependency class again.");
            }

            // NuGet gallery guideline: a URL-like token in the nuspec <description> trips the
            // "potentially invalid URL" validation warning (we hit it with the .bot TLD on the
            // core package). Descriptions must be URL-free prose; the project/repo links belong
            // in PackageProjectUrl / RepositoryUrl.
            foreach (var packageId in new[]
                     {
                         "mostlylucid.botdetection",
                         "Mostlylucid.BotDetection.OpenApi",
                         "Mostlylucid.BotDetection.PrometheusPack",
                         "Mostlylucid.BotDetection.UI",
                         "Mostlylucid.Common",
                     })
            {
                var desc = GetDescription(ReadNuspec(packDir, packageId));
                desc.Should().NotMatchRegex(
                    @"https?://|\b[a-z0-9-]+\.(bot|com|net|org|io|dev|cloud)\b",
                    $"the {packageId} nuspec <description> must not contain URL-like tokens " +
                    "(NuGet gallery validation guideline).");
            }

            // The definitive consumer guard: pack -> create a temp consumer app referencing ONLY
            // the UI package -> restore + build against the pack feed + nuget.org. This is the
            // ONLY check that reproduces what customers actually hit (NU1101 at restore, and the
            // contentFiles CS0122/CS0117 at consumer build) against the real packed artifact.
            await BuildConsumerAppAsync(packDir, repoRoot);
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
        psi.ArgumentList.Add("pack");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--no-restore");
        psi.ArgumentList.Add("-v:minimal"); // keep the build output bounded
        // MinVerSkip: without it MinVer computes its own version from the git
        // tags and the nupkg lands at an unrelated version, breaking the
        // exact-version co-existence assertions.
        psi.ArgumentList.Add("-p:MinVerSkip=true");
        psi.ArgumentList.Add("-p:Version=" + TestVersion);
        // Disable MSBuild node reuse + shared compilation + Razor build server:
        // this test shells a build from INSIDE a parallel xUnit suite, and a
        // persistent MSBuild/compiler node deadlocks against the testhost's own
        // build context (observed: the suite froze with idle MSBuild nodes).
        psi.ArgumentList.Add("/nodeReuse:false");
        psi.ArgumentList.Add("-p:UseSharedCompilation=false");
        psi.ArgumentList.Add("-p:UseRazorBuildServer=false");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);

        using var process = Process.Start(psi)!;

        // Drain BOTH pipes CONCURRENTLY. A chatty build (the UI RCL emits hundreds
        // of analyzer warnings) fills a pipe the moment it exceeds the buffer while
        // the other stream is being read to EOF, deadlocking ReadToEndAsync. Reading
        // both at once keeps the child unblocked.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best-effort kill */ }
            throw new InvalidOperationException(
                $"dotnet pack {Path.GetFileName(csproj)} timed out after 150 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().Be(0,
            $"dotnet pack {Path.GetFileName(csproj)} failed:\n{stdout}\n{stderr}");
    }

    /// <summary>
    ///     Create a throwaway ASP.NET Core consumer referencing ONLY the UI package from the
    ///     pack feed and BUILD it. Restore + build both must succeed -- restore catches the
    ///     NU1101 class (missing first-party deps), and the build catches the contentFiles
    ///     class (consumer Razor recompile failing on internal members). This is the customer
    ///     experience, end to end, against the real packed artifacts.
    /// </summary>
    private static async Task BuildConsumerAppAsync(string feedDir, string repoRoot)
    {
        var appDir = Path.Combine(Path.GetTempPath(), "sb-consume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(appDir, "Consumer.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Mostlylucid.BotDetection.UI" Version="9.9.9-test" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Program.cs"), """
                using Mostlylucid.BotDetection.UI.Extensions;
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddStyloBot(d => { d.AllowUnauthenticatedAccess = true; d.RequireAuthentication = false; });
                var app = builder.Build();
                app.UseRouting();
                app.UseStyloBot();
                app.MapGet("/", () => "ok");
                app.Run();
                """);

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
            buildPsi.ArgumentList.Add("build");
            buildPsi.ArgumentList.Add("-c");
            buildPsi.ArgumentList.Add("Release");
            buildPsi.ArgumentList.Add("/nodeReuse:false");
            await RunDotnetAsync(buildPsi, "consumer build");
        }
        finally
        {
            try { Directory.Delete(appDir, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task RunDotnetAsync(ProcessStartInfo psi, string label)
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{label} failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }

    private static XDocument ReadNuspec(string packDir, string packageId)
    {
        var nupkg = Path.Combine(packDir, $"{packageId}.{TestVersion}.nupkg");
        File.Exists(nupkg).Should().BeTrue(
            $"expected {packageId}.{TestVersion}.nupkg in the pack output, got nothing.");

        using var archive = ZipFile.OpenRead(nupkg);
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
        var nupkg = Path.Combine(packDir, $"{packageId}.{TestVersion}.nupkg");
        using var archive = ZipFile.OpenRead(nupkg);
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
