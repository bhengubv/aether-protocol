// SPDX-License-Identifier: MIT

namespace AetherMesh.Handshake;

/// <summary>
/// The negotiated protocol-version + capability set for a remote peer, locked
/// in once the Hello/HelloAck exchange completes (or after the backward-compat
/// timeout for peers that never replied).
///
/// <para>
/// The <see cref="NegotiatedVersion"/> is the highest protocol version both
/// sides advertised support for. The <see cref="Capabilities"/> set is the
/// intersection of both sides' advertised capability tags — services should
/// gate optional features (Double-Ratchet, DTN custody, voice, etc.) on
/// capability presence rather than on raw protocol-version.
/// </para>
/// </summary>
/// <param name="PeerUhid">UHID of the peer this record describes.</param>
/// <param name="NegotiatedVersion">
/// Highest mutually-supported protocol version. Defaults to
/// <c>1</c> for peers that never replied with a HelloAck (backward-compat).
/// </param>
/// <param name="Capabilities">
/// Intersection of capability tags both sides claim to support. Empty for
/// peers that never replied.
/// </param>
/// <param name="ImplementationVersion">
/// Free-form implementation banner the peer announced (e.g.
/// <c>"aether-csharp/1.0.0"</c>). Empty for peers that never replied.
/// </param>
/// <param name="NegotiatedAt">UTC timestamp when negotiation completed.</param>
public sealed record PeerCapabilities(
    string PeerUhid,
    byte NegotiatedVersion,
    IReadOnlySet<string> Capabilities,
    string ImplementationVersion,
    DateTimeOffset NegotiatedAt);
