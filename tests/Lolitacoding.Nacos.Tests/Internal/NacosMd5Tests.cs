using System.Text;
using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the change-detection hash matches what a Nacos server computes.
/// </summary>
/// <remarks>
/// The expected values are hand-written literals rather than calls back into the implementation:
/// a test that recomputes the hash the same way the code does would pass even if both were wrong,
/// and the only thing that matters here is agreement with the server.
/// </remarks>
public sealed class NacosMd5Tests
{
    [Fact]
    public void Compute_MatchesTheKnownDigestOfAnEmptyString()
    {
        // The MD5 of the empty byte sequence, and the value the protocol specifies for it.
        NacosMd5.Compute(string.Empty).ShouldBe("d41d8cd98f00b204e9800998ecf8427e");
    }

    [Fact]
    public void Compute_MatchesTheKnownDigestOfAsciiText()
    {
        NacosMd5.Compute("hello").ShouldBe("5d41402abc4b2a76b9719d911017c592");
    }

    [Fact]
    public void Compute_HashesUtf8BytesRatherThanUtf16()
    {
        // "中" is three UTF-8 bytes. Hashing the .NET string's UTF-16 representation, or using the
        // platform default encoding, would produce a different digest and silently break change
        // detection for every non-ASCII configuration. This digest is the MD5 of the UTF-8 bytes
        // E4 B8 AD, verified independently against a reference implementation.
        NacosMd5.Compute("中").ShouldBe("aed1dfbc31703955e64806b799b67645");
    }

    [Fact]
    public void Compute_IsLowercaseHexadecimal()
    {
        string digest = NacosMd5.Compute("Some Content");

        digest.Length.ShouldBe(32);
        digest.ShouldBe(digest.ToLowerInvariant());
    }

    [Fact]
    public void Compute_ProducesTheSameDigestForTheSameInput()
    {
        NacosMd5.Compute("stable").ShouldBe(NacosMd5.Compute("stable"));
    }

    [Fact]
    public void Compute_ProducesDifferentDigestsForDifferentInput()
    {
        NacosMd5.Compute("value-a").ShouldNotBe(NacosMd5.Compute("value-b"));
    }

    [Fact]
    public void Compute_ThrowsForNull()
    {
        Should.Throw<ArgumentNullException>(() => NacosMd5.Compute(null!));
    }

    [Fact]
    public void Compute_HandlesMultiLineContentWithTrailingNewline()
    {
        // Trailing whitespace is significant: a server that stored the newline would otherwise
        // appear to disagree with a client that trimmed it.
        string content = "line1\nline2\n";

        NacosMd5.Compute(content).ShouldNotBe(NacosMd5.Compute("line1\nline2"));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "ABC", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData("", "", false)]
    [InlineData("", "abc", false)]
    public void AreEqual_ComparesHashesTolerantly(string? left, string? right, bool expected)
    {
        NacosMd5.AreEqual(left, right).ShouldBe(expected);
    }
}
