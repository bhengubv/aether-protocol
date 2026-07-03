/**
 * Multi-device sync — one state change to a synced item.
 *
 * A SyncRecord is the unit a user's device gossips to that user's *other*
 * devices so they all converge on the same state, with no server. The
 * encrypted payload is already end-to-end encrypted to the user's device set,
 * so any node that relays the record (mesh or DTN store-and-forward) learns
 * nothing about its content.
 *
 * Binary wire format (mirrors DtnEnvelope / PacketSerializer conventions):
 * all multi-byte integers little-endian; the 16-byte record id is the UUID in
 * RFC-4122 big-endian order; strings are uint16-LE length-prefixed UTF-8; the
 * encrypted payload is int32-LE length-prefixed raw bytes. Every envelope
 * begins with a single format-version byte — readers reject any other value.
 * Byte-identical across every AetherNet SDK (verified against
 * fixtures/sync/vectors.json).
 *
 * Layout: version(u8=1) · record_id(16, big-endian) · op(u8)
 * · logical_clock(i64 LE) · created_at_ms(i64 LE) · device_id(u16 len + utf8)
 * · item_id(u16 len + utf8) · encrypted_payload(i32 len + bytes).
 *
 * SPDX-License-Identifier: MIT
 */

/** The kind of state change a {@link SyncRecord} carries. */
export enum SyncOp {
  /** Create or update the item. */
  Upsert = 0,
  /** Delete the item. */
  Delete = 1,
  /** Mark the item read (read-state sync). */
  Read = 2,
}

/**
 * One state change to a synced item (a message, a read-marker, a deletion).
 *
 * `recordId` and `itemId` are the sync keys. `recordId` is a UUID string
 * (e.g. "00112233-4455-6677-8899-aabbccddeeff"); on the wire its 16 bytes are
 * written big-endian (RFC-4122 order).
 */
export interface SyncRecord {
  /** Globally-unique id for this record (UUID string). */
  recordId: string;
  /** The device that produced the record. */
  deviceId: string;
  /** Create/update, delete, or read-marker. */
  op: SyncOp;
  /** The item this record is about (the sync key). */
  itemId: string;
  /** The device's monotonic counter at emit time (i64 — use BigInt-safe compare). */
  logicalClock: bigint;
  /** Wall-clock time (Unix ms) the record was created (i64). */
  createdAtMs: bigint;
  /** The E2E-encrypted item content (opaque; empty for a delete/read). */
  encryptedPayload: Uint8Array;
}

/** Wire format version; readers reject any other value. */
export const SYNC_RECORD_VERSION = 0x01;

const MAX_SYNC_PAYLOAD = 2147483647; // int32 max

/** Serializes a record to its canonical bytes. */
export function serializeSyncRecord(record: SyncRecord): Uint8Array {
  const device = utf8(record.deviceId ?? "");
  const item = utf8(record.itemId ?? "");
  const payload = record.encryptedPayload ?? new Uint8Array(0);
  if (device.length > 0xffff) throw new Error("SyncRecord: DeviceId is too long.");
  if (item.length > 0xffff) throw new Error("SyncRecord: ItemId is too long.");
  if (payload.length > MAX_SYNC_PAYLOAD) throw new Error("SyncRecord: payload too large.");

  const size =
    1 + 16 + 1 + 8 + 8 +
    2 + device.length +
    2 + item.length +
    4 + payload.length;

  const buf = new Uint8Array(size);
  const dv = new DataView(buf.buffer);
  let o = 0;
  buf[o++] = SYNC_RECORD_VERSION;
  buf.set(uuidToBytes(record.recordId), o); o += 16;
  buf[o++] = record.op & 0xff;
  dv.setBigInt64(o, BigInt.asIntN(64, record.logicalClock), true); o += 8;
  dv.setBigInt64(o, BigInt.asIntN(64, record.createdAtMs), true); o += 8;
  o = writeStr(buf, dv, o, device);
  o = writeStr(buf, dv, o, item);
  dv.setInt32(o, payload.length, true); o += 4;
  buf.set(payload, o);
  return buf;
}

/** Parses canonical bytes back into a record, validating framing. */
export function deserializeSyncRecord(data: Uint8Array): SyncRecord {
  if (data.length < 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4) {
    throw new Error("SyncRecord is too short.");
  }
  const dv = new DataView(data.buffer, data.byteOffset, data.length);
  let o = 0;
  if (data[o++] !== SYNC_RECORD_VERSION) {
    throw new Error("Unsupported SyncRecord format version.");
  }
  const recordId = bytesToUuid(data.subarray(o, o + 16)); o += 16;
  const opByte = data[o++];
  if (opByte > SyncOp.Read) throw new Error("Unknown SyncRecord op.");
  const op = opByte as SyncOp;
  const logicalClock = dv.getBigInt64(o, true); o += 8;
  const createdAtMs = dv.getBigInt64(o, true); o += 8;
  let deviceId: string;
  [deviceId, o] = readStr(data, dv, o);
  let itemId: string;
  [itemId, o] = readStr(data, dv, o);

  if (o + 4 > data.length) throw new Error("SyncRecord payload length is truncated.");
  const payloadLen = dv.getInt32(o, true); o += 4;
  if (payloadLen < 0 || o + payloadLen > data.length) {
    throw new Error("SyncRecord payload length is invalid.");
  }
  const encryptedPayload = data.slice(o, o + payloadLen);

  return { recordId, deviceId, op, itemId, logicalClock, createdAtMs, encryptedPayload };
}

// ── Low-level helpers (mirror DtnEnvelope byte conventions) ──────────────────

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

function writeStr(buf: Uint8Array, dv: DataView, o: number, bytes: Uint8Array): number {
  if (bytes.length > 0xffff) throw new Error("SyncRecord string too long");
  dv.setUint16(o, bytes.length, true); o += 2;
  buf.set(bytes, o); o += bytes.length;
  return o;
}

/** Reads a u16-LE length-prefixed UTF-8 string; returns [value, newOffset]. */
function readStr(data: Uint8Array, dv: DataView, o: number): [string, number] {
  if (o + 2 > data.length) throw new Error("SyncRecord string length is truncated.");
  const len = dv.getUint16(o, true); o += 2;
  if (o + len > data.length) throw new Error("SyncRecord string is truncated.");
  const s = new TextDecoder().decode(data.subarray(o, o + len)); o += len;
  return [s, o];
}

/** UUID string → 16 bytes in RFC-4122 big-endian order (matches C# Guid bigEndian). */
export function uuidToBytes(uuidStr: string): Uint8Array {
  const hex = uuidStr.replace(/-/g, "");
  if (hex.length !== 32) throw new Error(`SyncRecord: invalid record id "${uuidStr}"`);
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  return bytes;
}

/** 16 bytes (big-endian) → canonical lower-case UUID string. */
export function bytesToUuid(bytes: Uint8Array): string {
  const hex = Array.from(bytes).map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
