/**
 * PreKey exchange data models (PacketType.PreKeyRequest = 25 / PreKeyResponse = 26).
 *
 * Directed mesh transport of a {@link PreKeyBundle}: a requester asks a peer for its published
 * bundle (PreKeyRequest) so it can start an X3DH session while the peer is offline; the responder
 * replies with its bundle (PreKeyResponse). TRANSPORT ONLY — no key agreement happens here; the
 * host feeds the received bundle to SignalProtocol.processPreKeyBundle (Signal-canonical).
 *
 * The wire payloads are UTF-8 JSON with snake_case keys, no whitespace, lowercase-dashed UUID, and
 * integer ids bare. All public-key `Uint8Array` fields are STANDARD base64 (RFC 4648, '+/'
 * alphabet, '=' padding — `Buffer.from(bytes).toString("base64")`). The encoding is byte-identical
 * across every language port (locked by fixtures/prekey/vectors.json).
 *
 * PreKeyRequest field order:  request_id, requester_uhid.
 * PreKeyResponse field order: request_id, uhid, identity_key, identity_key_x25519, pre_key_id,
 *   pre_key, signed_pre_key_id, signed_pre_key, signed_pre_key_signature.
 *
 * SPDX-License-Identifier: MIT
 */

import type { PreKeyBundle } from "../security/SignalProtocol.js";

/**
 * JSON payload for a PreKeyRequest packet — a directed ask for a peer's published
 * {@link PreKeyBundle}. The requester mints a request id and puts its own UHID in
 * `requesterUhid` so the responder knows where to direct the reply.
 */
export interface PreKeyRequestPayload {
  /** Correlation id minted by the requester; echoed in the response (lowercase-dashed UUID). */
  requestId: string;
  /** UHID of the node asking for the bundle — where the response is sent. */
  requesterUhid: string;
}

/**
 * JSON payload for a PreKeyResponse packet — the responder's published {@link PreKeyBundle}
 * carried back to the requester, echoing the originating request id.
 */
export interface PreKeyResponsePayload {
  /** Request id echoed from the originating PreKeyRequest (lowercase-dashed UUID). */
  requestId: string;
  /** UHID of the responder whose bundle this is. */
  uhid: string;
  /** Long-term Ed25519 identity public key (32 bytes). */
  identityKey: Uint8Array;
  /** Long-term X25519 identity public key (32 bytes raw, RFC 7748). */
  identityKeyX25519: Uint8Array;
  preKeyId: number;
  /** One-time pre-key X25519 public key (32 bytes raw). */
  preKey: Uint8Array;
  signedPreKeyId: number;
  /** Signed pre-key X25519 public key (32 bytes raw). */
  signedPreKey: Uint8Array;
  /** Ed25519 signature over signedPreKey (64 bytes). */
  signedPreKeySignature: Uint8Array;
}

/**
 * Event surfaced when a peer's pre-key bundle arrives in a PreKeyResponse. Feed
 * {@link bundle} to SignalProtocol.processPreKeyBundle to run X3DH.
 */
export interface PreKeyBundleReceived {
  /** Request id echoed from the original PreKeyRequest (empty if unsolicited). */
  requestId: string;
  /** UHID of the peer that sent the bundle (the inbound packet's source). */
  fromUhid: string;
  /** The received pre-key bundle. */
  bundle: PreKeyBundle;
}

/** Project a PreKeyResponse payload into a {@link PreKeyBundle}. */
export function responsePayloadToBundle(p: PreKeyResponsePayload): PreKeyBundle {
  return {
    uhid: p.uhid,
    identityKey: p.identityKey,
    identityKeyX25519: p.identityKeyX25519,
    preKeyId: p.preKeyId,
    preKey: p.preKey,
    signedPreKeyId: p.signedPreKeyId,
    signedPreKey: p.signedPreKey,
    signedPreKeySignature: p.signedPreKeySignature,
  };
}

/** Build a PreKeyResponse payload from a bundle, echoing the originating request id. */
export function responsePayloadFromBundle(
  requestId: string,
  b: PreKeyBundle,
): PreKeyResponsePayload {
  return {
    requestId,
    uhid: b.uhid,
    identityKey: b.identityKey,
    identityKeyX25519: b.identityKeyX25519,
    preKeyId: b.preKeyId,
    preKey: b.preKey,
    signedPreKeyId: b.signedPreKeyId,
    signedPreKey: b.signedPreKey,
    signedPreKeySignature: b.signedPreKeySignature,
  };
}
