using System.Diagnostics;
using Mostlylucid.BotDetection.Test.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The FOURTH name-consumption site, asserted from the RENDERED page: the sessions list
///     (<c>SbSessionsList/Default.cshtml</c>) renders <c>SessionListEntry.BotName</c>, which the
///     component builds through <see cref="SessionEnrichmentExtensions.ResolveBotName"/>. When the
///     persisted name is a fallback ("Unclassified" — the live value), that resolver must hand the
///     view the provisional projection instead, exactly as the visitors list does.
/// </summary>
public sealed class SessionsListProvisionalNameRenderedTests
{
    private const string LiveSignature = "6TyG2z5IQguu37X-O3c2xw";

    private static async Task<string> RenderSessionsListAsync(SessionListEntry entry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DiagnosticSource>(new DiagnosticListener(nameof(SessionsListProvisionalNameRenderedTests)));
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener(nameof(SessionsListProvisionalNameRenderedTests)));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(SessionsListProvisionalNameRenderedTests).Assembly)
            .AddApplicationPart(typeof(Mostlylucid.BotDetection.UI.ViewComponents.Dashboard.SbSessionsListViewComponent).Assembly);
        services.AddSingleton<RazorViewRenderer>();
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }));
        services.AddSingleton<IOptions<DashboardMaterializerOptions>>(
            Options.Create(new DashboardMaterializerOptions()));
        services.AddSingleton(new Mock<IDashboardEventStore>().Object);
        services.AddSingleton<IFingerprintNameActionSlot, EmptyFingerprintNameActionSlot>();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/dashboard/sessions";

        var model = new SessionsListModel
        {
            Sessions = [entry],
            BasePath = "/dashboard",
            TotalCount = 1,
        };

        return await provider.GetRequiredService<RazorViewRenderer>()
            .RenderViewToStringAsync(
                "/Views/Shared/Components/SbSessionsList/Default.cshtml",
                model,
                context);
    }

    /// <summary>Builds the entry the way <c>SbSessionsListViewComponent</c> does: through the real
    ///     resolver, with the session row's class / country / UA as the projection inputs.</summary>
    private static SessionListEntry Entry(string? storedName) =>
        new()
        {
            Signature = LiveSignature,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = DateTime.UtcNow,
            RequestCount = 31,
            DominantState = "PageView",
            IsBot = true,
            AvgBotProbability = 0.9,
            RiskBand = "VeryHigh",
            BotName = new Dictionary<string, string?>().ResolveBotName(
                cache: null, LiveSignature, storedName,
                botType: "Scraper", countryCode: "GB", userAgent: null),
            CountryCode = "GB",
        };

    [Fact]
    public async Task A_fallback_named_session_renders_the_provisional_projection()
    {
        var html = await RenderSessionsListAsync(Entry("Unclassified"));

        var text = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("Scraper 6TyG2z5I · GB", text);
        Assert.DoesNotContain("Unclassified", text);
    }

    [Fact]
    public async Task A_real_session_name_still_wins_over_the_projection()
    {
        var html = await RenderSessionsListAsync(Entry("Googlebot"));

        var text = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("Googlebot", text);
        Assert.DoesNotContain("Scraper 6TyG2z5I", text);
    }

    /// <summary>
    ///     THE COMPONENT-LEVEL PASS: the two tests above prove the resolver + view pair; this one
    ///     drives <c>SbSessionsListViewComponent.InvokeAsync</c> itself over the real store seam
    ///     (self-fetch path) and asserts the model the component produced. That is the claim the
    ///     4th site rests on — the COMPONENT reads the persisted row and emits the projected name,
    ///     with the right inputs — rather than "the resolver would if asked".
    /// </summary>
    [Fact]
    public async Task The_sessions_component_projects_the_name_from_a_persisted_row()
    {
        var archive = new Mock<IDetectionArchive>();
        archive
            .Setup(a => a.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<PersistedSession>
            {
                new()
                {
                    Signature = LiveSignature,
                    StartedAt = DateTime.UtcNow.AddMinutes(-5),
                    EndedAt = DateTime.UtcNow,
                    RequestCount = 31,
                    Vector = new byte[] { 0 },
                    Maturity = 0.5f,
                    DominantState = "PageView",
                    IsBot = true,
                    AvgBotProbability = 0.9,
                    AvgConfidence = 0.9,
                    RiskBand = "VeryHigh",
                    BotName = "Unclassified",   // the live persisted fallback
                    BotType = "Scraper",
                    CountryCode = "GB",
                }
            });

        // The lookup extensions read through these store methods; both empty, so the projection
        // has only the row's own class/country to work with.
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>()))
            .ReturnsAsync(new List<DashboardSignatureEvent>());
        store.Setup(s => s.GetTopBotsAsync(
                It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new List<DashboardTopBotEntry>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }));
        // vc.View(...) eagerly resolves ICompositeViewEngine; a stub is enough because the
        // result is inspected as a model, never rendered here (the view render is covered by
        // the two tests above).
        services.AddSingleton(new Mock<ICompositeViewEngine>().Object);
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };

        var vc = new Mostlylucid.BotDetection.UI.ViewComponents.Dashboard.SbSessionsListViewComponent(
            archive.Object, store.Object, Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }))
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var result = Assert.IsType<ViewViewComponentResult>(await vc.InvokeAsync());
        var model = Assert.IsType<SessionsListModel>(result.ViewData.Model);
        var entry = Assert.Single(model.Sessions);

        Assert.Equal("Scraper 6TyG2z5I · GB", entry.BotName);
    }
}
