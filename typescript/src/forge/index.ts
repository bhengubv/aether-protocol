/**
 * aether-forge package cache: in-memory store + WIRE binding
 * (PacketType.ForgeAnnounce = 41).
 * SPDX-License-Identifier: MIT
 */

export { InMemoryForgeService } from "./ForgeService.js";
export type { IForgeService, ForgeEntry, ForgeStats } from "./ForgeService.js";
export {
  ForgeAnnounceService,
  serializeForgeAnnouncePayload,
  deserializeForgeAnnouncePayload,
} from "./ForgeAnnounceService.js";
export type { ForgeAnnouncePayload } from "./ForgeAnnounceService.js";
