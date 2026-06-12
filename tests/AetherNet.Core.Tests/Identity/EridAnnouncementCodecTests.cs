// SPDX-License-Identifier: MIT

using System;
using System.Text;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Coverage for <see cref="EridAnnouncementCodec"/> — the in-session ERID announcement frame.
/// Round-trip fidelity, magic/version rejection, and an end-to-end with <see cref="EridDirectory"/>
/// where one node's encoded announcement lets the other resolve its rotating address.
/// </summary>
public class EridAnnouncementCodecTests
{
    private static byte[] RoutingKeyFor(string seed)
        => EphemeralRoutingId.DeriveRoutingKey(Encoding.UTF8.GetBytes("identity-secret:" + seed));

    [Fact]
    public void Roundtrip_PreservesKeyAndParams()
    {
        var key = RoutingKeyFor("A");
        var bytes = EridAnnouncementCodec.Encode(key, epochSeconds: 600, eridLength: 20);

        Assert.True(EridAnnouncementCodec.TryDecode(bytes, out var outKey, out var epoch, out var len));
        Assert.Equal(key, outKey);
        Assert.Equal(600, epoch);
        Assert.Equal(20, len);
    }

    [Fact]
    public void Roundtrip_Defaults()
    {
        var key = RoutingKeyFor("A");
        Assert.True(EridAnnouncementCodec.TryDecode(EridAnnouncementCodec.Encode(key), out var outKey, out var epoch, out var len));
        Assert.Equal(key, outKey);
        Assert.Equal(EphemeralRoutingId.DefaultEpochSeconds, epoch);
        Assert.Equal(EphemeralRoutingId.DefaultLength, len);
    }

    [Fact]
    public void TryDecode_RejectsNonAnnouncementBytes()
    {
        Assert.False(EridAnnouncementCodec.TryDecode(new byte[] { 1, 2, 3 }, out _, out _, out _));               // too short
        Assert.False(EridAnnouncementCodec.TryDecode(new byte[32], out _, out _, out _));                          // zero magic
        Assert.False(EridAnnouncementCodec.TryDecode(Encoding.ASCII.GetBytes("not an erid announcement!!"), out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsWrongVersion()
    {
        var bytes = EridAnnouncementCodec.Encode(RoutingKeyFor("A"));
        bytes[4] = 2; // bump the version byte
        Assert.False(EridAnnouncementCodec.TryDecode(bytes, out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsTruncatedKey()
    {
        var bytes = EridAnnouncementCodec.Encode(RoutingKeyFor("A"));
        Assert.False(EridAnnouncementCodec.TryDecode(bytes.AsSpan(0, bytes.Length - 4), out _, out _, out _));
    }

    [Fact]
    public void EndToEnd_AnnouncementLetsPeerResolveRotatingErid()
    {
        const long t = 1_700_000_000;
        var aliceKey = RoutingKeyFor("alice");
        var alice = new EridDirectory(aliceKey);

        // Alice frames her announcement; it travels encrypted inside the session (not modelled here).
        var wire = EridAnnouncementCodec.Encode(aliceKey);

        // Bob decodes it and remembers Alice — now he can address her by her current rotating ERID.
        Assert.True(EridAnnouncementCodec.TryDecode(wire, out var aliceRoutingKey, out _, out _));
        var bob = new EridDirectory(RoutingKeyFor("bob"));
        bob.RememberPeer("alice", aliceRoutingKey);

        Assert.Equal(alice.MyErid(t), bob.EridForPeer("alice", t));
        Assert.Equal("alice", bob.ResolvePeer(alice.MyErid(t), t));
    }

    [Fact]
    public void Encode_RejectsEmptyKey()
        => Assert.Throws<ArgumentException>(() => EridAnnouncementCodec.Encode(Array.Empty<byte>()));
}
