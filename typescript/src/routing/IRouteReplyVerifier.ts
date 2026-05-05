/**
 * RREP verifier interface.
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";

export interface IRouteReplyVerifier {
  verify(routeReply: MeshPacket): Promise<boolean>;
}

/**
 * Permissive default — accepts every RREP. Suitable for tests and trust-the-fabric
 * demos; production hosts wire up a real verifier backed by their security service.
 */
export class AcceptAllRouteReplyVerifier implements IRouteReplyVerifier {
  async verify(_routeReply: MeshPacket): Promise<boolean> {
    return true;
  }
}
