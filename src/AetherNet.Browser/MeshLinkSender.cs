// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Browser;

/// <summary>
/// Carries protocol packets over whatever radio the host gave us.
///
/// <para>
/// The directory and content services speak <see cref="IMeshSender"/> — packets, next hops, peer
/// lists. A phone-to-phone radio has exactly one other end, so a next hop means nothing and
/// "broadcast" and "send" are the same act. This is the whole of the translation.
/// </para>
///
/// <para>
/// Inbound bytes are not handled here. They arrive on <see cref="IMeshLink.PacketReceived"/> and are
/// dispatched by the node, which knows which service each packet type belongs to — a sender that also
/// received would have to know that too, and would be two things.
/// </para>
/// </summary>
internal sealed class MeshLinkSender(string localUhid, IMeshLink link) : IMeshSender
{
    public string LocalUhid { get; } = localUhid;

    public string? LocalGeohash => null;

    public IReadOnlyList<PeerInfo> GetConnectedPeers() =>
        link.IsLinked
            ? [new PeerInfo { Uhid = string.Empty, TransportType = link.Name }]
            : [];

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default) =>
        link.SendAsync(PacketSerializer.Serialize(packet));

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default) =>
        await link.SendAsync(PacketSerializer.Serialize(packet)).ConfigureAwait(false) ? 1 : 0;
}
