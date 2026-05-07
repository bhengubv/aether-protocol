/**
 * Voice call services for the Aether mesh.
 * SPDX-License-Identifier: MIT
 */

export {
  VoiceCallService,
  encodeVoiceFrame,
  decodeVoiceFrame,
  uuidToBytes,
  bytesToUuid,
} from "./VoiceCallService.js";
export type {
  VoiceCallState,
  VoiceCallInfo,
  VoiceFrameEvent,
} from "./VoiceCallService.js";

export {
  GroupVoiceCallService,
  encodeGroupVoiceFrame,
  decodeGroupVoiceFrame,
} from "./GroupVoiceCallService.js";
export type {
  GroupCallState,
  GroupCallInfo,
  GroupFrameEvent,
} from "./GroupVoiceCallService.js";
