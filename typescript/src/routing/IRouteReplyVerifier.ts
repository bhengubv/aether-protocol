/**
 * RREP verifier interface + fail-closed defaults.
 *
 * Threat — RREP hijack. AODV-style reactive routing installs a forward route
 * straight from an RREP's sourceUhid. Any intermediate forwarder that sees a
 * route-request flood can fabricate an RREP claiming to be the destination,
 * poison every hop's route table, and pull the victim's traffic onto itself
 * (blackhole / man-in-the-middle). The only defence is to require a valid
 * source signature on the RREP before trusting it.
 *
 * Fail-closed by default. The default the RoutingService falls back to now
 * REJECTS every RREP (see {@link RejectAllRouteReplyVerifier}): an absent or
 * half-built verifier must never silently trust unverified route replies. A
 * host that ships a real implementation (e.g. the Ed25519 verifier in
 * security/Ed25519RouteReplyVerifier) opts in to actually validating
 * signatures; until it does, no RREP is accepted and no forward route is
 * installed.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";

export interface IRouteReplyVerifier {
  /**
   * Returns true only if `routeReply` is proven authentic (validly signed by
   * the node it claims to originate from). Implementations must be fail-closed:
   * a missing signature, an unknown signer, or a bad signature all return false.
   */
  verify(routeReply: MeshPacket): Promise<boolean>;
}

/**
 * Fail-closed verifier: every RREP is REJECTED. This is the safe default the
 * {@link RoutingService} falls back to when no verifier is supplied — an
 * unverified route reply is never trusted, so the RREP-hijack attack surface is
 * closed until a host wires a real signature verifier. Route discovery for
 * peers that would otherwise reply legitimately will simply not complete under
 * this verifier; that is intentional (correctness over availability for an
 * unconfigured node).
 */
export class RejectAllRouteReplyVerifier implements IRouteReplyVerifier {
  async verify(_routeReply: MeshPacket): Promise<boolean> {
    return false;
  }
}

/**
 * INSECURE. Accepts every RREP without any signature check. This is an explicit
 * opt-in escape hatch for unit tests that exercise routing *mechanics*
 * (forwarding, caching, TTL) and for trust-the-fabric demos on a closed, fully
 * trusted network. It provides NO protection against RREP hijack and MUST NOT
 * be used in production or on any open mesh — a single malicious forwarder can
 * blackhole traffic. It is deliberately NOT the default: callers have to reach
 * for it by name so the choice to disable verification is visible in the code.
 */
export class AcceptAllRouteReplyVerifier implements IRouteReplyVerifier {
  async verify(_routeReply: MeshPacket): Promise<boolean> {
    return true;
  }
}

/**
 * Resolves the Ed25519 public key of a node given its source UHID, so an RREP's
 * signature can be checked against the identity it claims. Returns `undefined`
 * when the UHID is unknown — the verifier treats an unresolvable signer as
 * untrusted and rejects the RREP (fail-closed: an unknown key can never produce
 * a valid signature we would accept).
 *
 * No shared peer-key directory exists in the protocol today — callers that
 * verify packets (reputation gossip, PoV token exchange) pass the sender public
 * key in explicitly. This minimal resolver abstracts "UHID → public key" for
 * the routing layer so a host can plug in whatever key source it already
 * maintains (handshake-established keys, a published identity directory, a
 * prekey/identity store, etc.) without the routing layer taking a dependency on
 * any one of them.
 */
export interface IRouteReplyKeyResolver {
  /**
   * Returns the Ed25519 public key registered for `sourceUhid`, or `undefined`
   * if the node is unknown. An `undefined` result causes the RREP to be
   * rejected.
   */
  resolvePublicKey(sourceUhid: string): Uint8Array | undefined;
}
