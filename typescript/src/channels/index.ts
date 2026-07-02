/**
 * Named-channel pub/sub for the Aether mesh (PacketType.ChannelMessage = 7).
 * SPDX-License-Identifier: MIT
 */

export {
  ChannelMessageService,
  serializeChannelMessagePayload,
  deserializeChannelMessagePayload,
} from "./ChannelMessageService.js";
export type { ChannelMessagePayload, ChannelMessageReceived } from "./models.js";
