// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AetherNet.BitTorrent.Trackers;

/// <summary>
/// A UDP BitTorrent tracker client (BEP-15): a connect handshake to obtain a connection id, then an
/// announce that returns the swarm's peers as compact 6-byte entries. Transaction ids are matched and
/// each request has a receive timeout.
/// </summary>
public sealed class UdpTrackerClient
{
    private const long ProtocolMagic = 0x41727101980L;
    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionError = 3;

    private readonly TimeSpan _timeout;

    public UdpTrackerClient(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<AnnounceResponse> AnnounceAsync(Uri udpUri, AnnounceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(udpUri);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(udpUri.Scheme, "udp", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("not a udp:// tracker URI");
        if (request.InfoHash is not { Length: 20 } || request.PeerId is not { Length: 20 })
            throw new ArgumentException("info_hash and peer_id must be 20 bytes");

        using var udp = new UdpClient();
        udp.Connect(udpUri.Host, udpUri.Port);

        long connectionId = await ConnectAsync(udp, ct).ConfigureAwait(false);
        return await AnnounceAsync(udp, connectionId, request, ct).ConfigureAwait(false);
    }

    private async Task<long> ConnectAsync(UdpClient udp, CancellationToken ct)
    {
        int txn = Random.Shared.Next();
        var req = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(0), ProtocolMagic);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(8), ActionConnect);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(12), txn);

        var resp = await SendReceiveAsync(udp, req, minLength: 16, ct).ConfigureAwait(false);
        int action = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(0));
        int rtxn = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(4));
        if (rtxn != txn) throw new TrackerException("connect transaction id mismatch");
        if (action != ActionConnect) throw new TrackerException($"unexpected connect action {action}");
        return BinaryPrimitives.ReadInt64BigEndian(resp.AsSpan(8));
    }

    private async Task<AnnounceResponse> AnnounceAsync(UdpClient udp, long connectionId, AnnounceRequest request, CancellationToken ct)
    {
        int txn = Random.Shared.Next();
        var req = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(0), connectionId);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(8), ActionAnnounce);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(12), txn);
        request.InfoHash.CopyTo(req.AsSpan(16));
        request.PeerId.CopyTo(req.AsSpan(36));
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(req.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(80), EventCode(request.Event));
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(84), 0);                 // ip (0 = the source)
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(88), Random.Shared.Next()); // key
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(92), request.NumWant);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(96), (ushort)request.Port);

        var resp = await SendReceiveAsync(udp, req, minLength: 20, ct).ConfigureAwait(false);
        int action = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(0));
        int rtxn = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(4));
        if (rtxn != txn) throw new TrackerException("announce transaction id mismatch");
        if (action == ActionError) throw new TrackerException($"tracker error: {Encoding.ASCII.GetString(resp.AsSpan(8))}");
        if (action != ActionAnnounce) throw new TrackerException($"unexpected announce action {action}");

        int interval = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(8));
        int leechers = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(12));
        int seeders = BinaryPrimitives.ReadInt32BigEndian(resp.AsSpan(16));

        var peers = new List<PeerAddress>();
        for (int i = 20; i + 6 <= resp.Length; i += 6)
            peers.Add(new PeerAddress(new IPAddress(resp[i..(i + 4)]), BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(i + 4, 2))));

        return new AnnounceResponse { Interval = interval, Incomplete = leechers, Complete = seeders, Peers = peers };
    }

    private async Task<byte[]> SendReceiveAsync(UdpClient udp, byte[] payload, int minLength, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            await udp.SendAsync(payload, cts.Token).ConfigureAwait(false);
            var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (result.Buffer.Length < minLength) throw new TrackerException($"short UDP response ({result.Buffer.Length} < {minLength})");
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TrackerException("UDP tracker timed out");
        }
        catch (SocketException ex)
        {
            // Windows raises ICMP port-unreachable as a connection reset on a connected UDP socket.
            throw new TrackerException($"UDP tracker unreachable: {ex.SocketErrorCode}");
        }
    }

    private static int EventCode(TrackerEvent e) => e switch
    {
        TrackerEvent.Completed => 1,
        TrackerEvent.Started => 2,
        TrackerEvent.Stopped => 3,
        _ => 0,
    };
}
