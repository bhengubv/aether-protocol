// SPDX-License-Identifier: MIT

using AetherMesh.Models;
using AetherMesh.Protocol;
using AetherMesh.Routing;

namespace AetherMesh.Soak.Tests;

/// <summary>
/// Bare-bones in-memory <see cref="IMeshSender"/> for soak tests. Different
/// from the unit-test FakeMeshSender in two ways:
/// <list type="bullet">
///   <item>Does NOT clone packets on send/broadcast — the soak runs would
///     allocate hundreds of MB of cloned packets during a 10k-iteration loop
///     and confound the leak measurement.</item>
///   <item>Does NOT retain a per-call history (no <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>
///     of sent packets) — same reason. Soak tests verify behavior via
///     observable state, not per-call inspection.</item>
/// </list>
/// </summary>
internal sealed class SoakFakeMeshSender : IMeshSender
{
    public string LocalUhid { get; }
    public string? LocalGeohash { get; }

    private readonly List<PeerInfo> _peers = new();

    public SoakFakeMeshSender(string localUhid, string? localGeohash = null)
    {
        LocalUhid = localUhid;
        LocalGeohash = localGeohash;
    }

    public IReadOnlyList<PeerInfo> GetConnectedPeers() => _peers;

    public void AddPeer(PeerInfo peer) => _peers.Add(peer);

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        => Task.FromResult(_peers.Count);
}
