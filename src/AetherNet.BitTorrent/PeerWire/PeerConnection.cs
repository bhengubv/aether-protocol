// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace AetherNet.BitTorrent.PeerWire;

/// <summary>
/// A single BitTorrent peer connection over a byte <see cref="Stream"/> (a real TCP socket, or any
/// duplex stream). Performs the handshake, frames messages on the wire, and tracks the four
/// choke/interested state bits. Message semantics (bitfield, picking, storage) live in the session
/// above it.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly Stream _stream;

    public Handshake? RemoteHandshake { get; private set; }

    public bool AmChoking { get; private set; } = true;
    public bool AmInterested { get; private set; }
    public bool PeerChoking { get; private set; } = true;
    public bool PeerInterested { get; private set; }

    public PeerConnection(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Exchange handshakes (the initiator — the connecting side — sends first) and verify the info-hash matches.</summary>
    public async Task<Handshake> HandshakeAsync(Handshake ours, bool initiator, CancellationToken ct = default)
    {
        if (initiator)
        {
            await _stream.WriteAsync(ours.ToBytes(), ct).ConfigureAwait(false);
            RemoteHandshake = await ReadHandshakeAsync(ct).ConfigureAwait(false);
        }
        else
        {
            RemoteHandshake = await ReadHandshakeAsync(ct).ConfigureAwait(false);
            await _stream.WriteAsync(ours.ToBytes(), ct).ConfigureAwait(false);
        }
        if (!RemoteHandshake.InfoHash.AsSpan().SequenceEqual(ours.InfoHash))
            throw new PeerWireException("peer info-hash does not match ours");
        return RemoteHandshake;
    }

    private async Task<Handshake> ReadHandshakeAsync(CancellationToken ct)
    {
        var buf = new byte[Handshake.Length];
        await _stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return Handshake.Parse(buf);
    }

    /// <summary>Send a message, updating our own choke/interested state for the state-changing kinds.</summary>
    public async Task SendAsync(PeerMessage message, CancellationToken ct = default)
    {
        await _stream.WriteAsync(message.ToBytes(), ct).ConfigureAwait(false);
        switch (message.Type)
        {
            case PeerMessageType.Choke: AmChoking = true; break;
            case PeerMessageType.Unchoke: AmChoking = false; break;
            case PeerMessageType.Interested: AmInterested = true; break;
            case PeerMessageType.NotInterested: AmInterested = false; break;
        }
    }

    /// <summary>Read the next message, updating the peer's choke/interested state.</summary>
    public async Task<PeerMessage> ReceiveAsync(CancellationToken ct = default)
    {
        var lengthBuf = new byte[4];
        await _stream.ReadExactlyAsync(lengthBuf, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
        if (length < 0) throw new PeerWireException("negative message length");

        byte[] body = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0) await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

        var message = PeerMessage.ParseBody(body);
        switch (message.Type)
        {
            case PeerMessageType.Choke: PeerChoking = true; break;
            case PeerMessageType.Unchoke: PeerChoking = false; break;
            case PeerMessageType.Interested: PeerInterested = true; break;
            case PeerMessageType.NotInterested: PeerInterested = false; break;
        }
        return message;
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
}
