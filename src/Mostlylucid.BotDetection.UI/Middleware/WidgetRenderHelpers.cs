using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Helpers shared between the FOSS dashboard middleware, view components, and
///     external dashboard consumers (the marketing website, third-party
///     embedders). Anything here is pure projection / shaping logic against the
///     canonical <see cref="DashboardTopBotEntry"/> and <see cref="ProjectedVisitor"/>
///     shapes; no IO, no state. Public so remote-mode hosts (the website
///     controllers) can use the same projection logic the view components do --
///     stops controllers re-inventing visitor shaping and ending up with the
///     "home page disconnected from dashboard" drift the user has caught
///     multiple times.
/// </summary>
public static class WidgetRenderHelpers
{
    // ^\s* tolerates leading whitespace/newlines that Razor emits before the first tag
    private static readonly Regex FirstTagRegex = new(
        @"^\s*(<[a-zA-Z][^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches the first opening tag that carries the data-sb-data-region attribute.
    // Group 1 = "<tag ... " (everything before the trailing >). Group 2 = "/>" or ">".
    private static readonly Regex DataRegionTagRegex = new(
        @"(<[a-zA-Z][^>]*?\sdata-sb-data-region(?=[ \t\r\n=/>])(?:=""[^""]*"")?[^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static IQueryCollection ExtractWidgetParams(HttpContext context, string widgetId)
    {
        var prefix = widgetId + ".";
        Dictionary<string, StringValues>? dict = null;
        foreach (var kvp in context.Request.Query)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                dict ??= new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
                dict[kvp.Key[prefix.Length..]] = kvp.Value;
            }
        }
        return dict is { Count: > 0 } ? new QueryCollection(dict) : context.Request.Query;
    }

    internal static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q)
    {
        var sorted = q
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"{k.Key}={k.Value}");
        return $"sb:widget:{widgetId}:{string.Join("&", sorted)}";
    }

    /// <summary>
    ///     Maps a widget instance id to the <see cref="Services.IDashboardChangeCursor"/>
    ///     surface whose data it renders. The surface's <c>TickFor()</c> is folded into
    ///     the widget's shingle fingerprint (<see cref="ComputeWidgetShingleFingerprint"/>)
    ///     so a data change on that surface -- and only that surface -- re-keys this
    ///     widget's cached render. Surfaces are the strings the producers bump:
    ///     <c>summary</c>, <c>signature</c>, <c>threats</c>, <c>countries</c>,
    ///     <c>endpoints</c>, <c>useragents</c> (see DetectionBroadcastMiddleware and
    ///     DashboardSummaryBroadcaster). Unknown widgets fall back to <c>signature</c>,
    ///     the broadest surface (bumped on every detection broadcast), so an unmapped
    ///     widget still refreshes on the normal live cadence rather than going stale.
    /// </summary>
    public static string WidgetSurface(string widgetId) => widgetId switch
    {
        "summary"     => "summary",
        "countries"   => "countries",
        "endpoints"   => "endpoints",
        "useragents"  => "useragents",
        "threats"     => "threats",
        "visitors"    => "signature",
        "sessions"    => "signature",
        "topbots" or "top-bots" or "top-visitors" or "live-visitors"
            or "live-activity" or "overview-topbots" => "signature",
        _ => "signature",
    };

    /// <summary>
    ///     The shingle fingerprint: the params-scoped widget key
    ///     (<see cref="ComputeWidgetCacheKey"/>) plus the widget's data-change
    ///     <paramref name="version"/> (its surface's cursor tick). Two shingles differ
    ///     iff their widget, filter/params, OR underlying data version differ -- so a
    ///     filter change or a data change yields a fresh key for THAT widget alone,
    ///     while an unchanged widget keeps the same fingerprint and serves warm from the
    ///     LFU. This is the cache identity for <c>DashboardWidgetShingleCache</c>.
    /// </summary>
    public static string ComputeWidgetShingleFingerprint(string widgetId, IQueryCollection q, long version) =>
        $"{ComputeWidgetCacheKey(widgetId, q)}:v{version}";

    internal static int QueryPage(IQueryCollection q) =>
        int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

    internal static int QueryPageSize(IQueryCollection q, int defaultSize, int max = int.MaxValue) =>
        int.TryParse(q["pageSize"].FirstOrDefault(), out var ps) && ps > 0 ? Math.Min(ps, max) : defaultSize;

    internal static string InjectOobAttribute(string html)
    {
        // Preferred path: a [data-sb-data-region] element exists in the chunk.
        // Inject hx-swap-oob="morph:innerHTML" on it so Idiomorph (the official
        // htmx morph extension, loaded via SbLiveUpdates) mutates the data
        // region's children in place -- unchanged rows are left untouched,
        // only the deltas mutate. Rows the operator's cursor is over, links
        // they're about to click, and any focused / hovered DOM nodes survive
        // a beacon. This replaces the previous innerHTML wholesale-replace
        // which tore down and rebuilt every row on every signal.
        var regionMatch = DataRegionTagRegex.Match(html);
        if (regionMatch.Success)
        {
            if (regionMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal))
                return html;

            // Bare `morph` (no :innerHTML / :outerHTML suffix) -- Idiomorph's OOB
            // hook in 0.7.4 only matches the unqualified keyword for `hx-swap-oob`;
            // the colon-suffixed variants trigger htmx:oobErrorNoTarget because
            // htmx's standard parser doesn't recognise them and the extension only
            // intercepts the plain word. Bare `morph` defaults to outerHTML
            // semantics, replacing the matched element with the new one whose
            // children have been morphed against the live ones -- the data
            // region's attributes (id, data-sb-data-region) survive because the
            // server emits them identically each time.
            return html[..(regionMatch.Index + regionMatch.Groups[1].Length)]
                   + " hx-swap-oob=\"morph\""
                   + html[(regionMatch.Index + regionMatch.Groups[1].Length)..];
        }

        // Fallback: no data region marked. Morph the whole widget element.
        var rootMatch = FirstTagRegex.Match(html);
        if (!rootMatch.Success) return html;
        if (rootMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;

        return html[..rootMatch.Groups[1].Index]
               + rootMatch.Groups[1].Value
               + " hx-swap-oob=\"morph\""
               + rootMatch.Groups[2].Value
               + html[(rootMatch.Index + rootMatch.Length)..];
    }

    /// <summary>
    ///     Splices the live per-minute hit-trend ring buffer from <see cref="SignatureAggregateCache"/>
    ///     onto any row whose <see cref="DashboardTopBotEntry.HitTrend"/> came back empty from the
    ///     event store. HitTrend has exactly ONE source of truth -- the runtime ring buffer; the DB
    ///     deliberately never stores per-minute counts (only the all-time <c>HitCount</c> column) -- so
    ///     this is surfacing that one source in a render path that was missing it, not forking state.
    ///     Mirrors the overlay <c>Mostlylucid.BotDetection.Api.Endpoints.ReadEndpoints.HandleTopBots</c>
    ///     already applies to the standalone REST JSON endpoint; every dashboard render path (SSR
    ///     ViewComponent, MVC page composer, SignalR/batch live-update) needs the SAME overlay applied
    ///     here. The PRIMARY sparkline source is the store: the gateway's Postgres backend
    ///     now projects each fingerprint's per-minute trend from the persisted rows (the
    ///     2026-08-13 sparkline fix — never in-memory-only). This overlay only fills the
    ///     remaining gaps (stores that don't project, or the last live minute before raw
    ///     rows land). <paramref name="signatureCache"/> is optional -- absent on a genuine
    ///     remote-mode viewer host with no local cache to overlay from, in which case rows
    ///     pass through unchanged (the established remote-mode-optional-DI pattern).
    /// </summary>
    public static IReadOnlyList<DashboardTopBotEntry> OverlayLiveHitTrend(
        IReadOnlyList<DashboardTopBotEntry> bots,
        SignatureAggregateCache? signatureCache)
    {
        if (signatureCache is null) return bots;

        List<DashboardTopBotEntry>? patched = null;
        for (var i = 0; i < bots.Count; i++)
        {
            if (bots[i].HitTrend is { Length: > 0 }) continue;
            if (!signatureCache.TryGetHitTrend(bots[i].PrimarySignature, out var trend)) continue;

            patched ??= new List<DashboardTopBotEntry>(bots);
            patched[i] = patched[i] with { HitTrend = trend };
        }
        return patched ?? bots;
    }

    /// <summary>
    ///     Collapse rows that share a verified-bot identity into a single aggregate row.
    ///     Identity-equality is gated on THREE axes so a spoofer never collapses with
    ///     the real client whose UA it copied:
    ///       1. <c>BotName</c> (or operator's CustomBotName) must match.
    ///       2. <c>IsVerifiedBot</c> must match -- a verified Googlebot (FCrDNS OK) and
    ///          an unverified Googlebot (rDNS missing) are distinct actors. The
    ///          "Spoofed-*" prefix that VerifiedBotContributor adds also lands the
    ///          spoofer in its own group naturally because the name itself differs.
    ///       3. Major UA-version family must match -- "Googlebot/2.1" requests do not
    ///          collapse with "Googlebot/2.0" requests; they're different crawlers
    ///          from Google's side and have separate ranges / behaviours.
    ///     Tool UAs and synth-named humans stay distinct -- they're different actors
    ///     that share a UA family. Shared by every top-bots build site so the visible
    ///     row count, pagination, and filter results all agree.
    /// </summary>
    public static List<DashboardTopBotEntry> CollapseGroupableIdentities(IReadOnlyList<DashboardTopBotEntry> source)
    {
        // Single pass to preserve the caller's sort order. Each output item carries
        // the position of its earliest source occurrence so a final OrderBy returns
        // results in caller-supplied order; the previous version always re-sorted
        // by LastSeen DESC which silently overrode any sort the caller passed in
        // (sort=name, sort=threat, sort=hits etc. all rendered as lastseen).
        // Case-insensitive keys -- same centroid rule used by the
        // SignatureAggregateCache collapse on the Visitors path. Stale
        // pre-canonicaliser rows in the event store still surface mixed
        // casings; the view collapses them at the group key without a
        // forced DB rewrite.
        var firstIndexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupMembers = new Dictionary<string, List<DashboardTopBotEntry>>(StringComparer.OrdinalIgnoreCase);
        var passThrough = new List<(int idx, DashboardTopBotEntry entry)>();

        for (var i = 0; i < source.Count; i++)
        {
            var b = source[i];
            if (IsGroupableIdentity(b))
            {
                var key = BuildIdentityKey(b);
                if (!firstIndexOf.ContainsKey(key))
                {
                    firstIndexOf[key] = i;
                    groupMembers[key] = new List<DashboardTopBotEntry>();
                }
                groupMembers[key].Add(b);
            }
            else
            {
                passThrough.Add((i, b));
            }
        }

        var collapsed = new List<(int idx, DashboardTopBotEntry entry)>(groupMembers.Count);
        foreach (var (key, members) in groupMembers)
        {
            var minIdx = firstIndexOf[key];
            if (members.Count == 1)
            {
                collapsed.Add((minIdx, members[0]));
                continue;
            }
            var canonical = members.OrderByDescending(b => b.LastSeen).First();
            collapsed.Add((minIdx, canonical with
            {
                HitCount = members.Sum(b => b.HitCount),
                FirstSeen = members.Min(b => b.FirstSeen == default ? DateTime.MaxValue : b.FirstSeen),
                LastSeen = members.Max(b => b.LastSeen),
                // Max-of-members. The matcher writes the per-request verdict, so a
                // confirmed Googlebot row that ever scored 1.0 stays at 1.0 even if
                // a cold-start request earlier sat at 0.2. Mean-across-members hides
                // the bot probability of clear-cut named identities.
                BotProbability = members.Max(b => b.BotProbability)
            }));
        }

        return collapsed.Concat(passThrough)
            .OrderBy(x => x.idx)
            .Select(x => x.entry)
            .ToList();
    }

    /// <summary>
    ///     Identity key for collapse. Combines name + verification state ONLY --
    ///     grouping is by bot identity, NEVER by the raw UA string (operator rule
    ///     2026-08-12: "NO repeat names, ever"; the same bot with different UAs must
    ///     collapse to ONE row with a clean name). A verified Googlebot and an
    ///     unverified Googlebot stay separate (verification is identity state, not
    ///     UA), and the operator-set CustomBotName is the highest-priority identity
    ///     signal and wins regardless of UA shape. The UA-major-version component
    ///     was REMOVED: it only ever fired on rows carrying a representative UA
    ///     (e.g. absorbed aggregate rows after the 2026-08-12 signal fix), and
    ///     split one bot (PetalBot/2.0 vs PetalBot/3.0) into duplicate rows.
    /// </summary>
    private static string BuildIdentityKey(DashboardTopBotEntry b)
    {
        if (b.CustomBotName is { Length: > 0 } custom) return $"custom|{custom}";
        var name = b.BotName ?? string.Empty;
        var verified = b.IsVerifiedBot ? "v" : "u";
        return $"{name}|{verified}";
    }

    /// <summary>
    ///     Single predicate for "is this row safe to collapse with other same-name rows".
    ///     Used by <see cref="CollapseGroupableIdentities"/> at model-build time, and by the
    ///     SbTopBots view at render time to decide which rows still need a synth-name
    ///     disambiguator (the ones that DON'T get collapsed). Operator-set CustomBotName is
    ///     always trusted; otherwise the UA must have produced a real BotName AND BotType
    ///     must be a known-bot category (not "Tool" / "Unknown") -- tools and humans are
    ///     different actors who share a UA family and must NOT collapse.
    /// </summary>
    public static bool IsGroupableIdentity(DashboardTopBotEntry b) =>
        IsGroupableIdentity(b.CustomBotName, b.BotName, b.BotType);

    /// <summary>
    ///     Primitive-typed predicate overload so non-DashboardTopBotEntry surfaces
    ///     (ProjectedVisitor in SignatureAggregateCache.GetFiltered, anything else) can
    ///     share the same "is safe to collapse" rule without re-implementing it.
    ///     A recognised <c>BotName</c> in the BotPatternLoader catalog is enough to
    ///     collapse -- BotType propagation through the persistence layer is best-
    ///     effort, but the pattern-name catalog is the canonical "this is a real
    ///     bot identity" signal and is the same check <c>SignatureDisplayName.Resolve</c>
    ///     trusts for label disposition. Operator-set <c>CustomBotName</c> always
    ///     groups; raw / null names never do.
    /// </summary>
    public static bool IsGroupableIdentity(string? customBotName, string? botName, string? botType)
    {
        if (!string.IsNullOrWhiteSpace(customBotName)) return true;
        if (string.IsNullOrWhiteSpace(botName)) return false;

        // Tool / Unknown bot-types are explicit "no identity" sentinels -- a hand
        // crafted tool UA isn't an actor we want to merge across rows even when
        // the matcher labelled it. Keep them out regardless of catalog status.
        if (string.Equals(botType, "Tool",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(botType, "Unknown", StringComparison.OrdinalIgnoreCase))
            return false;

        // Non-empty BotType wins -- the matcher set a real category, the row is
        // a real identity row, collapse it.
        if (!string.IsNullOrWhiteSpace(botType)) return true;

        // BotType empty but BotName matches a known catalog pattern (Googlebot,
        // bingbot, GPTBot, ...) -- this is the persistence-gap path the user hit
        // on staging: identity is real, BotType column just wasn't propagated.
        // The Spoofed- prefix VerifiedBotContributor adds for unverified bots
        // counts as a real identity too (it's a hostile-named row).
        if (botName.StartsWith("Spoofed-", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(BotPatternLoader.Default.FindBotTypeByName(botName));
    }

    /// <summary>
    ///     Case-insensitive substring match against the searchable fields of a top-bots row
    ///     (CustomBotName, BotName, BotType, PrimarySignature). Null/empty query returns
    ///     the input unchanged. Shared by every top-bots build site so the search box
    ///     behaves identically across the dashboard middleware, the OOB batch path,
    ///     and the view component.
    /// </summary>
    public static List<DashboardTopBotEntry> ApplySearchFilter(
        IReadOnlyList<DashboardTopBotEntry> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source.ToList();
        var q = query.Trim();
        var cmp = StringComparison.OrdinalIgnoreCase;
        var result = new List<DashboardTopBotEntry>(source.Count);
        foreach (var b in source)
        {
            if ((b.CustomBotName?.Contains(q, cmp) ?? false) ||
                (b.BotName?.Contains(q, cmp) ?? false) ||
                (b.BotType?.Contains(q, cmp) ?? false) ||
                b.PrimarySignature.Contains(q, cmp))
            {
                result.Add(b);
            }
        }
        return result;
    }

    /// <summary>
    ///     Apply the dashboard's top-bots sort rules to an arbitrary list. Mirrors the
    ///     switch in <see cref="Services.SignatureAggregateCache.GetTopBots"/> so the
    ///     cache-bypass read path (BuildTopBotsModel calling IDashboardEventStore in
    ///     remote-mode hosts) renders rows in the same order operators see when the
    ///     cache is hot.
    /// </summary>
    public static IEnumerable<DashboardTopBotEntry> SortTopBots(
        IEnumerable<DashboardTopBotEntry> source, string? sortBy, string? sortDir)
    {
        var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
        return (sortBy?.ToLowerInvariant()) switch
        {
            "name" => asc
                ? source.OrderBy(b => b.BotName ?? b.PrimarySignature)
                : source.OrderByDescending(b => b.BotName ?? b.PrimarySignature),
            "lastseen" => asc
                ? source.OrderBy(b => b.LastSeen)
                : source.OrderByDescending(b => b.LastSeen),
            "country" => asc
                ? source.OrderBy(b => b.CountryCode ?? "ZZ")
                : source.OrderByDescending(b => b.CountryCode ?? "ZZ"),
            "probability" => asc
                ? source.OrderBy(b => b.BotProbability)
                : source.OrderByDescending(b => b.BotProbability),
            "threat" => asc
                ? source.OrderBy(b => b.ThreatScore ?? 0)
                : source.OrderByDescending(b => b.ThreatScore ?? 0),
            "hits" => asc
                ? source.OrderBy(b => b.HitCount)
                : source.OrderByDescending(b => b.HitCount),
            _ => source.OrderByDescending(b =>
                (b.ThreatScore ?? 0) * 0.4 + Math.Log10(Math.Max(b.HitCount, 1)) * 0.6)
        };
    }

    /// <summary>
    ///     Project a top-bots fetch (read-through path via IDashboardEventStore) into the
    ///     ProjectedVisitor shape the Visitors-tab view consumes, applying the same audience
    ///     filter / sort / page rules <see cref="Services.SignatureAggregateCache.GetFiltered"/>
    ///     does. Counts header is computed from the full unfiltered projection so the
    ///     All / Humans / Bots header stays honest when an audience switch is active.
    ///     <para>
    ///     This is the visitor-list parallel to <see cref="SortTopBots"/>: lets the
    ///     dashboard render the Visitors tab off a fresh event-store top-N every request,
    ///     so a remote-mode host whose local cache only warms on startup still gets
    ///     up-to-date visitors.
    ///     </para>
    /// </summary>
    public static (IReadOnlyList<ProjectedVisitor> Items, int TotalCount, FilterCounts Counts) ProjectAsVisitors(
        IEnumerable<DashboardTopBotEntry> source,
        string? filter,
        string sortField,
        string sortDir,
        int page,
        int pageSize,
        string? country = null,
        string? botType = null,
        string? threat = null,
        string? fingerprintId = null,
        bool internalOnly = false)
    {
        // Apply the SAME centroid collapse the Top Bots widget uses BEFORE
        // projection. The gateway-local /dashboard/visitors render path used
        // to skip this step and showed every fingerprint as its own row, so
        // a single bot identity (Googlebot, GPTBot) surfaced as N rows --
        // one per fingerprint that resolved to it. Single collapse function,
        // case-insensitive identity key, every list view consistent.
        var collapsed = CollapseGroupableIdentities(source.ToList());

        var snapshot = collapsed.Select(e => new ProjectedVisitor
        {
            PrimarySignature = e.PrimarySignature,
            Hits = e.HitCount,
            FirstSeen = e.FirstSeen,
            LastSeen = e.LastSeen,
            IsBot = e.IsKnownBot,
            BotProbability = e.BotProbability,
            Confidence = e.Confidence,
            RiskBand = e.RiskBand ?? "Unknown",
            LastPath = e.LastPath,
            Action = e.Action ?? "Allow",
            BotName = e.BotName,
            BotType = e.BotType,
            UserAgent = e.UserAgent,
            CountryCode = e.CountryCode,
            UaFamily = e.UaFamily,
            ThreatScore = e.ThreatScore,
            ThreatBand = e.ThreatBand,
            Narrative = e.Narrative,
            Description = e.Description,
            ProcessingTimeMs = e.ProcessingTimeMs,
            TopReasons = e.TopReasons ?? new List<string>(),
            // V1: surface the durable entity id on the projection so the
            // Visitors page ?fingerprint= URL filter can match against it.
            // Falls back to null when no entity edge exists for the row.
            FingerprintId = e.EntityId,
        }).ToList();

        // Counts are derived from the full unfiltered projection. AI / Search / Tools
        // sub-classifications use the bot_type substring rules inlined below (mirroring
        // SignatureAggregateCache's IsAiBot / IsSearchBot / IsToolBot). Leaving them at 0
        // would still be correct for All/Humans/Bots but the sub-buckets would mis-report
        // on remote-mode hosts.
        var counts = new FilterCounts
        {
            All = snapshot.Count,
            Humans = snapshot.Count(v => !v.IsBot),
            Bots = snapshot.Count(v => v.IsBot),
            Ai = snapshot.Count(v => v.IsBot && IsAiBotByType(v.BotType)),
            Search = snapshot.Count(v => v.IsBot && IsSearchBotByType(v.BotType)),
            Tools = snapshot.Count(v => v.IsBot && IsToolBotByType(v.BotType)),
            // Internal = self-traffic (bot_type == "Internal": loopback / RFC1918 / health
            // probes). Exact marker, matching the middleware + store + Postgres.
            Internal = snapshot.Count(IsInternal),
        };

        // Default (and humans/bots) EXCLUDE self-traffic, matching the middleware / store /
        // Postgres AudiencePredicate. "internal" shows only self; "all" shows the full mix.
        // ai/search/tools already exclude internal via their bot-type check.
        IEnumerable<ProjectedVisitor> items = filter switch
        {
            "humans"   => snapshot.Where(v => !v.IsBot && !IsInternal(v)),
            "bots"     => snapshot.Where(v => v.IsBot && !IsInternal(v)),
            "ai"       => snapshot.Where(v => v.IsBot && IsAiBotByType(v.BotType)),
            "search"   => snapshot.Where(v => v.IsBot && IsSearchBotByType(v.BotType)),
            "tools"    => snapshot.Where(v => v.IsBot && IsToolBotByType(v.BotType)),
            "internal" => snapshot.Where(IsInternal),
            "all"      => snapshot,
            _          => snapshot.Where(v => !IsInternal(v))
        };

        // V1 URL filters (Visitors page): post-filter the already-projected
        // snapshot. Applied AFTER the audience pill so chip + pill compose
        // naturally (e.g. ?filter=bots&country=US returns US bots).
        if (!string.IsNullOrEmpty(country))
            items = items.Where(v => string.Equals(v.CountryCode, country, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(botType))
            items = items.Where(v => string.Equals(v.BotType, botType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(threat))
        {
            // ?threat=Medium+ means Medium-or-higher; trailing + is conventional.
            // Mirrors the same shape TrafficController introduced in T1.
            var minRank = ThreatRank(threat.TrimEnd('+'));
            items = items.Where(v => ThreatRank(v.ThreatBand) >= minRank);
        }
        if (!string.IsNullOrEmpty(fingerprintId))
        {
            // Match on either the durable entity id OR the primary signature.
            // Operators clicking through from a fingerprint detail link expect
            // either form to work; signature is rotation-fragile but harmless
            // as a fallback because the rotated row will simply not match.
            items = items.Where(v =>
                string.Equals(v.FingerprintId, fingerprintId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.PrimarySignature, fingerprintId, StringComparison.OrdinalIgnoreCase));
        }
        if (internalOnly)
            items = items.Where(IsInternal);

        items = (sortField, sortDir) switch
        {
            ("name", "asc") => items.OrderBy(v => v.BotName ?? v.PrimarySignature),
            ("name", _)     => items.OrderByDescending(v => v.BotName ?? v.PrimarySignature),
            ("hits", "asc") => items.OrderBy(v => v.Hits),
            ("hits", _)     => items.OrderByDescending(v => v.Hits),
            ("risk", "asc") => items.OrderBy(v => RiskOrder(v.RiskBand)),
            ("risk", _)     => items.OrderByDescending(v => RiskOrder(v.RiskBand)),
            (_, "asc")      => items.OrderBy(v => v.LastSeen),
            _               => items.OrderByDescending(v => v.LastSeen)
        };

        var materialized = items.ToList();
        var paged = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (paged, materialized.Count, counts);
    }

    private static int RiskOrder(string? band) => band switch
    {
        "VeryHigh" => 5,
        "High" => 4,
        "Medium" or "Elevated" => 3,
        "Low" => 2,
        "VeryLow" => 1,
        _ => 0
    };

    // Same BotType predicates SignatureAggregateCache uses (kept inline rather than
    // exposing the private methods so the helper has no dependency on the cache itself).
    private static bool IsAiBotByType(string? botType)     => botType is "AiBot";
    private static bool IsSearchBotByType(string? botType) => botType is "SearchEngine" or "VerifiedBot" or "GoodBot";
    private static bool IsToolBotByType(string? botType)   => botType is "Scraper" or "MonitoringBot" or "SocialMediaBot" or "Tool";

    // Same shape TrafficController.ThreatRank uses (T1) — kept in sync so the
    // ?threat=Medium+ semantic is identical on Traffic + Visitors pages.
    private static int ThreatRank(string? band) => band switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0,
    };

    /// <summary>
    ///     V1: pragmatic "looks like internal/local traffic" predicate. Returns
    ///     true when the row has no usable geo AND is not a known bot AND the
    ///     bot probability is below the human threshold. Catches the typical
    ///     LAN / loopback / private-VPN call shape on a freshly-deployed gateway
    ///     until a real per-fingerprint <c>IsInternal</c> marker exists
    ///     (RFC1918 source-IP latch or operator-tagged fingerprint).
    /// </summary>
    // Exact self-traffic marker: bot_type == "Internal" (loopback / RFC1918 / health probes /
    // StyloBot talking to itself). Matches the middleware + Postgres definition
    // (SbWidgetBatchMiddleware.IsInternal, PostgreSQLDashboardEventStore.AudiencePredicate);
    // replaces the former fuzzy "no-geo + not-bot + low-prob" guess that misclassified.
    private static bool IsInternal(ProjectedVisitor v)
        => string.Equals(v.BotType, "Internal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Cached-poison guard (operator directive 2026-08-12): true when a rendered
    ///     widget's HTML is the EMPTY-STATE — zero rows, "no data in this window",
    ///     "No endpoint analytics yet", etc. Such renders MUST never be cached as
    ///     authoritative shingles (a prewarm that ran against a cold/broken pipeline
    ///     would otherwise cache emptiness forever). The known empty-state markers
    ///     are the views' own copy — the deliberate, honest empty states, not the
    ///     warming spinners (which render as loading elements with no text).
    /// </summary>
    internal static bool IsEmptyStateHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        return html.Contains("No endpoint analytics yet", StringComparison.OrdinalIgnoreCase)
            || html.Contains("No location data", StringComparison.OrdinalIgnoreCase)
            || html.Contains("No external traffic", StringComparison.OrdinalIgnoreCase)
            || html.Contains("No detection signals", StringComparison.OrdinalIgnoreCase)
            || html.Contains("no data in this window", StringComparison.OrdinalIgnoreCase)
            || html.Contains("No data yet", StringComparison.OrdinalIgnoreCase)
            || html.Contains("no data yet", StringComparison.OrdinalIgnoreCase);
    }
}
