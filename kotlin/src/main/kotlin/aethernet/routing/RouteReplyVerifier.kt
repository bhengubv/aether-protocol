// SPDX-License-Identifier: MIT

package aethernet.routing

import aethernet.protocol.MeshPacket

/**
 * Verifies that a received RREP was actually signed by the node it claims to come from.
 * Without this check an intermediate forwarder can forge an RREP and hijack traffic for
 * the destination. Hosts ship a real implementation backed by their security service;
 * the default AcceptAll is permissive — fine for tests, not for production.
 */
interface RouteReplyVerifier {
    suspend fun verify(routeReply: MeshPacket): Boolean = true
}

/** Permissive default — accepts every RREP without verification. */
class AcceptAllRouteReplyVerifier : RouteReplyVerifier
