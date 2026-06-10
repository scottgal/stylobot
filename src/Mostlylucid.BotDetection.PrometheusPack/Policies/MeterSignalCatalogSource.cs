using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies;

/// <summary>
///     <see cref="ISignalCatalogSource" /> that projects every catalogued meter
///     into the <c>meter.{name}.{facet}</c> signal-catalog vocabulary so the
///     Policy Stack editor's autocomplete sees the same keys the runtime can
///     resolve at <see cref="MeterSnapshotSignalContributor" /> time.
///
///     <para>
///         The facet family is:
///         <list type="bullet">
///             <item><description><c>meter.{name}.current</c></description></item>
///             <item><description><c>meter.{name}.p50</c> (Histogram only)</description></item>
///             <item><description><c>meter.{name}.p99</c> (Histogram only)</description></item>
///             <item><description><c>meter.{name}.delta_1m</c></description></item>
///             <item><description><c>meter.{name}.delta_5m</c></description></item>
///         </list>
///         Counter / Gauge / UpDownCounter meters get three entries each;
///         Histograms get five. Every entry is grouped under <c>"Meters"</c>
///         and typed <see cref="SignalKind.Number" />.
///     </para>
///     <para>
///         Discovery is dynamic: <see cref="IMeterStream.ListAsync" /> returns
///         the current catalog snapshot, so meters published mid-process show
///         up the next time the catalog is read. The source intentionally calls
///         <c>ListAsync</c> with <see cref="CancellationToken.None" /> and a
///         GetAwaiter().GetResult() bridge -- both in-process implementations
///         return synchronously, and catalog reads are off the per-request
///         hot path (autocomplete = once per HTTP request).
///     </para>
/// </summary>
public sealed class MeterSignalCatalogSource : ISignalCatalogSource
{
    private const string Group = "Meters";

    private static readonly IReadOnlyList<string> NumberOperators =
        new[] { "=", "!=", ">=", ">", "<=", "<", "between" };

    private static readonly IReadOnlyList<SignalExample> NoExamples = Array.Empty<SignalExample>();
    private static readonly IReadOnlyList<string> NoRelated = Array.Empty<string>();

    private readonly IMeterStream _stream;

    public MeterSignalCatalogSource(IMeterStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <inheritdoc />
    public IReadOnlyList<SignalDescriptor> GetDescriptors()
    {
        IReadOnlyList<MeterCatalogEntry> catalog;
        try
        {
            catalog = _stream.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // A faulty meter stream must not poison the editor catalog.
            return Array.Empty<SignalDescriptor>();
        }

        if (catalog.Count == 0) return Array.Empty<SignalDescriptor>();

        // Counter/Gauge/UpDownCounter contribute 3 entries; Histogram contributes 5.
        // Initial estimate keeps allocation tight.
        var capacity = 0;
        for (var i = 0; i < catalog.Count; i++)
            capacity += catalog[i].Kind == MeterKind.Histogram ? 5 : 3;
        var result = new List<SignalDescriptor>(capacity);

        foreach (var entry in catalog)
        {
            result.Add(Entry(entry.Name, ".current",  "Most recent observed value of the meter."));
            result.Add(Entry(entry.Name, ".delta_1m", "Sum of bucket values across the last 1 minute."));
            result.Add(Entry(entry.Name, ".delta_5m", "Sum of bucket values across the last 5 minutes."));
            if (entry.Kind == MeterKind.Histogram)
            {
                result.Add(Entry(entry.Name, ".p50", "P50 (median) across the last minute (Histogram only)."));
                result.Add(Entry(entry.Name, ".p99", "P99 across the last minute (Histogram only)."));
            }
        }

        return result;
    }

    private static SignalDescriptor Entry(string meterName, string suffix, string description) =>
        new(
            Key:            $"meter.{meterName}{suffix}",
            Kind:           SignalKind.Number,
            Short:          description,
            Long:           description,
            DeclaringType:  Group,
            Operators:      NumberOperators,
            Examples:       NoExamples,
            Related:        NoRelated);
}