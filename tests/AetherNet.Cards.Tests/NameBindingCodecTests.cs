// SPDX-License-Identifier: MIT

using AetherNet.Content;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// The signable body must be deterministic (so signatures reproduce) and must bind every field (so a
/// changed version or content can't ride an old signature).
/// </summary>
public class NameBindingCodecTests
{
    private static byte[] Key() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void BuildSignableBody_IsDeterministic()
    {
        var a = NameBindingCodec.BuildSignableBody("abc", Key(), 3, "roothash");
        var b = NameBindingCodec.BuildSignableBody("abc", Key(), 3, "roothash");
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildSignableBody_BindsVersion()
    {
        var a = NameBindingCodec.BuildSignableBody("abc", Key(), 3, "roothash");
        var b = NameBindingCodec.BuildSignableBody("abc", Key(), 4, "roothash");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildSignableBody_BindsRootHash()
    {
        var a = NameBindingCodec.BuildSignableBody("abc", Key(), 3, "root-1");
        var b = NameBindingCodec.BuildSignableBody("abc", Key(), 3, "root-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildSignableBody_BindsNameHash()
    {
        var a = NameBindingCodec.BuildSignableBody("name-1", Key(), 3, "roothash");
        var b = NameBindingCodec.BuildSignableBody("name-2", Key(), 3, "roothash");
        Assert.NotEqual(a, b);
    }
}
