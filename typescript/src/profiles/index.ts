/**
 * Directed peer-profile exchange for the Aether mesh (PacketType.ProfileSync = 23).
 * SPDX-License-Identifier: MIT
 */

export {
  ProfileService,
  serializeProfileSyncPayload,
  deserializeProfileSyncPayload,
} from "./ProfileService.js";
export type { ProfileSyncPayload } from "./models.js";
