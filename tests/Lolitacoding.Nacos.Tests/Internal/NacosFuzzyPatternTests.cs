using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the fuzzy-watch pattern matcher and group-key codec against the rules the server and the
/// official client apply.
/// </summary>
/// <remarks>
/// These two pieces decide which pushes a client believes belong to a watch, and neither rule is
/// obvious: a pattern is not a glob, and the codec escapes exactly two characters. The expected
/// values are written as literals rather than recomputed from the implementation, so a change to
/// either side shows up as a failure here instead of as a silently different matched set.
/// </remarks>
public sealed class NacosFuzzyPatternTests
{
    private const string DefaultGroup = "DEFAULT_GROUP";

    [Theory]
    [InlineData("public", "DEFAULT_GROUP", "app.*", "public>>DEFAULT_GROUP>>app.*")]
    [InlineData("dev", "*", "*", "dev>>*>>*")]
    [InlineData(null, "DEFAULT_GROUP", "*", "public>>DEFAULT_GROUP>>*")]
    [InlineData("", "DEFAULT_GROUP", "*", "public>>DEFAULT_GROUP>>*")]
    [InlineData("  ", "DEFAULT_GROUP", "*", "public>>DEFAULT_GROUP>>*")]
    public void Generate_TreatsABlankNamespaceAsPublic(
        string? @namespace,
        string group,
        string resource,
        string expected)
    {
        NacosFuzzyPattern.Generate(@namespace, group, resource).ShouldBe(expected);
    }

    [Fact]
    public void Generate_RejectsANullPattern()
    {
        Should.Throw<ArgumentNullException>(() => NacosFuzzyPattern.Generate("public", null!, "*"));
        Should.Throw<ArgumentNullException>(() => NacosFuzzyPattern.Generate("public", DefaultGroup, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_RejectsABlankPattern(string pattern)
    {
        Should.Throw<ArgumentException>(() => NacosFuzzyPattern.Generate("public", pattern, "*"));
        Should.Throw<ArgumentException>(() => NacosFuzzyPattern.Generate("public", DefaultGroup, pattern));
    }

    [Theory]
    // A pattern without an asterisk is a literal, not a glob.
    [InlineData("app", "app", true)]
    [InlineData("app", "app2", false)]
    [InlineData("app", "APP", false)]
    // A single asterisk matches anything, including the empty string.
    [InlineData("*", "", true)]
    [InlineData("*", "anything", true)]
    // A trailing asterisk anchors the start, a leading one the end.
    [InlineData("app*", "app.one", true)]
    [InlineData("app*", "my.app", false)]
    [InlineData("*app", "my.app", true)]
    [InlineData("*app", "app.one", false)]
    // Asterisks on both ends search the middle.
    [InlineData("*app*", "my.app.one", true)]
    [InlineData("*app*", "my.one", false)]
    // The official matcher strips every asterisk rather than treating one in the middle as a glob,
    // so "a*b" neither starts nor ends with one and matches nothing.
    [InlineData("a*b", "ab", false)]
    [InlineData("a*b", "a-b", false)]
    public void SegmentMatches_FollowsTheOfficialMatcher(string pattern, string value, bool expected)
    {
        NacosFuzzyPattern.SegmentMatches(pattern, value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("public>>DEFAULT_GROUP>>app*", "public", "DEFAULT_GROUP", "app.one", true)]
    [InlineData("public>>DEFAULT_GROUP>>app*", null, "DEFAULT_GROUP", "app.one", true)]
    [InlineData("public>>*>>*", "public", "TENANT_GROUP", "anything", true)]
    [InlineData("public>>DEFAULT_GROUP>>app*", "other", "DEFAULT_GROUP", "app.one", false)]
    [InlineData("public>>DEFAULT_GROUP>>app*", "public", "OTHER_GROUP", "app.one", false)]
    [InlineData("public>>DEFAULT_GROUP>>app*", "public", "DEFAULT_GROUP", "other", false)]
    public void Matches_RequiresEverySegmentToMatch(
        string pattern,
        string? @namespace,
        string group,
        string resource,
        bool expected)
    {
        NacosFuzzyPattern.Matches(pattern, @namespace, group, resource).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("public>>DEFAULT_GROUP")]
    [InlineData("public>>DEFAULT_GROUP>>app>>extra")]
    public void Matches_RejectsAPatternThatIsNotThreeSegments(string pattern)
    {
        // A malformed pattern matches nothing rather than throwing: it would arrive on the read loop,
        // which carries every other notification as well.
        NacosFuzzyPattern.Matches(pattern, "public", DefaultGroup, "app").ShouldBeFalse();
    }

    [Theory]
    [InlineData("app", "DEFAULT_GROUP", null, "app+DEFAULT_GROUP")]
    [InlineData("app", "DEFAULT_GROUP", "", "app+DEFAULT_GROUP")]
    [InlineData("app", "DEFAULT_GROUP", "public", "app+DEFAULT_GROUP+public")]
    // The codec escapes exactly the two characters it introduces, because those are the only ones
    // that would otherwise be read back as a separator or an escape.
    [InlineData("a+b", "g%h", "t+%", "a%2Bb+g%25h+t%2B%25")]
    public void EncodeGroupKey_AppliesTheTwoEscapes(
        string dataId,
        string group,
        string? tenant,
        string expected)
    {
        NacosFuzzyPattern.EncodeGroupKey(dataId, group, tenant).ShouldBe(expected);
    }

    [Theory]
    [InlineData("app", "DEFAULT_GROUP", "public")]
    [InlineData("a+b", "g%h", "t+%")]
    [InlineData("配置", "分组", null)]
    public void RoundTrip_PreservesEveryComponent(string dataId, string group, string? tenant)
    {
        string key = NacosFuzzyPattern.EncodeGroupKey(dataId, group, tenant);

        NacosFuzzyPattern.TryParseGroupKey(key, out string parsedDataId, out string parsedGroup, out string? parsedTenant)
            .ShouldBeTrue();

        parsedDataId.ShouldBe(dataId);
        parsedGroup.ShouldBe(group);
        parsedTenant.ShouldBe(string.IsNullOrEmpty(tenant) ? null : tenant);
    }

    [Theory]
    [InlineData("app+DEFAULT_GROUP", "app", "DEFAULT_GROUP", null)]
    [InlineData("app+DEFAULT_GROUP+public", "app", "DEFAULT_GROUP", "public")]
    [InlineData("a%2Bb+g%25h+t%2B", "a+b", "g%h", "t+")]
    public void TryParseGroupKey_DecodesTheTwoEscapes(
        string key,
        string dataId,
        string group,
        string? tenant)
    {
        NacosFuzzyPattern.TryParseGroupKey(key, out string parsedDataId, out string parsedGroup, out string? parsedTenant)
            .ShouldBeTrue();

        parsedDataId.ShouldBe(dataId);
        parsedGroup.ShouldBe(group);
        parsedTenant.ShouldBe(tenant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("+DEFAULT_GROUP")]
    [InlineData("app+")]
    [InlineData("app+DEFAULT_GROUP+public+extra")]
    // The decoder accepts no escape other than the two the encoder produces, so a percent sign from
    // another producer is reported rather than guessed at.
    [InlineData("app%41+DEFAULT_GROUP")]
    [InlineData("app%2+DEFAULT_GROUP")]
    [InlineData("app+DEFAULT_GROUP%")]
    public void TryParseGroupKey_RejectsAMalformedKey(string? key)
    {
        NacosFuzzyPattern.TryParseGroupKey(key, out string dataId, out string group, out string? tenant)
            .ShouldBeFalse();

        dataId.ShouldBeEmpty();
        group.ShouldBeEmpty();
        tenant.ShouldBeNull();
    }
}
