// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.BitTorrent.PeerWire;

/// <summary>Thrown when peer-wire framing or a handshake is malformed.</summary>
public sealed class PeerWireException : Exception
{
    public PeerWireException(string message) : base(message) { }
}

/// <summary>
/// The BitTorrent peer handshake (BEP-3): a fixed 68-byte message —
/// <c>pstrlen(1)=19, pstr(19)="BitTorrent protocol", reserved(8), info_hash(20), peer_id(20)</c>.
/// </summary>
public sealed class Handshake
{
    public const string ProtocolId = "BitTorrent protocol";
    public const int Length = 68;
    private static readonly byte[] ProtocolBytes = Encoding.ASCII.GetBytes(ProtocolId);

    public byte[] Reserved { get; } // 8 bytes
    public byte[] InfoHash { get; } // 20 bytes
    public byte[] PeerId { get; }   // 20 bytes

    /// <summary>Peer advertises the extension protocol (BEP-10) — reserved byte 5, bit 0x10.</summary>
    public bool SupportsExtensionProtocol => (Reserved[5] & 0x10) != 0;
    /// <summary>Peer advertises DHT (BEP-5) — reserved byte 7, bit 0x01.</summary>
    public bool SupportsDht => (Reserved[7] & 0x01) != 0;
    /// <summary>Peer advertises the Fast Extension (BEP-6) — reserved byte 7, bit 0x04.</summary>
    public bool SupportsFastExtension => (Reserved[7] & 0x04) != 0;

    public Handshake(byte[] infoHash, byte[] peerId, byte[]? reserved = null)
    {
        if (infoHash is not { Length: 20 }) throw new ArgumentException("info_hash must be 20 bytes", nameof(infoHash));
        if (peerId is not { Length: 20 }) throw new ArgumentException("peer_id must be 20 bytes", nameof(peerId));
        if (reserved is not null && reserved.Length != 8) throw new ArgumentException("reserved must be 8 bytes", nameof(reserved));
        InfoHash = infoHash;
        PeerId = peerId;
        Reserved = reserved ?? new byte[8];
    }

    /// <summary>Reserved bytes advertising the extension protocol (BEP-10) and DHT (BEP-5).</summary>
    public static byte[] DefaultReserved()
    {
        var r = new byte[8];
        r[5] |= 0x10; // extension protocol
        r[7] |= 0x01; // DHT
        return r;
    }

    public byte[] ToBytes()
    {
        var buf = new byte[Length];
        buf[0] = (byte)ProtocolBytes.Length; // 19
        ProtocolBytes.CopyTo(buf, 1);
        Reserved.CopyTo(buf, 20);
        InfoHash.CopyTo(buf, 28);
        PeerId.CopyTo(buf, 48);
        return buf;
    }

    public static Handshake Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
            throw new PeerWireException($"handshake must be {Length} bytes, got {data.Length}");
        int pstrlen = data[0];
        if (pstrlen != ProtocolBytes.Length)
            throw new PeerWireException($"unexpected pstrlen {pstrlen} (expected {ProtocolBytes.Length})");
        if (!data.Slice(1, pstrlen).SequenceEqual(ProtocolBytes))
            throw new PeerWireException("unexpected protocol id in handshake");
        return new Handshake(
            infoHash: data.Slice(28, 20).ToArray(),
            peerId: data.Slice(48, 20).ToArray(),
            reserved: data.Slice(20, 8).ToArray());
    }
}
