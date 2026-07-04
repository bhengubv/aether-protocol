// SPDX-License-Identifier: MIT

import Foundation

/// Verifies that a received RREP was actually signed by the node it claims to come from.
///
/// **Threat — RREP hijack.** AODV-style reactive routing installs a forward route straight
/// from an RREP's `sourceUhid`. Any intermediate forwarder that sees a route-request flood
/// can fabricate an RREP claiming to be the destination, poison every hop's route table, and
/// pull the victim's traffic onto itself (blackhole / man-in-the-middle). The only defence is
/// to require a valid source signature on the RREP before trusting it.
///
/// **Fail-closed by default.** The `RoutingService` default is now
/// ``RejectAllRouteReplyVerifier`` — an absent or partial verifier must never silently trust
/// unverified route replies. A host that ships a real implementation (typically
/// ``Ed25519RouteReplyVerifier``) opts in to actually validating signatures; until it does, no
/// RREP is accepted and no forward route is installed.
public protocol RouteReplyVerifier: Sendable {
    /// Returns `true` only if `routeReply` is proven authentic (validly signed by the node it
    /// claims to originate from).
    func verify(_ routeReply: MeshPacket) async -> Bool
}

/// Fail-closed verifier: every RREP is **REJECTED**. This is the safe default the
/// ``RoutingService`` falls back to when no verifier is supplied — an unverified route reply is
/// never trusted, so the RREP-hijack attack surface is closed until a host wires a real
/// signature verifier. Route discovery for peers that would otherwise reply legitimately will
/// simply not complete under this verifier; that is intentional (correctness over availability
/// for an unconfigured node).
public struct RejectAllRouteReplyVerifier: RouteReplyVerifier {
    public init() {}
    public func verify(_ routeReply: MeshPacket) async -> Bool { false }
}

/// **INSECURE.** Accepts every RREP without any signature check. This is an explicit opt-in
/// escape hatch for unit tests that exercise routing *mechanics* (forwarding, caching, TTL) and
/// for trust-the-fabric demos on a closed, fully-trusted network. It provides **no** protection
/// against RREP hijack and MUST NOT be used in production or on any open mesh — a single
/// malicious forwarder can blackhole traffic. It is deliberately NOT the default: callers have
/// to reach for it by name so the choice to disable verification is visible in the code.
public struct AcceptAllRouteReplyVerifier: RouteReplyVerifier {
    public init() {}
    public func verify(_ routeReply: MeshPacket) async -> Bool { true }
}

/// Resolves the Ed25519 public key of a node given its source UHID, so an RREP's signature can
/// be checked against the identity it claims. Returns `nil` when the UHID is unknown — the
/// verifier treats an unresolvable signer as untrusted and rejects the RREP (fail-closed: an
/// unknown key can never produce a valid signature we would accept).
///
/// No shared peer-key directory exists in the protocol today — callers that verify packets
/// (reputation gossip, PoV token exchange) pass the sender public key in explicitly. This
/// minimal resolver abstracts "UHID → public key" for the routing layer so a host can plug in
/// whatever key source it already maintains (handshake-established keys, a published identity
/// directory, a prekey/identity store, etc.) without the routing layer depending on any one of
/// them.
public protocol RouteReplyKeyResolver: Sendable {
    /// Returns the Ed25519 public key registered for `sourceUhid`, or `nil` if the node is
    /// unknown. A `nil` result causes the RREP to be rejected.
    func resolvePublicKey(_ sourceUhid: String) -> Data?
}

/// Production ``RouteReplyVerifier``: accepts an RREP only if it carries a valid Ed25519
/// signature produced by the node it claims to originate from.
///
/// This closes the RREP-hijack hole. An AODV forward route is installed straight from an RREP's
/// `sourceUhid`; without a signature check, any intermediate forwarder can forge an RREP for the
/// destination and blackhole / man-in-the-middle the victim's traffic. Here we resolve the
/// claimed source's public key and verify the signature over the EXACT same canonical bytes the
/// source signed (``PacketSigningService/buildSignableData(_:)``), so a forged or unsigned RREP
/// fails and no route is installed.
///
/// **Fail-closed at every branch:** a missing signature, an unresolvable / unknown source key,
/// or a signature that does not verify all return `false`. Only a signature that validates
/// against a known key is accepted.
///
/// Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here — that is already
/// enforced by ``PacketSigningService`` in the packet-ingest pipeline. This verifier is purely
/// the source-identity gate the routing layer needs before trusting a route reply.
public struct Ed25519RouteReplyVerifier: RouteReplyVerifier {
    private let keyResolver: any RouteReplyKeyResolver

    /// Creates the verifier.
    /// - Parameter keyResolver: Resolves an RREP source UHID to its Ed25519 public key. A `nil`
    ///   result (unknown signer) causes the RREP to be rejected.
    public init(keyResolver: any RouteReplyKeyResolver) {
        self.keyResolver = keyResolver
    }

    public func verify(_ routeReply: MeshPacket) async -> Bool {
        // No signature → cannot be trusted. (MeshPacket.signature defaults to an empty Data.)
        guard !routeReply.signature.isEmpty else {
            return false
        }

        // Resolve the claimed source's public key. Unknown signer → reject (fail-closed): an
        // unresolvable key can never produce a signature we would accept.
        guard let publicKey = keyResolver.resolvePublicKey(routeReply.sourceUhid),
              !publicKey.isEmpty else {
            return false
        }

        // Verify the Ed25519 signature over the canonical signable bytes — the SAME layout the
        // source signed and every other language implementation shares.
        let signableData = PacketSigningService.buildSignableData(routeReply)
        return Ed25519Service.verify(publicKey, signableData, routeReply.signature)
    }
}
