using System.Diagnostics;
using Mostlylucid.BotDetection.Test.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Half 2 of the 2026-09-09 name ruling, asserted from the RENDERED PAGE: when the
///     name a signature resolves to is a fallback ("Unclassified" is the live case — the
///     value the induced-name writer persisted before Half 1's gate), the display tier
///     must treat it as UNRESOLVED and render the provisional projection instead.
///     <para>
///         The projection is never stored: it is composed at read time from what the row
///         already carries (bot_type → the class, country → the qualifier, the signature
///         → the fp8 discriminator). The second test pins the REPLACEMENT property —
///         a real resolved name in a slot still wins outright.
///     </para>
///     <para>
///         Live data for <c>6TyG2z5IQguu37X-O3c2xw</c>: <c>bot_name="Unclassified"</c>
///         (persisted), <c>bot_type=Scraper</c>, no UA, country GB. The operator's worked
///         example is <c>Scraper 6TyG2z5I · GB</c> — the fp8 is EIGHT characters.
///     </para>
/// </summary>
public sealed class ProvisionalNameRenderedOnVisitorsPageTests
{
    private const string LiveSignature = "6TyG2z5IQguu37X-O3c2xw";

    private static async Task<string> RenderVisitorsPageAsync(params DashboardTopBotEntry[] rows)
    {
        var totalHits = rows.Sum(r => r.HitCount);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DiagnosticSource>(new DiagnosticListener(nameof(ProvisionalNameRenderedOnVisitorsPageTests)));
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener(nameof(ProvisionalNameRenderedOnVisitorsPageTests)));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(ProvisionalNameRenderedOnVisitorsPageTests).Assembly)
            .AddApplicationPart(typeof(Mostlylucid.BotDetection.UI.ViewComponents.Dashboard.SbVisitorListViewComponent).Assembly);
        services.AddSingleton<RazorViewRenderer>();
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }));
        services.AddSingleton<IOptions<DashboardMaterializerOptions>>(
            Options.Create(new DashboardMaterializerOptions()));
        services.AddSingleton(new Mock<IDashboardEventStore>().Object);
        services.AddSingleton<IFingerprintNameActionSlot, EmptyFingerprintNameActionSlot>();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/dashboard/visitors";
        context.Items["sb.dashboard.pageresult"] = new DashboardPageResult(
            new DashboardDatasetBundle(
                Summary: new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow,
                    TotalRequests = totalHits,
                    BotRequests = totalHits,
                    HumanRequests = 0,
                    UncertainRequests = 0,
                    UniqueSignatures = rows.Length,
                    BotFingerprints = rows.Length,
                    HumanFingerprints = 0,
                    RiskBandCounts = new(),
                    TopBotTypes = new(),
                    TopActions = new()
                },
                TimeBuckets: null,
                BotAggregate: rows,
                Geo: null,
                Endpoints: null));

        var model = new VisitorsPageModel(
            Filter: "all",
            Country: null,
            BotType: null,
            Threat: null,
            FingerprintId: null,
            Internal: false,
            BasePath: "/dashboard",
            Counters: new TrafficCounters(totalHits, 0, totalHits, 1d, 0, 0, 0, 0, 0, 0, rows.Length, 0, rows.Length),
            Countries: []);

        return await provider.GetRequiredService<RazorViewRenderer>()
            .RenderViewToStringAsync(
                "/Views/StyloBot/Dashboard/Visitors/Index.cshtml",
                model,
                context);
    }

    [Theory]
    [InlineData(LiveSignature, "Scraper 6TyG2z5I · GB")]
    [InlineData("7KpQ2w9xABCDEFGH", "Scraper 7KpQ2w9x · GB")]
    public async Task Fallback_named_row_renders_the_provisional_projection_with_the_fp8(
        string signature, string expected)
    {
        // The exact live row: the persisted name is the fallback "Unclassified", the
        // pipeline knows the class (Scraper) and the country (GB), and there is no UA.
        // Two UA-less scrapers in the same country must render DISTINCT names, which
        // is the whole point of the always-present fp8 discriminator.
        var html = await RenderVisitorsPageAsync(new DashboardTopBotEntry
        {
            PrimarySignature = signature,
            HitCount = 31,
            BotName = "Unclassified",
            BotType = "Scraper",
            CountryCode = "GB",
            UserAgent = null,
            BotProbability = 0.9,
            IsKnownBot = true,
            RiskBand = "VeryHigh",
            LastSeen = DateTime.UtcNow,
        });

        // Assert on the DECODED text: the browser renders "·" from Razor's &#xB7;,
        // so the bar ("what the operator sees") is checked on rendered text.
        var text = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("Unclassified", text);
    }

    [Fact]
    public async Task A_resolved_name_in_a_slot_wins_over_the_projection()
    {
        // THE REPLACEMENT PROPERTY, at the display tier: a real resolved name is not
        // replaced by a synthesis of the row's signals.
        var html = await RenderVisitorsPageAsync(new DashboardTopBotEntry
        {
            PrimarySignature = LiveSignature,
            HitCount = 31,
            BotName = "Googlebot",
            BotType = "Scraper",
            CountryCode = "GB",
            UserAgent = null,
            BotProbability = 0.9,
            IsKnownBot = true,
            RiskBand = "VeryHigh",
            LastSeen = DateTime.UtcNow,
        });

        var text = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("Googlebot", text);
        Assert.DoesNotContain("Scraper 6TyG2z5I", text);
    }

    [Fact]
    public async Task Two_unresolved_fingerprints_of_the_same_class_and_country_render_as_two_distinct_rows()
    {
        // "NO repeat names, ever" (operator rule): the fp8 discriminator is what keeps two
        // UA-less scrapers in the same country apart. Both rows are fallback-named, so both
        // are projected — and they must project to DIFFERENT names on the same page.
        var html = await RenderVisitorsPageAsync(
            new DashboardTopBotEntry
            {
                PrimarySignature = LiveSignature,
                HitCount = 20,
                BotName = "Unclassified",
                BotType = "Scraper",
                CountryCode = "GB",
                UserAgent = null,
                BotProbability = 0.9,
                IsKnownBot = true,
                RiskBand = "VeryHigh",
                LastSeen = DateTime.UtcNow,
            },
            new DashboardTopBotEntry
            {
                PrimarySignature = "7KpQ2w9xABCDEFGHIJKLMN",
                HitCount = 11,
                BotName = "Unclassified",
                BotType = "Scraper",
                CountryCode = "GB",
                UserAgent = null,
                BotProbability = 0.9,
                IsKnownBot = true,
                RiskBand = "VeryHigh",
                LastSeen = DateTime.UtcNow.AddSeconds(-1),
            });

        var text = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("Scraper 6TyG2z5I · GB", text);
        Assert.Contains("Scraper 7KpQ2w9x · GB", text);
    }
}
