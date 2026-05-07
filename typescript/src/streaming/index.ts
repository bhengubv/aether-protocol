/**
 * Streaming, video call, and watch-together services for the Aether mesh.
 * SPDX-License-Identifier: MIT
 */

export {
  StreamingService,
  encodeStreamSegment,
  decodeStreamSegment,
} from "./StreamingService.js";
export type { StreamInfo, SegmentEvent } from "./StreamingService.js";

export {
  VideoCallService,
  encodeVideoFrame,
  decodeVideoFrame,
} from "./VideoCallService.js";
export type {
  VideoCallState,
  VideoCallInfo,
  VideoFrameEvent,
  VideoQualityParams,
} from "./VideoCallService.js";

export { WatchTogetherService } from "./WatchTogetherService.js";
export type {
  WatchSession,
  SyncAppliedEvent,
  ReactionEvent,
} from "./WatchTogetherService.js";
