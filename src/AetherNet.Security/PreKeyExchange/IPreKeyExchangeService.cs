// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Security.Models;

namespace AetherNet.PreKeys;

/// <summary>
/// Mesh pre-key exchange over <see cref="PacketType.PreKeyRequest"/> (25) and
/// <see cref="PacketType.PreKeyResponse"/> (26). Closes the "how does a peer get another peer's
/// <see cref="PreKeyBundle"/> over the mesh" gap the messaging layer previously left out-of-band.
///
/// A node publishes its current bundle via <see cref="SetLocalBundle"/> (the host produces it with
/// ISignalProtocolService.GeneratePreKeyBundleAsync). A peer asks for it with
/// <see cref="RequestBundleAsync"/>; the responder replies with its bundle; the requester surfaces the
/// received bundle via <see cref="BundleReceived"/> and caches it. This service is the mesh TRANSPORT
/// of bundles — the host performs the actual X3DH by feeding the received bundle to
/// ISignalProtocolService.ProcessPreKeyBundleAsync (Signal-canonical: no key agreement happens here).
/// </summary>
public interface IPreKeyExchangeService
{
    /// <summary>Raised when a peer's pre-key bundle arrives in a <see cref="PacketType.PreKeyResponse"/>.</summary>
    event EventHandler<PreKeyBundleReceivedEventArgs>? BundleReceived;

    /// <summary>Set (or replace) this node's published bundle — served in reply to inbound requests.</summary>
    void SetLocalBundle(PreKeyBundle bundle);

    /// <summary>The currently-published local bundle, or null if none has been set.</summary>
    PreKeyBundle? GetLocalBundle();

    /// <summary>
    /// Ask <paramref name="peerUhid"/> for its pre-key bundle: mint a request id and send a directed
    /// <see cref="PacketType.PreKeyRequest"/>. Returns the new request id (echoed by the response).
    /// </summary>
    Task<Guid> RequestBundleAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process an incoming pre-key packet. On <see cref="PacketType.PreKeyRequest"/>, reply with the
    /// local bundle (if set). On <see cref="PacketType.PreKeyResponse"/>, cache the peer bundle and raise
    /// <see cref="BundleReceived"/>. Returns false for the wrong packet type, a malformed payload, or a
    /// request received when no local bundle is set.
    /// </summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>The most recently received bundle for <paramref name="uhid"/>, or null.</summary>
    PreKeyBundle? GetReceivedBundle(string uhid);
}
