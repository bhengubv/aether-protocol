// SPDX-License-Identifier: MIT

using System.Text;
using System.Threading.Tasks;
using AetherNet.Identity;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// End-to-end coverage for <see cref="EridExchangeService"/> against the REAL Signal stack: a node
/// shares its routingKey sealed inside the session, and the peer — after decrypting — can resolve
/// that node's rotating wire address. Proves the in-session exchange works through real crypto,
/// not a fake.
/// </summary>
public class EridExchangeServiceTests
{
    private sealed record Node(
        SignalProtocolService Signal,
        EridDirectory Directory,
        EridExchangeService Exchange,
        PreKeyBundle Bundle);

    private static async Task<Node> NewNodeAsync(string uhid)
    {
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bundle = await signal.GeneratePreKeyBundleAsync(uhid); // single init → stable identity
        var key = signal.DeriveEridRoutingKey();
        var dir = new EridDirectory(key);
        return new Node(signal, dir, new EridExchangeService(dir, signal, key), bundle);
    }

    [Fact]
    public async Task InSessionAnnouncement_LetsPeerResolveOurRotatingErid()
    {
        const long t = 1_700_000_000;
        var alice = await NewNodeAsync("alice");
        var bob = await NewNodeAsync("bob");

        // Alice processes Bob's bundle (X3DH) → she can seal a message to Bob.
        await alice.Signal.ProcessPreKeyBundleAsync(bob.Bundle);

        // Alice announces her routingKey, sealed inside the session.
        var sealedPayload = await alice.Exchange.CreateAnnouncementAsync("bob");
        Assert.NotNull(sealedPayload);

        // Bob decrypts (establishing his receiving session) and processes the announcement.
        var plaintext = await bob.Signal.DecryptAsync("alice", sealedPayload!);
        Assert.True(bob.Exchange.TryProcessInbound("alice", plaintext));

        // Bob can now resolve Alice's CURRENT rotating ERID — matching exactly what Alice presents.
        Assert.Equal(alice.Directory.MyErid(t), bob.Directory.EridForPeer("alice", t));
        Assert.Equal("alice", bob.Directory.ResolvePeer(alice.Directory.MyErid(t), t));
    }

    [Fact]
    public async Task CreateAnnouncement_ReturnsNull_WithoutASession()
    {
        var alice = await NewNodeAsync("alice");
        // No session with "stranger" → nothing to ride inside, so no announcement is produced.
        Assert.Null(await alice.Exchange.CreateAnnouncementAsync("stranger"));
    }

    [Fact]
    public async Task TryProcessInbound_IgnoresNonAnnouncementPayload()
    {
        var bob = await NewNodeAsync("bob");
        Assert.False(bob.Exchange.TryProcessInbound("alice", Encoding.UTF8.GetBytes("hello, not an erid")));
        Assert.Equal(0, bob.Directory.KnownPeerCount);
    }
}
