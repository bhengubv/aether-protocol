// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Dht;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class DhtNodeTests
{
    [Fact]
    public async Task Ping_returns_responder_id()
    {
        await using var a = new DhtNode();
        a.Start();
        await using var b = new DhtNode();
        b.Start();

        var id = await b.PingAsync(a.LocalEndPoint).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(a.Id, id);
    }

    [Fact]
    public async Task Get_peers_announce_and_iterative_find_over_udp()
    {
        await using var a = new DhtNode();
        a.Start();
        await using var b = new DhtNode();
        b.Start();

        var infoHash = Enumerable.Repeat((byte)0x55, 20).ToArray();

        // b learns a.
        await b.BootstrapAsync(a.LocalEndPoint).WaitAsync(TimeSpan.FromSeconds(10));

        // Before anyone announces, get_peers yields a token but no peers.
        var (token, peersBefore, _) = await b.GetPeersAsync(a.LocalEndPoint, infoHash).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(peersBefore);
        Assert.NotEmpty(token);

        // b announces itself on port 12345 for this info-hash, using the token a issued.
        await b.AnnouncePeerAsync(a.LocalEndPoint, infoHash, port: 12345, token).WaitAsync(TimeSpan.FromSeconds(10));

        // An iterative lookup from b now finds the announced peer (via a).
        var found = await b.FindPeersAsync(infoHash).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(found, p => p.Port == 12345);
    }

    [Fact]
    public async Task Announce_with_bad_token_is_rejected()
    {
        await using var a = new DhtNode();
        a.Start();
        await using var b = new DhtNode();
        b.Start();

        var infoHash = Enumerable.Repeat((byte)0x77, 20).ToArray();
        await b.BootstrapAsync(a.LocalEndPoint).WaitAsync(TimeSpan.FromSeconds(10));

        // A forged token → a rejects the announce (KRPC error surfaces as a timeout/exception),
        // and crucially nothing is stored.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            b.AnnouncePeerAsync(a.LocalEndPoint, infoHash, port: 999, token: new byte[] { 9, 9, 9, 9 })
                .WaitAsync(TimeSpan.FromSeconds(6)));

        var (_, peers, _) = await b.GetPeersAsync(a.LocalEndPoint, infoHash).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(peers);
    }
}
