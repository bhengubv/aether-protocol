// SPDX-License-Identifier: MIT

package aether.handshake

import java.time.Instant

/**
 * The negotiated protocol-version + capability set for a remote peer, locked
 * in once the Hello/HelloAck exchange completes (or after the backward-compat
 * timeout for peers that never replied).
 *
 * The [negotiatedVersion] is the highest protocol version both sides advertised
 * support for. The [capabilities] set is the intersection of both sides'
 * advertised capability tags — services should gate optional features
 * (Double-Ratchet, DTN custody, voice, etc.) on capability presence rather
 * than on raw protocol-version.
 *
 * @property peerUhid UHID of the peer this record describes.
 * @property negotiatedVersion Highest mutually-supported protocol version.
 *   Defaults to `1` for peers that never replied with a HelloAck (backward-compat).
 * @property capabilities Intersection of capability tags both sides claim to
 *   support. Empty for peers that never replied.
 * @property implementationVersion Free-form implementation banner the peer
 *   announced (e.g. `"aether-csharp/1.0.0"`). Empty for peers that never replied.
 * @property negotiatedAt UTC timestamp when negotiation completed.
 */
data class PeerCapabilities(
    val peerUhid: String,
    val negotiatedVersion: Byte,
    val capabilities: Set<String>,
    val implementationVersion: String,
    val negotiatedAt: Instant,
)

/**
 * Reason payload fired when a peer's announced version range does not
 * overlap with ours, or its range is otherwise malformed.
 *
 * Mirrors the C# `IncompatiblePeerEventArgs`.
 */
data class IncompatiblePeerEvent(
    /** UHID of the incompatible peer. */
    val peerUhid: String,
    /** Lowest version the peer claimed to support. */
    val theirMinVersion: Byte,
    /** Highest version the peer claimed to support. */
    val theirMaxVersion: Byte,
    /** Lowest version we accept. */
    val ourMinVersion: Byte,
    /** Highest version we speak. */
    val ourMaxVersion: Byte,
    /** Human-readable explanation for the mismatch. */
    val reason: String,
)
