using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Lookup-by-id is the bridge between a fingerprint's stored InferredClientType (an
///     archetype id) and the archetype's display name. Without it, FingerprintMatchContributor
///     would have to iterate the All collection on every match to find the right name.
/// </summary>
public sealed class IdentityArchetypeRegistryTests
{
    private static IdentityArchetypeRegistry BuildRegistry(params IdentityArchetype[] archetypes)
    {
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1()));
        if (archetypes.Length > 0) registry.Replace(archetypes);
        return registry;
    }

    private static IdentityArchetype BuildArchetype(string id, string name, string? description = null)
    {
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        return new IdentityArchetype
        {
            ArchetypeId = id,
            Name = name,
            Description = description,
            ArchetypeKind = "test",
            Centroid = new float[dim],
            DimensionMask = new float[dim],
            DescendantCount = 0,
            LastRefinedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public void TryGetById_ReturnsArchetype_WhenIdMatches()
    {
        var registry = BuildRegistry(
            BuildArchetype("python-requests", "Python requests library", "HTTP client"),
            BuildArchetype("chrome-desktop", "Chrome on Desktop"));

        var found = registry.TryGetById("python-requests");

        Assert.NotNull(found);
        Assert.Equal("python-requests", found!.ArchetypeId);
        Assert.Equal("Python requests library", found.Name);
        Assert.Equal("HTTP client", found.Description);
    }

    [Fact]
    public void TryGetById_IsCaseInsensitive()
    {
        var registry = BuildRegistry(BuildArchetype("python-requests", "Python requests library"));

        Assert.NotNull(registry.TryGetById("PYTHON-REQUESTS"));
        Assert.NotNull(registry.TryGetById("Python-Requests"));
    }

    [Fact]
    public void TryGetById_ReturnsNull_WhenIdUnknown()
    {
        var registry = BuildRegistry(BuildArchetype("python-requests", "Python requests"));

        Assert.Null(registry.TryGetById("does-not-exist"));
    }

    [Fact]
    public void TryGetById_ReturnsNull_OnEmptyOrNullInput()
    {
        var registry = BuildRegistry(BuildArchetype("python-requests", "Python requests"));

        Assert.Null(registry.TryGetById(""));
        Assert.Null(registry.TryGetById(null!));
    }
}
