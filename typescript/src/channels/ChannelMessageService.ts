/**
 * Default named-channel pub/sub service (PacketType.ChannelMessage = 7). A node subscribes to
 * channel ids it cares about; publishing floods the mesh; subscribed receivers surface the
 * message via onMessageReceived. Messages are de-duplicated by messageId and re-flooded
 * (TTL-bounded) so they reach subscribers several hops away.
 *
 * Mirrors the C# ChannelMessageService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { ChannelMessagePayload, ChannelMessageReceived } from "./models.js";

export class ChannelMessageService {
  private readonly subscriptions = new Set<string>();
  private readonly seen = new Set<string>();

  /** Raised when a message arrives on a subscribed channel (not raised for this node's own messages). */
  onMessageReceived?: (message: ChannelMessageReceived) => void;

  constructor(private readonly sender: IMeshSender) {}

  /** Subscribe to a channel — messages on it will raise onMessageReceived. */
  subscribe(channelId: string): void {
    if (!channelId) throw new Error("channelId must not be empty");
    this.subscriptions.add(channelId);
  }

  /** Stop surfacing messages for a channel. */
  unsubscribe(channelId: string): void {
    this.subscriptions.delete(channelId);
  }

  /** The channels this node is currently subscribed to. */
  getSubscriptions(): string[] {
    return Array.from(this.subscriptions);
  }

  /**
   * Publish `content` to `channelId`: floods a PacketType.ChannelMessage (dest "*", default TTL)
   * to all peers. Returns the number of peers reached directly.
   */
  async publish(channelId: string, content: string): Promise<number> {
    if (!channelId) throw new Error("channelId must not be empty");
    if (content === null || content === undefined) throw new Error("content must not be null");

    const payload: ChannelMessagePayload = {
      channelId,
      messageId: crypto.randomUUID(),
      senderUhid: this.sender.localUhid,
      content,
      sentAtMs: Date.now(),
    };
    this.seen.add(payload.messageId); // never re-handle our own message when it floods back

    const body = new TextEncoder().encode(serializeChannelMessagePayload(payload));

    const packet = new MeshPacket();
    packet.type = PacketType.ChannelMessage;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;

    return this.sender.broadcast(packet);
  }

  /**
   * Process an incoming PacketType.ChannelMessage packet: de-dup by message id, surface it if we
   * are subscribed to its channel (and it is not our own), and re-flood while TTL allows. Returns
   * false for the wrong packet type, a malformed payload, or a duplicate.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.ChannelMessage) return false;

    let body: ChannelMessagePayload | undefined;
    try {
      body = deserializeChannelMessagePayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.channelId) return false;

    // Flood de-duplication: only the first copy of a given message id is processed.
    if (this.seen.has(body.messageId)) return false;
    this.seen.add(body.messageId);

    const isOwn = body.senderUhid === this.sender.localUhid;
    if (!isOwn && this.subscriptions.has(body.channelId)) {
      this.onMessageReceived?.({
        channelId: body.channelId,
        messageId: body.messageId,
        senderUhid: body.senderUhid,
        content: body.content,
        sentAtMs: body.sentAtMs,
      });
    }

    // Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
    if (packet.ttl > 1 && !isOwn) {
      packet.ttl -= 1;
      await this.sender.broadcast(packet);
    }

    return true;
  }
}

/**
 * Canonical ChannelMessage payload serialization — MUST be byte-identical across all language
 * ports (fixtures/channels/vectors.json): snake_case keys, field order channel_id, message_id,
 * sender_uhid, content, sent_at_ms, no whitespace, lowercase-dashed UUID, sent_at_ms a bare int.
 */
export function serializeChannelMessagePayload(p: ChannelMessagePayload): string {
  return JSON.stringify({
    channel_id: p.channelId,
    message_id: p.messageId,
    sender_uhid: p.senderUhid,
    content: p.content,
    sent_at_ms: p.sentAtMs,
  });
}

/** Parse a canonical ChannelMessage payload back into camelCase fields. */
export function deserializeChannelMessagePayload(bytes: Uint8Array): ChannelMessagePayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    channel_id?: string;
    message_id?: string;
    sender_uhid?: string;
    content?: string;
    sent_at_ms?: number;
  };
  return {
    channelId: data.channel_id ?? "",
    messageId: data.message_id ?? "",
    senderUhid: data.sender_uhid ?? "",
    content: data.content ?? "",
    sentAtMs: data.sent_at_ms ?? 0,
  };
}
