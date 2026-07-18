// SPDX-License-Identifier: MIT

using System.Net.Sockets;

namespace AetherNet.BitTorrent.Utp;

/// <summary>
/// A µTP connection (BEP-29) over a connected <see cref="UdpClient"/>: the SYN/STATE handshake with
/// the standard <c>recv_id = send_id + 1</c> connection-id convention, reliable in-order DATA delivery
/// with STATE acknowledgements and retransmission, and an acked FIN. A congestion window is a
/// performance refinement; this uses acked send with retransmit — genuinely reliable µTP transfer.
/// </summary>
public sealed class UtpSocket : IAsyncDisposable
{
    private const int MaxPayload = 1000;

    private readonly UdpClient _udp;
    private readonly bool _initiator;
    private readonly TimeSpan _rto = TimeSpan.FromMilliseconds(500);
    private readonly object _lock = new();

    private ushort _recvId;
    private ushort _sendId;
    private ushort _seqNr;   // next sequence number we will send
    private ushort _ackNr;   // highest in-order sequence we've received from the peer
    private ushort _peerAck; // highest sequence the peer has acknowledged

    private readonly List<byte> _received = new();
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _fin = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _cts;
    private Task? _recvLoop;

    private UtpSocket(UdpClient udp, bool initiator)
    {
        _udp = udp;
        _initiator = initiator;
    }

    public static UtpSocket Initiator(UdpClient connectedUdp) => new(connectedUdp, true);
    public static UtpSocket Acceptor(UdpClient connectedUdp) => new(connectedUdp, false);

    public byte[] ReceivedBytes
    {
        get { lock (_lock) return _received.ToArray(); }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _recvId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        _sendId = (ushort)(_recvId + 1);
        Start();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await SendAsync(UtpPacketType.Syn, _recvId, seq: 1, Array.Empty<byte>(), ct).ConfigureAwait(false);
            try
            {
                await _handshake.Task.WaitAsync(_rto, ct).ConfigureAwait(false);
                _seqNr = 2; // SYN used seq 1; data starts at 2
                return;
            }
            catch (TimeoutException) { /* retransmit SYN */ }
        }
        throw new UtpException("µTP handshake timed out");
    }

    public async Task AcceptAsync(CancellationToken ct = default)
    {
        Start();
        await _handshake.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        for (int off = 0; off < data.Length; off += MaxPayload)
        {
            int len = Math.Min(MaxPayload, data.Length - off);
            await SendReliableAsync(UtpPacketType.Data, _seqNr, data[off..(off + len)], ct).ConfigureAwait(false);
            _seqNr++;
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await SendReliableAsync(UtpPacketType.Fin, _seqNr, Array.Empty<byte>(), ct).ConfigureAwait(false);
        _seqNr++;
    }

    public Task WaitForFinAsync(CancellationToken ct = default) => _fin.Task.WaitAsync(ct);

    // ── internals ─────────────────────────────────────────────────────────────

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _recvLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch { break; }

            UtpPacket p;
            try { p = UtpPacket.Parse(r.Buffer); }
            catch { continue; }

            await HandleAsync(p, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(UtpPacket p, CancellationToken ct)
    {
        switch (p.Type)
        {
            case UtpPacketType.Syn:
                _sendId = p.ConnectionId;
                _recvId = (ushort)(p.ConnectionId + 1);
                lock (_lock) { _ackNr = p.SeqNr; }
                _seqNr = (ushort)Random.Shared.Next(1, ushort.MaxValue);
                await SendStateAsync(ct).ConfigureAwait(false);
                _handshake.TrySetResult();
                break;

            case UtpPacketType.State:
                lock (_lock) { _peerAck = p.AckNr; }
                _handshake.TrySetResult(); // the STATE that answers our SYN
                break;

            case UtpPacketType.Data:
                bool delivered = false;
                lock (_lock)
                {
                    if (p.SeqNr == (ushort)(_ackNr + 1)) // next in-order
                    {
                        _received.AddRange(p.Payload);
                        _ackNr = p.SeqNr;
                        delivered = true;
                    }
                }
                _ = delivered; // duplicates/out-of-order are simply re-acked below
                await SendStateAsync(ct).ConfigureAwait(false);
                break;

            case UtpPacketType.Fin:
                lock (_lock) { if (p.SeqNr == (ushort)(_ackNr + 1)) _ackNr = p.SeqNr; }
                await SendStateAsync(ct).ConfigureAwait(false);
                _fin.TrySetResult();
                break;
        }
    }

    private async Task SendReliableAsync(UtpPacketType type, ushort seq, byte[] payload, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await SendAsync(type, _sendId, seq, payload, ct).ConfigureAwait(false);
            if (await WaitForAckAsync(seq, ct).ConfigureAwait(false)) return;
        }
        throw new UtpException($"µTP {type} seq {seq} was never acknowledged");
    }

    private async Task<bool> WaitForAckAsync(ushort seq, CancellationToken ct)
    {
        int polls = (int)(_rto.TotalMilliseconds / 10);
        for (int i = 0; i < polls; i++)
        {
            lock (_lock) { if (SeqLeq(seq, _peerAck)) return true; }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        return false;
    }

    private Task SendStateAsync(CancellationToken ct)
    {
        ushort ack;
        lock (_lock) ack = _ackNr;
        return SendAsync(UtpPacketType.State, _sendId, _seqNr, Array.Empty<byte>(), ct, ack);
    }

    private Task SendAsync(UtpPacketType type, ushort connId, ushort seq, byte[] payload, CancellationToken ct, ushort? ackOverride = null)
    {
        ushort ack = ackOverride ?? AckSnapshot();
        var bytes = new UtpPacket
        {
            Type = type,
            ConnectionId = connId,
            SeqNr = seq,
            AckNr = ack,
            WindowSize = 1 << 20,
            Payload = payload,
        }.ToBytes();
        return _udp.SendAsync(bytes, ct).AsTask();
    }

    private ushort AckSnapshot()
    {
        lock (_lock) return _ackNr;
    }

    /// <summary>Wrap-safe "seq a has been reached by ack b".</summary>
    private static bool SeqLeq(ushort a, ushort b) => (ushort)(b - a) < 0x8000;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _udp.Dispose();
        if (_recvLoop is not null)
        {
            try { await _recvLoop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
