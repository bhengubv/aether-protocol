// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Browser;

/// <summary>
/// The transport for a device with no radio: everything it sends, it receives.
///
/// <para>
/// A browser with no radio is not a broken browser. The pages its owner wrote are on this device, the
/// cards they hold are on this device, and both should open — a desktop, a phone with everything
/// switched off, and a test all want exactly that. What they do not have is anybody else.
/// </para>
///
/// <para>
/// So packets go round in a circle. A query for one of this device's own cards is asked, heard by the
/// same node, and answered by the same node — which is what makes the local case work through the
/// ordinary code path rather than through a special one. There is no second code path to keep honest.
/// </para>
///
/// <para>
/// Delivered off the calling stack on purpose. A directory answering its own query re-enters the same
/// services, and doing that synchronously inside a lock is how a browser opens its own front page and
/// stops responding.
/// </para>
/// </summary>
internal sealed class LoopbackMeshSender(string localUhid, Action<byte[]> heard) : IMeshSender
{
    public string LocalUhid { get; } = localUhid;

    public string? LocalGeohash => null;

    /// <summary>Nobody. A device with no radio has no peers, and saying otherwise would be a lie with consequences.</summary>
    public IReadOnlyList<PeerInfo> GetConnectedPeers() => [];

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        Deliver(packet);
        return Task.FromResult(true);
    }

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        Deliver(packet);
        return Task.FromResult(1);
    }

    private void Deliver(MeshPacket packet)
    {
        var bytes = PacketSerializer.Serialize(packet);
        _ = Task.Run(() => heard(bytes));
    }
}
