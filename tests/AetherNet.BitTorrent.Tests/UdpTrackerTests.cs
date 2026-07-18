// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AetherNet.BitTorrent.Trackers;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class UdpTrackerTests
{
    private static byte[] Filled(int len, byte b)
    {
        var a = new byte[len];
        Array.Fill(a, b);
        return a;
    }

    [Fact]
    public async Task Announce_does_connect_handshake_then_returns_peers()
    {
        const long connectionId = 0x1122334455667788L;

        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var serve = Task.Run(async () =>
        {
            // ── connect ──
            var c = await server.ReceiveAsync();
            Assert.Equal(0x41727101980L, BinaryPrimitives.ReadInt64BigEndian(c.Buffer.AsSpan(0)));  // protocol magic
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(c.Buffer.AsSpan(8)));                // action = connect
            int cTxn = BinaryPrimitives.ReadInt32BigEndian(c.Buffer.AsSpan(12));

            var cr = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(cr.AsSpan(0), 0);
            BinaryPrimitives.WriteInt32BigEndian(cr.AsSpan(4), cTxn);
            BinaryPrimitives.WriteInt64BigEndian(cr.AsSpan(8), connectionId);
            await server.SendAsync(cr, cr.Length, c.RemoteEndPoint);

            // ── announce ──
            var a = await server.ReceiveAsync();
            Assert.Equal(connectionId, BinaryPrimitives.ReadInt64BigEndian(a.Buffer.AsSpan(0)));      // echoes connection id
            Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(a.Buffer.AsSpan(8)));                 // action = announce
            int aTxn = BinaryPrimitives.ReadInt32BigEndian(a.Buffer.AsSpan(12));
            Assert.Equal(6881, BinaryPrimitives.ReadUInt16BigEndian(a.Buffer.AsSpan(96)));            // port

            var ar = new byte[20 + 12];
            BinaryPrimitives.WriteInt32BigEndian(ar.AsSpan(0), 1);   // action
            BinaryPrimitives.WriteInt32BigEndian(ar.AsSpan(4), aTxn);
            BinaryPrimitives.WriteInt32BigEndian(ar.AsSpan(8), 900); // interval
            BinaryPrimitives.WriteInt32BigEndian(ar.AsSpan(12), 3);  // leechers
            BinaryPrimitives.WriteInt32BigEndian(ar.AsSpan(16), 7);  // seeders
            new byte[] { 1, 2, 3, 4 }.CopyTo(ar, 20);
            BinaryPrimitives.WriteUInt16BigEndian(ar.AsSpan(24), 6881);
            new byte[] { 5, 6, 7, 8 }.CopyTo(ar, 26);
            BinaryPrimitives.WriteUInt16BigEndian(ar.AsSpan(30), 51413);
            await server.SendAsync(ar, ar.Length, a.RemoteEndPoint);
        });

        var client = new UdpTrackerClient(TimeSpan.FromSeconds(5));
        var resp = await client.AnnounceAsync(
            new Uri($"udp://127.0.0.1:{port}/announce"),
            new AnnounceRequest { InfoHash = Filled(20, 1), PeerId = Filled(20, 2), Port = 6881, Left = 100 });

        Assert.Equal(900, resp.Interval);
        Assert.Equal(7, resp.Complete);
        Assert.Equal(3, resp.Incomplete);
        Assert.Equal(2, resp.Peers.Count);
        Assert.Equal("1.2.3.4:6881", resp.Peers[0].ToString());
        Assert.Equal("5.6.7.8:51413", resp.Peers[1].ToString());

        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Times_out_when_tracker_is_silent()
    {
        // Nothing listens on this port → the client should surface a timeout, not hang.
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close(); // free the port; nobody answers

        var client = new UdpTrackerClient(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TrackerException>(() => client.AnnounceAsync(
            new Uri($"udp://127.0.0.1:{deadPort}/announce"),
            new AnnounceRequest { InfoHash = Filled(20, 1), PeerId = Filled(20, 2), Port = 6881 }));
    }
}
