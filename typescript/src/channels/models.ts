/**
 * Channel message data models (PacketType.ChannelMessage = 7).
 *
 * The wire payload is UTF-8 JSON with snake_case keys and field order `channel_id`,
 * `message_id`, `sender_uhid`, `content`, `sent_at_ms` — no whitespace, lowercase-dashed
 * UUID, `sent_at_ms` a bare integer — so the encoding is byte-identical across every
 * language port (locked by fixtures/channels/vectors.json).
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * JSON payload for a ChannelMessage packet. A named channel is an application-layer pub/sub
 * topic ("res-floor-3", a society, a project team). Publishing floods a ChannelMessage; nodes
 * subscribed to `channelId` surface it. The original author is carried in `senderUhid` so it
 * survives relay hops (the enclosing packet's sourceUhid changes at each hop).
 */
export interface ChannelMessagePayload {
  /** Application-defined channel identifier (opaque to the protocol). */
  channelId: string;
  /** Unique id for this message — used for flood de-duplication (lowercase-dashed UUID). */
  messageId: string;
  /** UHID of the original author (preserved across relay hops). */
  senderUhid: string;
  /** Message body. */
  content: string;
  /** Unix timestamp in milliseconds when the author published the message. */
  sentAtMs: number;
}

/**
 * Event surfaced when a channel message arrives on a channel this node is subscribed to.
 */
export interface ChannelMessageReceived {
  /** Channel the message was published to. */
  channelId: string;
  /** Unique id of the message. */
  messageId: string;
  /** UHID of the original author. */
  senderUhid: string;
  /** Message body. */
  content: string;
  /** Unix-ms timestamp the author published the message. */
  sentAtMs: number;
}
