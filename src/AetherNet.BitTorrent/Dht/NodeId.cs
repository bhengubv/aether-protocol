// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;

namespace AetherNet.BitTorrent.Dht;

/// <summary>A 160-bit Kademlia node id (BEP-5).</summary>
public sealed class NodeId : IEquatable<NodeId>
{
    public const int Length = 20;
    private readonly byte[] _bytes;

    public NodeId(byte[] bytes)
    {
        if (bytes is not { Length: Length }) throw new ArgumentException($"node id must be {Length} bytes", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
    }

    public ReadOnlySpan<byte> Span => _bytes;
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public static NodeId Random() => new(RandomNumberGenerator.GetBytes(Length));

    /// <summary>The Kademlia XOR distance to another id (a 20-byte big-endian magnitude).</summary>
    public byte[] DistanceTo(NodeId other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var d = new byte[Length];
        for (int i = 0; i < Length; i++) d[i] = (byte)(_bytes[i] ^ other._bytes[i]);
        return d;
    }

    public bool Equals(NodeId? other) => other is not null && other._bytes.AsSpan().SequenceEqual(_bytes);
    public override bool Equals(object? obj) => Equals(obj as NodeId);
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(_bytes);
        return h.ToHashCode();
    }
    public override string ToString() => Convert.ToHexString(_bytes).ToLowerInvariant();
}

/// <summary>A DHT contact: a node id and where to reach it.</summary>
public sealed record DhtContact(NodeId Id, IPEndPoint EndPoint);
