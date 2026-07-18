// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AetherNet.BitTorrent.Client;
using AetherNet.BitTorrent.PeerWire;
using AetherNet.BitTorrent.Storage;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class LoopbackTransferTests
{
    private static byte[] MakePeerId(char tag)
    {
        var id = new byte[20];
        id[0] = (byte)tag;
        for (int i = 1; i < 20; i++) id[i] = (byte)i;
        return id;
    }

    private static async Task SafeAwait(Task t)
    {
        try { await t; } catch { /* shutdown noise */ }
    }

    [Fact]
    public async Task Two_peers_exchange_a_multi_piece_file_over_real_tcp()
    {
        // ~100 KB of deterministic content: several full multi-block pieces + a short final piece.
        var content = new byte[100_000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 + 7);
        const int pieceLength = 32768; // 2 blocks per full piece

        var seederStore = PieceStore.FromContent(content, pieceLength);
        var leecherStore = new PieceStore(content.Length, pieceLength, seederStore.PieceHashes);
        Assert.True(seederStore.PieceCount >= 3);

        var infoHash = SHA1.HashData("aethernet-bittorrent-loopback"u8.ToArray());

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port);
        using var serverTcp = await acceptTask;

        await using var seederConn = new PeerConnection(serverTcp.GetStream());
        await using var leecherConn = new PeerConnection(clientTcp.GetStream());

        // Seeder has the whole file; leecher starts empty and must download it.
        var seeder = new PeerSession(seederConn, seederStore, infoHash, initiator: false, peerId: MakePeerId('S'));
        var leecher = new PeerSession(leecherConn, leecherStore, infoHash, initiator: true, peerId: MakePeerId('L'));

        using var cts = new CancellationTokenSource();
        var seederRun = seeder.RunAsync(cts.Token);
        var leecherRun = leecher.RunAsync(cts.Token);

        // The file actually crosses two real BitTorrent peers over TCP.
        await leecher.DownloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(leecherStore.IsComplete);
        Assert.Equal(content, leecherStore.Assemble());

        cts.Cancel();
        await SafeAwait(Task.WhenAll(seederRun, leecherRun).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Handshake_rejects_mismatched_infohash()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port);
        using var serverTcp = await acceptTask;

        await using var a = new PeerConnection(serverTcp.GetStream());
        await using var b = new PeerConnection(clientTcp.GetStream());

        var hsA = new Handshake(new byte[20], MakePeerId('A'));
        var mismatched = new byte[20];
        mismatched[0] = 0xFF;
        var hsB = new Handshake(mismatched, MakePeerId('B'));

        var serverHs = a.HandshakeAsync(hsA, initiator: false);
        await Assert.ThrowsAsync<PeerWireException>(() => b.HandshakeAsync(hsB, initiator: true));
        await SafeAwait(serverHs);
    }
}
