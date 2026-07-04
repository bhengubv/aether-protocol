/**
 * Production IRouteReplyVerifier: accepts an RREP only if it carries a valid
 * Ed25519 signature produced by the node it claims to originate from.
 *
 * This closes the RREP-hijack hole. An AODV forward route is installed straight
 * from an RREP's sourceUhid; without a signature check, any intermediate
 * forwarder can forge an RREP for the destination and blackhole / man-in-the-
 * middle the victim's traffic. Here we resolve the claimed source's public key
 * and verify the signature over the exact same canonical bytes the source
 * signed ({@link buildSignableData}), so a forged or unsigned RREP fails and no
 * route is installed.
 *
 * Fail-closed at every branch: a missing signature, an unresolvable / unknown
 * source key, or a signature that does not verify all return `false`. Only a
 * signature that validates against a known key is accepted.
 *
 * Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here —
 * that is already enforced by PacketSigningService in the packet-ingest
 * pipeline. This verifier is purely the source-identity gate the routing layer
 * needs before trusting a route reply.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import {
  IRouteReplyVerifier,
  IRouteReplyKeyResolver,
} from "../routing/IRouteReplyVerifier.js";
import { buildSignableData } from "./PacketSigning.js";
import { Ed25519Service } from "./Ed25519Service.js";

export class Ed25519RouteReplyVerifier implements IRouteReplyVerifier {
  /**
   * @param keyResolver Resolves an RREP source UHID to its Ed25519 public key.
   *   An `undefined` result (unknown signer) causes the RREP to be rejected.
   */
  constructor(private readonly keyResolver: IRouteReplyKeyResolver) {}

  async verify(routeReply: MeshPacket): Promise<boolean> {
    // No signature → cannot be trusted. (MeshPacket.signature defaults to an
    // empty array.)
    if (!routeReply.signature || routeReply.signature.length === 0) {
      return false;
    }

    // Resolve the claimed source's public key. Unknown signer → reject
    // (fail-closed): an unresolvable key can never produce a signature we would
    // accept.
    const publicKey = this.keyResolver.resolvePublicKey(routeReply.sourceUhid);
    if (!publicKey || publicKey.length === 0) {
      return false;
    }

    // Verify the Ed25519 signature over the canonical signable bytes — the SAME
    // layout the source signed and every other language implementation shares.
    const signableData = buildSignableData(routeReply);
    return Ed25519Service.verify(publicKey, signableData, routeReply.signature);
  }
}
