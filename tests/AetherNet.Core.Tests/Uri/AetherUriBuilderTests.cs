// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Addressing;
using Xunit;

namespace AetherNet.Core.Tests.Uri;

public class AetherUriBuilderTests
{
    [Fact]
    public void Builder_AuthorityFromTag_Succeeds()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)i;
        var tag = AetherNetTag.FromPublicKey(key);
        var u = new AetherUriBuilder()
            .WithAuthority(tag)
            .WithPath("profile")
            .Build();
        Assert.Equal(tag.Value, u.Authority);
        Assert.Equal("profile", u.Path);
    }

    [Fact]
    public void Builder_FluentChain_RendersCorrectly()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .WithPath("content/sha256-abc")
            .WithQueryParam("codec", "opus")
            .WithFragment("t=1m30s")
            .Build();
        Assert.Equal("aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s", u.ToString());
    }

    [Fact]
    public void Builder_AppendPathSegment_BuildsPath()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .AppendPathSegment("watch")
            .AppendPathSegment("sess-99")
            .AppendPathSegment("join")
            .Build();
        Assert.Equal("watch/sess-99/join", u.Path);
    }

    [Fact]
    public void Builder_RemoveQueryParam_DropsKey()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .WithPath("x")
            .WithQueryParam("a", "1")
            .WithQueryParam("b", "2")
            .RemoveQueryParam("a")
            .Build();
        Assert.False(u.Query.ContainsKey("a"));
        Assert.Equal("2", u.Query["b"]);
    }

    [Fact]
    public void Builder_StripLeadingSlashOnPath()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .WithPath("/profile")
            .Build();
        Assert.Equal("profile", u.Path);
    }

    [Fact]
    public void Builder_StripLeadingHashOnFragment()
    {
        var u = new AetherUriBuilder()
            .WithAuthority("KXJB7-MN2P4")
            .WithFragment("#anchor")
            .Build();
        Assert.Equal("anchor", u.Fragment);
    }

    [Fact]
    public void Builder_MissingAuthority_ThrowsOnBuild()
    {
        Assert.Throws<AetherUriException>(() =>
            new AetherUriBuilder().WithPath("x").Build());
    }

    [Fact]
    public void Builder_BadAuthorityString_Throws()
    {
        Assert.Throws<AetherUriException>(() =>
            new AetherUriBuilder().WithAuthority("not-an-id"));
    }
}
