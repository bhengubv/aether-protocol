// SPDX-License-Identifier: MIT

using AetherMesh.Extensibility;
using AetherMesh.Protocol;

namespace AetherMesh.Handshake;

/// <summary>
/// Protocol-version + capability negotiation service.
///
/// <para>
/// Peers exchange a <see cref="PacketType.Hello"/> / <see cref="PacketType.HelloAck"/>
/// pair on first contact: each side announces the protocol-version range it
/// can speak and the capability tags it supports; the receiver replies with
/// the highest mutually-supported version + the intersection of capability
/// tags. Once locked in, subsequent traffic is gated against this record.
/// </para>
///
/// <para>
/// The handshake itself is unencrypted and unauthenticated — it runs before
/// any Signal session exists. Peer identity is verified later via Ed25519
/// packet signatures on data packets. The capability set must therefore be
/// treated as a hint, not as an authenticated claim.
/// </para>
///
/// <para>
/// Backward-compat: a peer that never replies with a HelloAck is assumed to
/// be running protocol version 1 with no advertised capabilities. Traffic
/// still flows; services that depend on optional capabilities should query
/// <see cref="GetPeerCapabilitiesAsync"/> and degrade gracefully if a
/// capability tag is absent.
/// </para>
/// </summary>
public interface IHandshakeService
{
    /// <summary>
    /// Fired when negotiation completes (either via HelloAck receipt or via
    /// the backward-compat fallback). Gives subscribers the locked-in
    /// <see cref="PeerCapabilities"/> record.
    /// </summary>
    event EventHandler<PeerCapabilities>? PeerNegotiated;

    /// <summary>
    /// Fired when a peer's announced version range does not overlap with
    /// ours — we cannot speak to them. Subscribers should drop the peer
    /// from their connected-peer set.
    /// </summary>
    event EventHandler<IncompatiblePeerEventArgs>? IncompatiblePeer;

    /// <summary>
    /// Initiate a Hello towards a freshly discovered peer. No-op if a
    /// Hello has already been sent to this peer in the current session
    /// (re-broadcasts can cause duplicate Hellos otherwise).
    /// </summary>
    /// <param name="peerUhid">UHID of the freshly seen peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitiateAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle an inbound <see cref="PacketType.Hello"/>: lock in their
    /// announced capabilities and reply with a HelloAck.
    /// </summary>
    Task HandleHelloAsync(MeshPacket helloPacket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handle an inbound <see cref="PacketType.HelloAck"/>: lock in the
    /// negotiated capabilities for the replying peer.
    /// </summary>
    Task HandleHelloAckAsync(MeshPacket helloAckPacket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up the locked-in capabilities for a peer. Returns null if the
    /// handshake has not yet completed — callers can either wait for the
    /// <see cref="PeerNegotiated"/> event or proceed with caution.
    /// </summary>
    Task<PeerCapabilities?> GetPeerCapabilitiesAsync(
        string peerUhid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop a peer's cached capabilities and re-issue a Hello on the next
    /// outbound contact. Used when version-mismatch is detected in
    /// subsequent traffic.
    /// </summary>
    Task RenegotiateAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot of every peer that has finished negotiating, for
    /// diagnostics / health-check use.
    /// </summary>
    IReadOnlyList<PeerCapabilities> GetAllNegotiated();

    /// <summary>
    /// Verify physical co-presence of a peer by detecting a face in a live
    /// camera frame and comparing it to a reference embedding.
    ///
    /// <para>
    /// This is the biometric component of the aether-market PoV
    /// (Proof-of-Vicinity) token exchange. Both devices capture a camera
    /// frame; each verifies that the face it sees matches the claimed
    /// identity of the peer standing in front of it.
    /// </para>
    ///
    /// <para>
    /// When no <see cref="IBiometricProvider"/> is registered, or the
    /// registered provider reports <see cref="IBiometricProvider.IsAvailable"/>
    /// = <c>false</c>, returns <see cref="BiometricVerificationResult.Failed"/>
    /// — biometrics are optional and never gate core mesh connectivity.
    /// </para>
    /// </summary>
    /// <param name="localFaceFrameRgbHwc">
    ///   Raw camera frame: width × height × 3 bytes, HWC layout, RGB,
    ///   values 0–255.
    /// </param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="referenceEmbedding">
    ///   The peer's identity embedding (from their AetherTag profile or PoV
    ///   packet).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///   A <see cref="BiometricVerificationResult"/> indicating whether the
    ///   face in <paramref name="localFaceFrameRgbHwc"/> matches
    ///   <paramref name="referenceEmbedding"/>.
    /// </returns>
    Task<BiometricVerificationResult> VerifyCoPresenceAsync(
        byte[]            localFaceFrameRgbHwc,
        int               width,
        int               height,
        FaceEmbedding     referenceEmbedding,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload for the <see cref="IHandshakeService.IncompatiblePeer"/> event.
/// </summary>
public sealed class IncompatiblePeerEventArgs : EventArgs
{
    public IncompatiblePeerEventArgs(string peerUhid, byte theirMin, byte theirMax, byte ourMin, byte ourMax, string reason)
    {
        PeerUhid = peerUhid;
        TheirMinVersion = theirMin;
        TheirMaxVersion = theirMax;
        OurMinVersion = ourMin;
        OurMaxVersion = ourMax;
        Reason = reason;
    }

    /// <summary>UHID of the incompatible peer.</summary>
    public string PeerUhid { get; }

    /// <summary>Lowest version the peer claimed to support.</summary>
    public byte TheirMinVersion { get; }

    /// <summary>Highest version the peer claimed to support.</summary>
    public byte TheirMaxVersion { get; }

    /// <summary>Lowest version we accept.</summary>
    public byte OurMinVersion { get; }

    /// <summary>Highest version we speak.</summary>
    public byte OurMaxVersion { get; }

    /// <summary>Human-readable explanation for the mismatch.</summary>
    public string Reason { get; }
}
