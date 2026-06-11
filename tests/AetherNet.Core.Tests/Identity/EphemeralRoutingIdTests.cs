// SPDX-License-Identifier: MIT

using System;
using System.Text;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Coverage for <see cref="EphemeralRoutingId"/> — the rotating, key-derived wire address that
/// replaces the stable phone-derived UHID. The privacy guarantees under test:
/// <list type="bullet">
///   <item>same node + same window → same address (so an in-session peer can resolve it),</item>
///   <item>same node + next window → a DIFFERENT, uncorrelated address (no cross-time tracking),</item>
///   <item>different nodes never share a wire address,</item>
///   <item>the routing key is a 256-bit secret distinct from the seed (not the public key).</item>
/// </list>
/// </summary>
public class EphemeralRoutingIdTests
{
    private static byte[] Key(string secret)
        => EphemeralRoutingId.DeriveRoutingKey(Encoding.ASCII.GetBytes(secret));

    [Fact]
    public void Derive_IsDeterministic_ForSameKeyAndEpoch()
    {
        var k = Key("node-secret-A");
        Assert.Equal(
            EphemeralRoutingId.DeriveForEpoch(k, 12345),
            EphemeralRoutingId.DeriveForEpoch(k, 12345));
    }

    [Fact]
    public void Derive_Rotates_AcrossConsecutiveEpochs()
    {
        var k = Key("node-secret-A");
        // Consecutive windows must be unlinkable on the wire — this is the whole point.
        Assert.NotEqual(
            EphemeralRoutingId.DeriveForEpoch(k, 100),
            EphemeralRoutingId.DeriveForEpoch(k, 101));
    }

    [Fact]
    public void Derive_DiffersByNode_InTheSameEpoch()
    {
        Assert.NotEqual(
            EphemeralRoutingId.DeriveForEpoch(Key("node-A"), 7),
            EphemeralRoutingId.DeriveForEpoch(Key("node-B"), 7));
    }

    [Fact]
    public void Erid_HasExpectedLength_AndUsesCrockfordAlphabetOnly()
    {
        var id = EphemeralRoutingId.DeriveForEpoch(Key("n"), 1);
        Assert.Equal(EphemeralRoutingId.DefaultLength, id.Length);
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I/L/O/U
        Assert.All(id, c => Assert.Contains(c, alphabet));
    }

    [Theory]
    [InlineData(0, 900, 0)]
    [InlineData(899, 900, 0)]
    [InlineData(900, 900, 1)]
    [InlineData(1800, 900, 2)]
    [InlineData(1234567, 900, 1371)]
    public void EpochFor_ComputesTheWindowIndex(long unixSeconds, int epochSeconds, long expected)
        => Assert.Equal(expected, EphemeralRoutingId.EpochFor(unixSeconds, epochSeconds));

    [Fact]
    public void Derive_IsStableWithinAWindow_ButChangesAtTheBoundary()
    {
        var k = Key("n");
        // 1000 and 1500 both fall inside window 1 ([900, 1799]) → same ERID...
        Assert.Equal(
            EphemeralRoutingId.Derive(k, 1000),
            EphemeralRoutingId.Derive(k, 1500));
        // ...2000 falls in window 2 ([1800, 2699]) → a different ERID.
        Assert.NotEqual(
            EphemeralRoutingId.Derive(k, 1000),
            EphemeralRoutingId.Derive(k, 2000));
    }

    [Fact]
    public void DeriveRoutingKey_IsDeterministic_256Bit_AndDistinctFromTheSeed()
    {
        var seed = Encoding.ASCII.GetBytes("ed25519-private-key-material-seed");
        var k1 = EphemeralRoutingId.DeriveRoutingKey(seed);
        var k2 = EphemeralRoutingId.DeriveRoutingKey(seed);

        Assert.Equal(k1, k2);                 // deterministic for a given identity
        Assert.Equal(32, k1.Length);          // 256-bit routing key
        Assert.NotEqual(seed, k1);            // never the raw seed
        Assert.NotEqual(
            EphemeralRoutingId.DeriveRoutingKey(Encoding.ASCII.GetBytes("a-different-identity")),
            k1);                              // different identity → different schedule
    }

    [Fact]
    public void DeriveForEpoch_RejectsEmptyKey()
        => Assert.Throws<ArgumentException>(
            () => EphemeralRoutingId.DeriveForEpoch(Array.Empty<byte>(), 1));

    [Fact]
    public void DeriveRoutingKey_RejectsEmptySecret()
        => Assert.Throws<ArgumentException>(
            () => EphemeralRoutingId.DeriveRoutingKey(Array.Empty<byte>()));
}
