/**
 * Presence — privacy-preserving "I'm here" broadcast + "who's around here?" solicitation
 * (PacketType.PresenceBeacon = 21 / PresenceQuery = 22).
 * SPDX-License-Identifier: MIT
 */

export { PresenceService } from "./PresenceService.js";
export {
  PresenceStatus,
  serializePresenceBeaconPayload,
  deserializePresenceBeaconPayload,
  serializePresenceQueryPayload,
  deserializePresenceQueryPayload,
} from "./models.js";
export type {
  PresenceBeaconPayload,
  PresenceQueryPayload,
  PresenceBeaconReceived,
  PresenceQueryReceived,
} from "./models.js";
