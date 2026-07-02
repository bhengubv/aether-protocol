/**
 * VoicePtt(15) + ScreenShare(32) directed media frames (PacketType.VoicePtt = 15 /
 * PacketType.ScreenShare = 32). BINARY frames sharing the 29-byte header.
 * SPDX-License-Identifier: MIT
 */

export {
  MediaFrameCodec,
  VoicePttService,
  ScreenShareService,
} from "./MediaFrameService.js";
export type {
  VoicePttFrame,
  ScreenShareFrame,
  VoicePttFrameReceived,
  ScreenShareFrameReceived,
} from "./MediaFrameService.js";
