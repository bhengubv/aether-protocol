// SPDX-License-Identifier: MIT

using AetherNet.Uri;
using Xunit;

namespace AetherNet.Core.Tests.Uri;

public class AetherUriParseTests
{
    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AuthorityOnly_Succeeds()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4");
        Assert.Equal("KXJB7-MN2P4", u.Authority);
        Assert.Equal(string.Empty, u.Path);
        Assert.Empty(u.Query);
        Assert.Equal(string.Empty, u.Fragment);
    }

    [Fact]
    public void Parse_AuthorityWithoutDash_CanonicalisesToWithDash()
    {
        var u = AetherUri.Parse("aether://KXJB7MN2P4");
        Assert.Equal("KXJB7-MN2P4", u.Authority);
    }

    [Fact]
    public void Parse_AuthorityLowercase_CanonicalisesToUpper()
    {
        var u = AetherUri.Parse("aether://kxjb7-mn2p4");
        Assert.Equal("KXJB7-MN2P4", u.Authority);
    }

    [Fact]
    public void Parse_AuthorityWithPath_Succeeds()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/profile");
        Assert.Equal("profile", u.Path);
        Assert.Equal("profile", u.HandlerName);
    }

    [Fact]
    public void Parse_AuthorityWithMultiSegmentPath_Succeeds()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/content/sha256-abc123");
        Assert.Equal("content/sha256-abc123", u.Path);
        Assert.Equal("content", u.HandlerName);
        Assert.Equal(new[] { "content", "sha256-abc123" }, u.PathSegments);
    }

    [Fact]
    public void Parse_WithQuery_Succeeds()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128");
        Assert.Equal("opus", u.Query["codec"]);
        Assert.Equal("128", u.Query["bitrate"]);
    }

    [Fact]
    public void Parse_QueryKey_IsCaseInsensitive()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/x?Codec=opus");
        Assert.Equal("opus", u.Query["codec"]);
        Assert.Equal("opus", u.Query["CODEC"]);
    }

    [Fact]
    public void Parse_WithFragment_Succeeds()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/stream/live#t=1m30s");
        Assert.Equal("t=1m30s", u.Fragment);
    }

    [Fact]
    public void Parse_WithEmptyValueQueryParam_TreatsAsEmpty()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/x?flag");
        Assert.True(u.Query.ContainsKey("flag"));
        Assert.Equal(string.Empty, u.Query["flag"]);
    }

    [Fact]
    public void Parse_Uhid64Hex_Succeeds()
    {
        var hex = new string('a', 64);
        var u = AetherUri.Parse($"aether://{hex}/inbox");
        Assert.Equal(hex.ToUpperInvariant(), u.Authority);
        Assert.Equal("inbox", u.HandlerName);
    }

    [Fact]
    public void Parse_PercentEncodedQuery_Decodes()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/inbox?title=hello%20world");
        Assert.Equal("hello world", u.Query["title"]);
    }

    [Fact]
    public void Parse_PercentEncodedPathSegment_Decodes()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/inbox/Hello%20World");
        Assert.Equal(new[] { "inbox", "Hello World" }, u.PathSegments);
    }

    [Fact]
    public void Parse_PercentEncodedUtf8_Decodes()
    {
        // "café" → c, a, f, é (UTF-8 c3 a9)
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9");
        Assert.Equal("café", u.Query["title"]);
    }

    [Fact]
    public void Parse_FragmentNotInQuery()
    {
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/x?a=b#frag");
        Assert.Equal("b", u.Query["a"]);
        Assert.Equal("frag", u.Fragment);
    }

    // ── Failure paths ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("http://KXJB7-MN2P4/")]
    [InlineData("aether:KXJB7-MN2P4")]
    [InlineData("aether:/KXJB7-MN2P4")]
    public void TryParse_BadScheme_Fails(string input)
    {
        Assert.False(AetherUri.TryParse(input, out _, out _));
    }

    [Fact]
    public void TryParse_NullInput_Fails()
    {
        Assert.False(AetherUri.TryParse(null!, out _, out _));
    }

    [Fact]
    public void TryParse_EmptyAuthority_Fails()
    {
        Assert.False(AetherUri.TryParse("aether:///profile", out _, out _));
    }

    [Fact]
    public void TryParse_BadAuthority_Fails()
    {
        // Contains "I" — not a Crockford char.
        Assert.False(AetherUri.TryParse("aether://INVALID-AUTH1/x", out _, out _));
    }

    [Fact]
    public void TryParse_TooShortAuthority_Fails()
    {
        Assert.False(AetherUri.TryParse("aether://ABC", out _, out _));
    }

    [Fact]
    public void TryParse_ConsecutiveSlashesInPath_Fails()
    {
        Assert.False(AetherUri.TryParse("aether://KXJB7-MN2P4/a//b", out _, out _));
    }

    [Fact]
    public void TryParse_IllegalPathCharacter_Fails()
    {
        Assert.False(AetherUri.TryParse("aether://KXJB7-MN2P4/has space", out _, out _));
    }

    [Fact]
    public void TryParse_MalformedPercentEncoding_Fails()
    {
        Assert.False(AetherUri.TryParse("aether://KXJB7-MN2P4/inbox/%2", out _, out _));
    }

    [Fact]
    public void TryParse_EmptyQueryKey_Fails()
    {
        Assert.False(AetherUri.TryParse("aether://KXJB7-MN2P4/x?=value", out _, out _));
    }

    [Fact]
    public void Parse_BadInput_ThrowsAetherUriException()
    {
        Assert.Throws<AetherUriException>(() => AetherUri.Parse("not-a-uri"));
    }

    // ── Round-trip ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("aether://KXJB7-MN2P4")]
    [InlineData("aether://KXJB7-MN2P4/profile")]
    [InlineData("aether://KXJB7-MN2P4/content/sha256-abc")]
    [InlineData("aether://KXJB7-MN2P4/stream/live#t=1m30s")]
    public void RoundTrip_Canonical_Stable(string input)
    {
        var parsed = AetherUri.Parse(input);
        var rendered = parsed.ToString();
        var reparsed = AetherUri.Parse(rendered);
        Assert.Equal(parsed, reparsed);
        Assert.Equal(rendered, reparsed.ToString());
    }

    [Fact]
    public void ToString_EncodesSpaces()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .WithPath("inbox")
            .WithQueryParam("title", "hello world")
            .Build();
        // Builder calls Parse internally; toString should encode the space.
        Assert.Contains("hello%20world", u.ToString());
    }

    // ── Equality ───────────────────────────────────────────────────────────

    [Fact]
    public void Equality_SameContent_Equal()
    {
        var a = AetherUri.Parse("aether://KXJB7-MN2P4/x?k=v");
        var b = AetherUri.Parse("aether://KXJB7-MN2P4/x?k=v");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentAuthority_NotEqual()
    {
        var a = AetherUri.Parse("aether://KXJB7-MN2P4/x");
        var b = AetherUri.Parse("aether://KXJB7-MN2P5/x");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_QueryOrderIrrelevant()
    {
        var a = AetherUri.Parse("aether://KXJB7-MN2P4/x?a=1&b=2");
        var b = AetherUri.Parse("aether://KXJB7-MN2P4/x?b=2&a=1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void IsValid_Default_IsFalse()
    {
        var u = default(AetherUri);
        Assert.False(u.IsValid);
    }
}
