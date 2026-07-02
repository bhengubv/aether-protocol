/**
 * Video call-control for the Aether mesh (PacketType.VideoCall = 27).
 * SPDX-License-Identifier: MIT
 */

export {
  VideoCallControlService,
  serializeVideoCallControlPayload,
  deserializeVideoCallControlPayload,
} from "./VideoCallControlService.js";
export type { VideoCallControlPayload, VideoCallStateChanged } from "./models.js";
