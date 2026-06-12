// SPDX-License-Identifier: MIT

using System;
using System.Text;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Coverage for <see cref="EridDirectory"/> — the in-session ERID resolver. The headline proof is
/// that two nodes which have exchanged routingKeys can follow each other's ROTATING wire address
/// across epochs, while an outsider holding no key cannot link the address to anyone.
/// </summary>
public class EridDirectoryTests
{
    private static byte[] RoutingKeyFor(string seed)
        => EphemeralRoutingId.DeriveRoutingKey(Encoding.UTF8.GetBytes("identity-secret:" + seed));

    private const long T = 1_700_000_000;                                        // mid-epoch
    private const long TNextWindow = T + EphemeralRoutingId.DefaultEpochSeconds;  // next 15-min window

    [Fact]
    public void MyErid_IsStableWithinWindow_AndRotatesAcrossWindows()
    {
        var dir = new EridDirectory(RoutingKeyFor("A"));
        Assert.Equal(dir.MyErid(T), dir.MyErid(T + 1));            // same window → same ERID
        Assert.NotEqual(dir.MyErid(T), dir.MyErid(TNextWindow));  // next window → different ERID
    }

    [Fact]
    public void EridForPeer_IsNull_ForUnknownPeer()
        => Assert.Null(new EridDirectory(RoutingKeyFor("A")).EridForPeer("bob", T));

    [Fact]
    public void TwoNodes_ResolveEachOthersRotatingErid_AcrossWindows()
    {
        var aKey = RoutingKeyFor("A");
        var bKey = RoutingKeyFor("B");
        var alice = new EridDirectory(aKey);
        var bob = new EridDirectory(bKey);

        // In-session exchange: each side remembers the other's routingKey.
        alice.RememberPeer("bob", bKey);
        bob.RememberPeer("alice", aKey);

        // Alice addresses Bob by his CURRENT rotating ERID — exactly the address Bob presents.
        Assert.Equal(bob.MyErid(T), alice.EridForPeer("bob", T));
        // Bob reverse-resolves an inbound ERID (Alice's) back to the stable "alice".
        Assert.Equal("alice", bob.ResolvePeer(alice.MyErid(T), T));

        // Next window: the ERID has rotated, but the established relationship still resolves it.
        Assert.NotEqual(alice.EridForPeer("bob", T), alice.EridForPeer("bob", TNextWindow));
        Assert.Equal(bob.MyErid(TNextWindow), alice.EridForPeer("bob", TNextWindow));
        Assert.Equal("alice", bob.ResolvePeer(alice.MyErid(TNextWindow), TNextWindow));
    }

    [Fact]
    public void Outsider_WithNoKeys_CannotLinkAnEridToAnyone()
    {
        var alice = new EridDirectory(RoutingKeyFor("A"));
        var outsider = new EridDirectory(RoutingKeyFor("X")); // never told anyone's routingKey

        // The outsider sees Alice's ERID on the wire but holds no key to tie it to her.
        Assert.Null(outsider.ResolvePeer(alice.MyErid(T), T));
    }

    [Fact]
    public void ResolvePeer_AcrossEpochs_DoesNotCrossLink()
    {
        var bKey = RoutingKeyFor("B");
        var bob = new EridDirectory(bKey);
        var alice = new EridDirectory(RoutingKeyFor("A"));
        alice.RememberPeer("bob", bKey);

        // Bob's ERID from THIS window must not resolve under NEXT window's schedule — no cross-time link.
        Assert.Null(alice.ResolvePeer(bob.MyErid(T), TNextWindow));
    }

    [Fact]
    public void ForgetPeer_StopsResolution()
    {
        var alice = new EridDirectory(RoutingKeyFor("A"));
        alice.RememberPeer("bob", RoutingKeyFor("B"));
        Assert.NotNull(alice.EridForPeer("bob", T));

        Assert.True(alice.ForgetPeer("bob"));
        Assert.Null(alice.EridForPeer("bob", T));
        Assert.False(alice.ForgetPeer("bob")); // already gone
    }

    [Fact]
    public void Ctor_RejectsEmptyRoutingKey()
        => Assert.Throws<ArgumentException>(() => new EridDirectory(Array.Empty<byte>()));
}
