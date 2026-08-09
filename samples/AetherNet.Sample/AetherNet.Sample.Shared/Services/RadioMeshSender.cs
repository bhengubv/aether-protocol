// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Bridges the packet-level <see cref="IMeshSender"/> (consumed by the directory + content services)
/// onto a real over-the-air radio via <see cref="IRadioMesh"/>. There is exactly one linked peer on a
/// phone-to-phone radio, so every send — broadcast or unicast — is just "push these bytes to the peer";
/// the next-hop UHID is irrelevant on a one-hop link. Inbound bytes arrive on
/// <see cref="IRadioMesh.PacketReceived"/> and are dispatched by the mesh-web node, not here.
/// </summary>
internal sealed class RadioMeshSender : IMeshSender
{
    private readonly IRadioMesh _radio;

    public RadioMeshSender(string localUhid, IRadioMesh radio)
    {
        LocalUhid = localUhid;
        _radio = radio;
    }

    public string LocalUhid { get; }
    public string? LocalGeohash => null;

    public IReadOnlyList<PeerInfo> GetConnectedPeers() =>
        _radio is { IsLinked: true, PeerTag: { } peer }
            ? new[] { new PeerInfo { Uhid = peer, TransportType = _radio.SelectedRadio } }
            : Array.Empty<PeerInfo>();

    // One physical link → one peer. Unicast and broadcast both mean "send to the peer".
    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => _radio.SendPacketAsync(PacketSerializer.Serialize(packet));

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        => await _radio.SendPacketAsync(PacketSerializer.Serialize(packet)).ConfigureAwait(false) ? 1 : 0;
}
