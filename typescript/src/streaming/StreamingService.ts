/**
 * Live stream publish/subscribe service.
 *
 * Publishers broadcast StreamAnnounce and unicast StreamSegment to each
 * subscriber. Subscribers send StreamSubscribe / StreamUnsubscribe to the
 * publisher.
 *
 * Binary segment payload:
 *   [16] StreamId (UUID RFC4122 big-endian)
 *   [4]  Sequence (uint32 little-endian)
 *   [8]  TimestampMs (int64 little-endian)
 *   [1]  IsKeyframe (0 or 1)
 *   [N]  EncodedPayload
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { uuidToBytes, bytesToUuid } from "../voice/VoiceCallService.js";

// ──────────────────────────────────────────────────────────────────
// Constants
// ──────────────────────────────────────────────────────────────────

const ANNOUNCE_PRIORITY = 0;
const SEGMENT_PRIORITY = 32;
const SUB_PRIORITY = 16;

// ──────────────────────────────────────────────────────────────────
// Wire types (snake_case)
// ──────────────────────────────────────────────────────────────────

type StreamState = "live" | "ending" | "ended";

interface StreamAnnouncePayload {
  stream_id: string;
  title: string;
  content_type: string;
  codec: string;
  segment_duration_ms: number;
  state: StreamState;
  started_at_ms: number;
}

interface StreamSubscribePayload {
  stream_id: string;
  live_only: boolean;
}

interface StreamUnsubscribePayload {
  stream_id: string;
}

// ──────────────────────────────────────────────────────────────────
// Public types
// ──────────────────────────────────────────────────────────────────

export interface StreamInfo {
  streamId: string;
  publisherUhid: string;
  title: string;
  contentType: string;
  codec: string;
  segmentDurationMs: number;
  state: StreamState;
  startedAtMs: number;
}

export interface SegmentEvent {
  streamId: string;
  fromUhid: string;
  sequence: number;
  timestampMs: number;
  isKeyframe: boolean;
  encodedPayload: Uint8Array;
}

// ──────────────────────────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────────────────────────

export class StreamingService {
  /** streams we are publishing: streamId → info */
  private readonly publishedStreams = new Map<string, StreamInfo>();
  /** streams we are subscribed to: streamId → publisherUhid */
  private readonly subscribedStreams = new Map<string, string>();
  /** subscribers per stream we are publishing: streamId → Set<uhid> */
  private readonly subscribers = new Map<string, Set<string>>();
  /** known remote streams: streamId → info */
  private readonly knownStreams = new Map<string, StreamInfo>();

  private segmentSeq = 0;

  // Event callbacks
  onStreamAnnounced?: (info: StreamInfo) => void;
  onSubscriberJoined?: (streamId: string, uhid: string) => void;
  onSubscriberLeft?: (streamId: string, uhid: string) => void;
  onSegmentReceived?: (event: SegmentEvent) => void;
  onStreamEnded?: (streamId: string) => void;

  constructor(private readonly sender: IMeshSender) {}

  // ──────────────── publisher actions ─────────────────────────────

  async startStream(
    title: string,
    contentType: string,
    codec: string,
    segmentDurationMs: number,
  ): Promise<string> {
    const streamId = crypto.randomUUID();
    const now = Date.now();
    const info: StreamInfo = {
      streamId,
      publisherUhid: this.sender.localUhid,
      title,
      contentType,
      codec,
      segmentDurationMs,
      state: "live",
      startedAtMs: now,
    };
    this.publishedStreams.set(streamId, info);
    this.subscribers.set(streamId, new Set<string>());

    await this.broadcastAnnounce(info);
    return streamId;
  }

  async endStream(streamId: string): Promise<void> {
    const info = this.publishedStreams.get(streamId);
    if (!info) return;

    info.state = "ending";
    await this.broadcastAnnounce(info);

    info.state = "ended";
    await this.broadcastAnnounce(info);

    this.publishedStreams.delete(streamId);
    this.subscribers.delete(streamId);
    this.onStreamEnded?.(streamId);
  }

  async publishSegment(
    streamId: string,
    data: Uint8Array,
    isKeyframe: boolean,
  ): Promise<void> {
    const info = this.publishedStreams.get(streamId);
    if (!info || info.state !== "live") return;

    const subs = this.subscribers.get(streamId);
    if (!subs || subs.size === 0) return;

    const payload = encodeStreamSegment(streamId, data, isKeyframe, this.segmentSeq++);

    for (const uhid of subs) {
      const packet = new MeshPacket();
      packet.type = PacketType.StreamSegment;
      packet.sourceUhid = this.sender.localUhid;
      packet.destinationUhid = uhid;
      packet.ttl = DEFAULT_TTL;
      packet.priority = SEGMENT_PRIORITY;
      packet.payload = payload;
      await this.sender.send(packet, uhid);
    }
  }

  // ──────────────── subscriber actions ────────────────────────────

  async subscribe(
    streamId: string,
    publisherUhid: string,
    liveOnly: boolean,
  ): Promise<void> {
    this.subscribedStreams.set(streamId, publisherUhid);

    const body = new TextEncoder().encode(
      JSON.stringify({ stream_id: streamId, live_only: liveOnly } satisfies StreamSubscribePayload),
    );
    const packet = new MeshPacket();
    packet.type = PacketType.StreamSubscribe;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = publisherUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = SUB_PRIORITY;
    packet.payload = body;
    await this.sender.send(packet, publisherUhid);
  }

  async unsubscribe(streamId: string, publisherUhid: string): Promise<void> {
    this.subscribedStreams.delete(streamId);

    const body = new TextEncoder().encode(
      JSON.stringify({ stream_id: streamId } satisfies StreamUnsubscribePayload),
    );
    const packet = new MeshPacket();
    packet.type = PacketType.StreamUnsubscribe;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = publisherUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = SUB_PRIORITY;
    packet.payload = body;
    await this.sender.send(packet, publisherUhid);
  }

  // ──────────────── inbound packet routing ────────────────────────

  async onPacket(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.StreamAnnounce:
        this.handleAnnounce(packet);
        break;
      case PacketType.StreamSubscribe:
        this.handleSubscribe(packet);
        break;
      case PacketType.StreamUnsubscribe:
        this.handleUnsubscribe(packet);
        break;
      case PacketType.StreamSegment:
        this.handleSegment(packet);
        break;
      default:
        break;
    }
  }

  // ──────────────── inspection ─────────────────────────────────────

  getKnownStreams(): StreamInfo[] {
    return Array.from(this.knownStreams.values());
  }

  getSubscribers(streamId: string): string[] {
    return Array.from(this.subscribers.get(streamId) ?? []);
  }

  // ──────────────── private helpers ─────────────────────────────────

  private async broadcastAnnounce(info: StreamInfo): Promise<void> {
    const body = new TextEncoder().encode(
      JSON.stringify({
        stream_id: info.streamId,
        title: info.title,
        content_type: info.contentType,
        codec: info.codec,
        segment_duration_ms: info.segmentDurationMs,
        state: info.state,
        started_at_ms: info.startedAtMs,
      } satisfies StreamAnnouncePayload),
    );
    const packet = new MeshPacket();
    packet.type = PacketType.StreamAnnounce;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "";
    packet.ttl = DEFAULT_TTL;
    packet.priority = ANNOUNCE_PRIORITY;
    packet.payload = body;
    await this.sender.broadcast(packet);
  }

  private handleAnnounce(packet: MeshPacket): void {
    let msg: StreamAnnouncePayload;
    try {
      msg = JSON.parse(
        new TextDecoder().decode(packet.payload),
      ) as StreamAnnouncePayload;
    } catch {
      return;
    }
    if (!msg.stream_id) return;

    const info: StreamInfo = {
      streamId: msg.stream_id,
      publisherUhid: packet.sourceUhid,
      title: msg.title ?? "",
      contentType: msg.content_type ?? "",
      codec: msg.codec ?? "",
      segmentDurationMs: msg.segment_duration_ms ?? 0,
      state: msg.state,
      startedAtMs: msg.started_at_ms ?? Date.now(),
    };

    if (msg.state === "ended") {
      this.knownStreams.delete(msg.stream_id);
      this.subscribedStreams.delete(msg.stream_id);
      this.onStreamEnded?.(msg.stream_id);
    } else {
      this.knownStreams.set(msg.stream_id, info);
      this.onStreamAnnounced?.(info);
    }
  }

  private handleSubscribe(packet: MeshPacket): void {
    let msg: StreamSubscribePayload;
    try {
      msg = JSON.parse(
        new TextDecoder().decode(packet.payload),
      ) as StreamSubscribePayload;
    } catch {
      return;
    }
    if (!msg.stream_id) return;
    if (!this.publishedStreams.has(msg.stream_id)) return;

    const subs = this.subscribers.get(msg.stream_id) ?? new Set<string>();
    subs.add(packet.sourceUhid);
    this.subscribers.set(msg.stream_id, subs);
    this.onSubscriberJoined?.(msg.stream_id, packet.sourceUhid);
  }

  private handleUnsubscribe(packet: MeshPacket): void {
    let msg: StreamUnsubscribePayload;
    try {
      msg = JSON.parse(
        new TextDecoder().decode(packet.payload),
      ) as StreamUnsubscribePayload;
    } catch {
      return;
    }
    if (!msg.stream_id) return;

    const subs = this.subscribers.get(msg.stream_id);
    if (subs?.has(packet.sourceUhid)) {
      subs.delete(packet.sourceUhid);
      this.onSubscriberLeft?.(msg.stream_id, packet.sourceUhid);
    }
  }

  private handleSegment(packet: MeshPacket): void {
    const seg = decodeStreamSegment(packet.payload);
    if (!seg) return;

    this.onSegmentReceived?.({
      streamId: seg.streamId,
      fromUhid: packet.sourceUhid,
      sequence: seg.sequence,
      timestampMs: seg.timestampMs,
      isKeyframe: seg.isKeyframe,
      encodedPayload: seg.encodedPayload,
    });
  }
}

// ──────────────────────────────────────────────────────────────────
// Binary codec
// ──────────────────────────────────────────────────────────────────

/**
 * StreamSegment binary payload:
 * [16] StreamId (UUID RFC4122 big-endian)
 * [4]  Sequence (uint32 little-endian)
 * [8]  TimestampMs (int64 little-endian)
 * [1]  IsKeyframe (0 or 1)
 * [N]  EncodedPayload
 */
export function encodeStreamSegment(
  streamId: string,
  encodedPayload: Uint8Array,
  isKeyframe: boolean,
  sequence: number,
): Uint8Array {
  const buf = new Uint8Array(16 + 4 + 8 + 1 + encodedPayload.length);
  const dv = new DataView(buf.buffer);

  uuidToBytes(streamId, buf, 0);
  dv.setUint32(16, sequence >>> 0, true);
  dv.setBigInt64(20, BigInt(Date.now()), true);
  buf[28] = isKeyframe ? 1 : 0;
  buf.set(encodedPayload, 29);

  return buf;
}

export function decodeStreamSegment(data: Uint8Array): {
  streamId: string;
  sequence: number;
  timestampMs: number;
  isKeyframe: boolean;
  encodedPayload: Uint8Array;
} | null {
  if (data.length < 29) return null;
  const dv = new DataView(data.buffer, data.byteOffset, data.byteLength);

  const streamId = bytesToUuid(data, 0);
  const sequence = dv.getUint32(16, true);
  const timestampMs = Number(dv.getBigInt64(20, true));
  const isKeyframe = data[28] !== 0;
  const encodedPayload = data.slice(29);

  return { streamId, sequence, timestampMs, isKeyframe, encodedPayload };
}
