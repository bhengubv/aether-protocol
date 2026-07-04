// SPDX-License-Identifier: MIT

package aethernet.security

import aethernet.protocol.MeshPacket
import aethernet.routing.RouteReplyKeyResolver
import aethernet.routing.RouteReplyVerifier
import org.slf4j.LoggerFactory

/**
 * Production [RouteReplyVerifier]: accepts an RREP only if it carries a valid Ed25519 signature
 * produced by the node it claims to originate from.
 *
 * This closes the RREP-hijack hole. An AODV forward route is installed straight from an RREP's
 * `sourceUhid`; without a signature check, any intermediate forwarder can forge an RREP for the
 * destination and blackhole / man-in-the-middle the victim's traffic. Here we resolve the claimed
 * source's public key and verify the signature over the exact same canonical bytes the source
 * signed ([PacketSigning.constructSignableData]), so a forged or unsigned RREP fails and no route
 * is installed.
 *
 * **Fail-closed at every branch:** a missing signature, an unresolvable / unknown source key, or a
 * signature that does not verify all return `false`. Only a signature that validates against a
 * known key is accepted.
 *
 * Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here — that is already
 * enforced by [PacketSigning] in the packet-ingest pipeline. This verifier is purely the
 * source-identity gate the routing layer needs before trusting a route reply.
 *
 * @param keyResolver Resolves an RREP source UHID to its Ed25519 public key. A null result
 *   (unknown signer) causes the RREP to be rejected.
 * @param reputation Optional reputation hook. When set, a signature that fails to verify against a
 *   known key down-scores the offending source (same semantics as [PacketSigning]). `null` (the
 *   default) disables the side-effect without changing validation semantics.
 */
class Ed25519RouteReplyVerifier(
    private val keyResolver: RouteReplyKeyResolver,
    private val reputation: NodeReputationService? = null,
) : RouteReplyVerifier {

    private val logger = LoggerFactory.getLogger(Ed25519RouteReplyVerifier::class.java)

    override suspend fun verify(routeReply: MeshPacket): Boolean {
        // No signature → cannot be trusted. (MeshPacket.signature defaults to an empty array.)
        if (routeReply.signature.isEmpty()) {
            logger.warn("RREP from {} rejected: unsigned", routeReply.sourceUhid)
            return false
        }

        // Resolve the claimed source's public key. Unknown signer → reject (fail-closed):
        // an unresolvable key can never produce a signature we would accept.
        val publicKey = keyResolver.resolvePublicKey(routeReply.sourceUhid)
        if (publicKey == null || publicKey.isEmpty()) {
            logger.warn("RREP from {} rejected: source public key unknown", routeReply.sourceUhid)
            return false
        }

        // Verify the Ed25519 signature over the canonical signable bytes — the SAME layout the
        // source signed and every other language implementation shares.
        val signableData = PacketSigning.constructSignableData(routeReply)
        val valid = Ed25519Service.verify(publicKey, signableData, routeReply.signature)

        if (!valid) {
            logger.warn("RREP from {} rejected: invalid signature", routeReply.sourceUhid)
            reputation?.recordSignatureFailure(routeReply.sourceUhid)
        }

        return valid
    }
}
