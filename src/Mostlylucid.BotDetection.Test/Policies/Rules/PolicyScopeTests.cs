using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Covers the composite-shape <see cref="PolicyScope"/> record itself --
///     <see cref="PolicyScope.IsWildcard"/>, <see cref="PolicyScope.Specificity"/>,
///     and value equality. The walk + matching semantics are exercised in
///     <c>PolicyResolverTests</c> + <c>PolicyScopeMatcherTests</c>.
/// </summary>
public class PolicyScopeTests
{
    [Fact]
    public void IsWildcard_true_when_all_slots_null()
    {
        Assert.True(new PolicyScope().IsWildcard);
        Assert.True(PolicyScope.Wildcard().IsWildcard);
    }

    [Fact]
    public void IsWildcard_false_when_any_slot_populated()
    {
        Assert.False(PolicyScope.Domain("x.com").IsWildcard);
        Assert.False(new PolicyScope(Method: "POST").IsWildcard);
        Assert.False(new PolicyScope(Geo: "RU").IsWildcard);
        Assert.False(new PolicyScope(Identity: new IdentityScope.NamedBot("googlebot")).IsWildcard);
    }

    [Fact]
    public void Specificity_zero_for_wildcard()
    {
        Assert.Equal(0, PolicyScope.Wildcard().Specificity);
    }

    [Fact]
    public void Specificity_host_endpoint_dominates_subdomain_dominates_domain()
    {
        var endpoint = PolicyScope.Endpoint("x.com", "docs.x.com", "GET /api/upload");
        var subdomain = PolicyScope.Subdomain("x.com", "docs.x.com");
        var domain = PolicyScope.Domain("x.com");

        Assert.True(endpoint.Specificity > subdomain.Specificity);
        Assert.True(subdomain.Specificity > domain.Specificity);
        Assert.True(domain.Specificity > 0);
    }

    [Fact]
    public void Specificity_sums_orthogonal_slots()
    {
        var hostOnly = PolicyScope.Domain("x.com");
        var hostAndMethod = new PolicyScope(Host: new HostScope.Domain("x.com"), Method: "POST");
        var hostMethodGeo = new PolicyScope(Host: new HostScope.Domain("x.com"), Method: "POST", Geo: "RU");
        var allFour = new PolicyScope(
            Host: new HostScope.Domain("x.com"),
            Method: "POST",
            Geo: "RU",
            Identity: new IdentityScope.NamedBot("googlebot"));

        Assert.Equal(hostOnly.Specificity + 1, hostAndMethod.Specificity);
        Assert.Equal(hostAndMethod.Specificity + 1, hostMethodGeo.Specificity);
        Assert.Equal(hostMethodGeo.Specificity + 1, allFour.Specificity);
    }

    [Fact]
    public void Specificity_url_slot_dominates_orthogonal_slots()
    {
        // An Endpoint host (+8) beats a wildcard rule with every orthogonal
        // slot populated (Method + Geo + Identity = 3). That's the spec:
        // URL hierarchy stays the dominant axis when scopes collide.
        var endpoint = PolicyScope.Endpoint("x.com", "docs.x.com", "GET /api/upload");
        var orthogonal = new PolicyScope(
            Method: "POST", Geo: "RU", Identity: new IdentityScope.NamedBot("googlebot"));

        Assert.True(endpoint.Specificity > orthogonal.Specificity);
    }

    [Fact]
    public void Value_equality_holds_across_constructors()
    {
        var a = PolicyScope.Domain("x.com");
        var b = new PolicyScope(Host: new HostScope.Domain("x.com"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_equality_distinguishes_orthogonal_slots()
    {
        var a = new PolicyScope(Method: "GET");
        var b = new PolicyScope(Method: "POST");
        Assert.NotEqual(a, b);
    }
}
