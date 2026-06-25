/**
 * Binary DTN-envelope serialization — the cross-language wire format for the
 * three DTN packet bodies (bundle / custody-ack / delivery-receipt) carried in
 * MeshPacket.payload. Conventions mirror PacketSerializer: all multi-byte
 * integers little-endian; the 16-byte bundle id is the UUID in RFC-4122
 * big-endian order; strings are uint16-LE length-prefixed UTF-8; the encrypted
 * payload is int32-LE length-prefixed raw bytes. Every envelope begins with a
 * single format-version byte so the format can evolve without a flag-day — a
 * reader rejects any unknown version.
 *
 * Cleartext routing fields are laid out first and the opaque encrypted payload
 * last, so a later version can encrypt sender/recipient with no field-shuffle.
 *
 * SPDX-License-Identifier: MIT
 */

import { BundlePriority, BundleStatus, DtnBundle, DtnDeliveryReceipt } from "../models/index.js";

export const DTN_ENVELOPE_VERSION = 0x01;
const MAX_ENVELOPE_PAYLOAD = 16 * 1024 * 1024;

// ── Bundle ───────────────────────────────────────────────────────────────────

export function serializeBundle(b: DtnBundle): Uint8Array {
  const senderU = utf8(b.senderUhid);
  const recipU = utf8(b.recipientUhid);
  const senderG = utf8(b.senderGeohash ?? "");
  const recipG = utf8(b.recipientLastGeohash ?? "");
  const payload = b.encryptedPayload;

  const size =
    1 + 16 + 1 + 1 + 4 + 4 + 4 + 8 + 8 +
    2 + senderU.length + 2 + recipU.length +
    2 + senderG.length + 2 + recipG.length +
    4 + payload.length;

  const buf = new Uint8Array(size);
  const dv = new DataView(buf.buffer);
  let o = 0;
  buf[o++] = DTN_ENVELOPE_VERSION;
  buf.set(uuidToBytes(b.id), o); o += 16;
  buf[o++] = b.priority & 0xff;
  buf[o++] = b.status & 0xff;
  dv.setInt32(o, b.copyCount, true); o += 4;
  dv.setInt32(o, b.maxCopies, true); o += 4;
  dv.setInt32(o, b.hopCount, true); o += 4;
  dv.setBigInt64(o, BigInt(b.createdAt.getTime()), true); o += 8;
  dv.setBigInt64(o, BigInt(b.expiresAt.getTime()), true); o += 8;
  o = writeStr(buf, dv, o, senderU);
  o = writeStr(buf, dv, o, recipU);
  o = writeStr(buf, dv, o, senderG);
  o = writeStr(buf, dv, o, recipG);
  if (payload.length > MAX_ENVELOPE_PAYLOAD) throw new Error("DTN payload too large");
  dv.setInt32(o, payload.length, true); o += 4;
  buf.set(payload, o);
  return buf;
}

export function deserializeBundle(data: Uint8Array): DtnBundle {
  const r = new Reader(data);
  r.expectVersion();
  const id = r.uuid();
  const priority = r.u8();
  if (priority > BundlePriority.Sos) throw new Error(`DTN: invalid priority ${priority}`);
  const status = r.u8();
  if (status > BundleStatus.Failed) throw new Error(`DTN: invalid status ${status}`);
  const copyCount = r.i32();
  const maxCopies = r.i32();
  const hopCount = r.i32();
  const createdAtMs = r.i64();
  const expiresAtMs = r.i64();
  const senderUhid = r.str();
  const recipientUhid = r.str();
  const senderGeohash = r.str();
  const recipientLastGeohash = r.str();
  const encryptedPayload = r.bytes32();
  return {
    id,
    senderUhid,
    recipientUhid,
    encryptedPayload,
    priority: priority as BundlePriority,
    status: status as BundleStatus,
    copyCount,
    maxCopies,
    senderGeohash,
    recipientLastGeohash,
    hopCount,
    createdAt: new Date(createdAtMs),
    expiresAt: new Date(expiresAtMs),
  };
}

// ── Custody-ack ──────────────────────────────────────────────────────────────

export function serializeCustodyAck(bundleId: string, accepted: boolean): Uint8Array {
  const buf = new Uint8Array(18);
  buf[0] = DTN_ENVELOPE_VERSION;
  buf.set(uuidToBytes(bundleId), 1);
  buf[17] = accepted ? 0x01 : 0x00;
  return buf;
}

export function deserializeCustodyAck(data: Uint8Array): { bundleId: string; accepted: boolean } {
  const r = new Reader(data);
  r.expectVersion();
  const bundleId = r.uuid();
  const accepted = r.u8() !== 0;
  return { bundleId, accepted };
}

// ── Delivery-receipt ─────────────────────────────────────────────────────────

export function serializeDeliveryReceipt(r: DtnDeliveryReceipt): Uint8Array {
  const recipU = utf8(r.recipientUhid);
  const size = 1 + 16 + 2 + recipU.length + 4 + 4 + 8;
  const buf = new Uint8Array(size);
  const dv = new DataView(buf.buffer);
  let o = 0;
  buf[o++] = DTN_ENVELOPE_VERSION;
  buf.set(uuidToBytes(r.bundleId), o); o += 16;
  o = writeStr(buf, dv, o, recipU);
  dv.setInt32(o, r.totalHops, true); o += 4;
  dv.setInt32(o, r.totalCustodyTransfers, true); o += 4;
  dv.setBigInt64(o, BigInt(r.deliveredAt.getTime()), true);
  return buf;
}

export function deserializeDeliveryReceipt(data: Uint8Array): DtnDeliveryReceipt {
  const r = new Reader(data);
  r.expectVersion();
  const bundleId = r.uuid();
  const recipientUhid = r.str();
  const totalHops = r.i32();
  const totalCustodyTransfers = r.i32();
  const deliveredAtMs = r.i64();
  return {
    bundleId,
    recipientUhid,
    totalHops,
    totalCustodyTransfers,
    deliveredAt: new Date(deliveredAtMs),
  };
}

// ── Low-level helpers ────────────────────────────────────────────────────────

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

function writeStr(buf: Uint8Array, dv: DataView, o: number, bytes: Uint8Array): number {
  if (bytes.length > 65535) throw new Error("DTN string too long");
  dv.setUint16(o, bytes.length, true); o += 2;
  buf.set(bytes, o); o += bytes.length;
  return o;
}

function uuidToBytes(uuidStr: string): Uint8Array {
  const hex = uuidStr.replace(/-/g, "");
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  return bytes;
}

function bytesToUuid(bytes: Uint8Array): string {
  const hex = Array.from(bytes).map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

class Reader {
  private readonly dv: DataView;
  private o = 0;
  constructor(private readonly data: Uint8Array) {
    this.dv = new DataView(data.buffer, data.byteOffset, data.length);
  }
  expectVersion(): void {
    const v = this.u8();
    if (v !== DTN_ENVELOPE_VERSION) throw new Error(`DTN: unsupported envelope version 0x${v.toString(16)}`);
  }
  u8(): number {
    return this.data[this.o++];
  }
  uuid(): string {
    const s = this.data.slice(this.o, this.o + 16);
    this.o += 16;
    return bytesToUuid(s);
  }
  i32(): number {
    const v = this.dv.getInt32(this.o, true);
    this.o += 4;
    return v;
  }
  i64(): number {
    const v = this.dv.getBigInt64(this.o, true);
    this.o += 8;
    return Number(v);
  }
  u16(): number {
    const v = this.dv.getUint16(this.o, true);
    this.o += 2;
    return v;
  }
  str(): string {
    const n = this.u16();
    const s = new TextDecoder().decode(this.data.slice(this.o, this.o + n));
    this.o += n;
    return s;
  }
  bytes32(): Uint8Array {
    const n = this.i32();
    if (n < 0 || n > MAX_ENVELOPE_PAYLOAD) throw new Error(`DTN: invalid payload length ${n}`);
    const b = this.data.slice(this.o, this.o + n);
    this.o += n;
    return b;
  }
}
