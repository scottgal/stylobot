namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Late-binding catalog contributor enumerated by <see cref="SignalCatalog" />
///     on read. The reflected SignalKeys constants are catalogued once at
///     load (they're compile-time fixed), but some signal families are dynamic:
///     the meter-stream family (<c>meter.{name}.*</c>) appears as the runtime
///     publishes instruments and can change over the process lifetime.
///
///     <para>
///         Implementations are queried via <see cref="GetDescriptors" />;
///         they should return a snapshot of every descriptor they currently
///         want catalogued. <see cref="SignalCatalog" /> calls them on read
///         paths (<c>All</c>, <c>Search</c>, <c>TryGet</c>, etc.) and merges
///         the results on top of the reflected vocabulary. The reflected
///         entries always win on key collisions. A faulty source MUST NOT
///         take down catalog reads -- exceptions are swallowed by the
///         catalog.
///     </para>
/// </summary>
public interface ISignalCatalogSource
{
    /// <summary>
    ///     Return every <see cref="SignalDescriptor" /> this source wants
    ///     contributed to the catalogue. The catalog enumerates this set on
    ///     every read so dynamic vocabularies (e.g. live meter names) stay
    ///     fresh; sources should keep this O(known-entries) and avoid I/O.
    /// </summary>
    IReadOnlyList<SignalDescriptor> GetDescriptors();
}
