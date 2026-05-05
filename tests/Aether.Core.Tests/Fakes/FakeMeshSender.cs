// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Aether.Models;
using Aether.Protocol;
using Aether.Routing;

namespace Aether.Core.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IMeshSender"/> for tests. Records every send and broadcast,
/// supports configurable success/failure per peer, and exposes a list of currently
/// connected peers that the routing/DTN strategies can query.
/// </summary>
public sealed class FakeMeshSender : IMeshSender
{
    public string LocalUhid { get; }
    public string? LocalGeohash { get; set; }

    public ConcurrentBag<(MeshPacket Packet, string NextHopUhid)> Unicasts { get; } = new();
    public ConcurrentBag<MeshPacket> Broadcasts { get; } = new();

    private readonly List<PeerInfo> _peers = new();
    private readonly HashSet<string> _peersThatFailSend = new(StringComparer.Ordinal);

    public FakeMeshSender(string localUhid, string? localGeohash = null)
    {
        LocalUhid = localUhid;
        LocalGeohash = localGeohash;
    }

    public IReadOnlyList<PeerInfo> GetConnectedPeers() => _peers.ToArray();

    public void AddPeer(PeerInfo peer) => _peers.Add(peer);

    public void RemovePeer(string uhid) => _peers.RemoveAll(p => p.Uhid == uhid);

    public void FailSendsToPeer(string uhid) => _peersThatFailSend.Add(uhid);

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        if (_peersThatFailSend.Contains(nextHopUhid))
            return Task.FromResult(false);

        // Defensive copy so the test can mutate one and the recorded other stays intact.
        Unicasts.Add((Clone(packet), nextHopUhid));
        return Task.FromResult(true);
    }

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        Broadcasts.Add(Clone(packet));
        return Task.FromResult(_peers.Count);
    }

    public void Clear()
    {
        Unicasts.Clear();
        Broadcasts.Clear();
    }

    private static MeshPacket Clone(MeshPacket source)
    {
        return new MeshPacket
        {
            Id = source.Id,
            Type = source.Type,
            SourceUhid = source.SourceUhid,
            DestinationUhid = source.DestinationUhid,
            Ttl = source.Ttl,
            Priority = source.Priority,
            Payload = (byte[])source.Payload.Clone(),
            Signature = (byte[])source.Signature.Clone(),
            PacketNonce = (byte[])source.PacketNonce.Clone(),
            TimestampMs = source.TimestampMs,
            ProtocolVersion = source.ProtocolVersion,
            CreatedAt = source.CreatedAt,
        };
    }
}
