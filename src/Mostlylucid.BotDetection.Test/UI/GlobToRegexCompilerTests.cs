using FluentAssertions;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class GlobToRegexCompilerTests
{
    [Theory]
    [InlineData("/api/users/*",    @"^/api/users/[^/]+$")]    // single * = one path segment
    [InlineData("/admin/**",       @"^/admin/.*$")]            // ** = any depth
    [InlineData("*.example.com",   @"^[^.]+\.example\.com$")]  // domain wildcard
    [InlineData("/exact/path",     @"^/exact/path$")]          // literal
    [InlineData("/api/v?/users",   @"^/api/v[^/]/users$")]     // ? = single non-slash char
    [InlineData("/path.with.dots", @"^/path\.with\.dots$")]    // dots escaped
    [InlineData("/path+plus",      @"^/path\+plus$")]          // + escaped
    [InlineData("/path(paren)",    @"^/path\(paren\)$")]       // parens escaped
    [InlineData("/path[bracket]",  @"^/path\[bracket\]$")]     // brackets escaped
    [InlineData("/a/**/b",         @"^/a/.*/b$")]              // ** in middle
    public void Compile_translates_globs_to_anchored_regex(string glob, string expectedRegex)
    {
        GlobToRegexCompiler.Compile(glob).Should().Be(expectedRegex);
    }

    [Fact]
    public void Compile_empty_returns_anchored_empty()
    {
        GlobToRegexCompiler.Compile("").Should().Be("^$");
    }

    [Fact]
    public void Compile_null_throws_ArgumentNullException()
    {
        var act = () => GlobToRegexCompiler.Compile(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
