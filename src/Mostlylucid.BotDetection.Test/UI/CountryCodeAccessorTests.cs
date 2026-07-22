using FluentAssertions;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Covers the AOT / no-JIT country-code accessor path in
///     <see cref="DetectionBroadcastMiddleware" />. Under NativeAOT (the shipped Console gateway)
///     the <c>Expression.Compile</c> fast-path is dead-code-eliminated and this reflection reader
///     is used instead. A JIT test host's runtime guard would always pick the compiled delegate,
///     so we exercise the reflection builder directly — it is the accessor that runs in production
///     on the AOT gateway. See issue <c>nativeaot-pessimizes-the-compiled-delegate-count</c>.
/// </summary>
public class CountryCodeAccessorTests
{
    private sealed class GeoLike
    {
        public string? CountryCode { get; init; }
    }

    [Fact]
    public void Reflection_accessor_reads_CountryCode()
    {
        var prop = typeof(GeoLike).GetProperty(nameof(GeoLike.CountryCode))!;
        var accessor = DetectionBroadcastMiddleware.BuildReflectionCountryCodeAccessor(prop);

        accessor(new GeoLike { CountryCode = "NL" }).Should().Be("NL");
    }

    [Fact]
    public void Reflection_accessor_returns_null_when_property_value_null()
    {
        var prop = typeof(GeoLike).GetProperty(nameof(GeoLike.CountryCode))!;
        var accessor = DetectionBroadcastMiddleware.BuildReflectionCountryCodeAccessor(prop);

        accessor(new GeoLike { CountryCode = null }).Should().BeNull();
    }
}
