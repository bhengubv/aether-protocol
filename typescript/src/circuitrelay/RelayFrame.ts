/**
 * Binary circuit-relay-v2 wire-frame serialization — the cross-language wire
 * format for native (no-libp2p) any-node relaying, carried in MeshPacket.payload
 * the same way the DTN envelope is. Conventions mirror DtnEnvelope /
 * PacketSerializer exactly so the eight language SDKs stay byte-identical (and
 * are pinned by the fixtures/circuit-relay corpus): every frame begins with a
 * single format-version byte (readers reject any other value); all multi-byte
 * integers are little-endian; the 16-byte connectionId is the UUID in RFC-4122
 * big-endian order; strings are uint16-LE length-prefixed UTF-8; the payload is
 * int32-LE length-prefixed raw bytes and is always the last field.
 *
 * Layout (fixed, every field always present):
 *   version u8 | type u8 | status u8
 *   srcUhid u16+utf8 | dstUhid u16+utf8 | relayUhid u16+utf8
 *   connId 16B(BE) | reservationExpiresAtMs i64 | limitDurationSeconds i32 | limitDataBytes i64
 *   payload i32+bytes
 * Minimum size (all strings empty, no payload): 49 bytes.
 *
 * Byte-identical to the C# reference (AetherNet.CircuitRelay.RelayFrameSerializer)
 * and the Go oracle (go/circuitrelay).
 *
 * SPDX-License-Identifier: MIT
 */

/** Circuit-relay-v2 verb (message type). */
export enum RelayMessageType {
  Reserve = 1,
  ReserveResponse = 2,
  Connect = 3,
  Stop = 4,
  StopResponse = 5,
  ConnectResponse = 6,
  Data = 7,
}

/** Relay response result code. */
export enum RelayStatus {
  Ok = 0,
  ReservationRefused = 1,
  NoReservation = 2,
  ResourceLimitExceeded = 3,
  PermissionDenied = 4,
  ConnectionFailed = 5,
  MalformedMessage = 6,
}

/**
 * A single circuit-relay-v2 wire frame. One fixed layout carries every verb,
 * type-discriminated. `reservationExpiresAtMs` and `limitDataBytes` are i64 on
 * the wire but carried as JS numbers (matching how DtnEnvelope handles
 * createdAtMs); values stay well within Number.MAX_SAFE_INTEGER.
 */
export interface RelayFrame {
  type: RelayMessageType;
  status: RelayStatus;
  sourceUhid: string;
  destinationUhid: string;
  relayUhid: string;
  /** UUID string; "" (or omitted) is treated as the nil UUID (16 zero bytes). */
  connectionId: string;
  reservationExpiresAtMs: number;
  limitDurationSeconds: number;
  limitDataBytes: number;
  payload: Uint8Array;
}

export const RELAY_FRAME_VERSION = 0x01;
const MAX_RELAY_PAYLOAD = 16 * 1024 * 1024;
const NIL_UUID = "00000000-0000-0000-0000-000000000000";

export function serializeRelayFrame(f: RelayFrame): Uint8Array {
  const srcU = utf8(f.sourceUhid ?? "");
  const dstU = utf8(f.destinationUhid ?? "");
  const relayU = utf8(f.relayUhid ?? "");
  const payload = f.payload ?? new Uint8Array(0);

  const size =
    1 + 1 + 1 +
    2 + srcU.length + 2 + dstU.length + 2 + relayU.length +
    16 + 8 + 4 + 8 +
    4 + payload.length;

  const buf = new Uint8Array(size);
  const dv = new DataView(buf.buffer);
  let o = 0;
  buf[o++] = RELAY_FRAME_VERSION;
  buf[o++] = f.type & 0xff;
  buf[o++] = (f.status ?? RelayStatus.Ok) & 0xff;
  o = writeStr(buf, dv, o, srcU);
  o = writeStr(buf, dv, o, dstU);
  o = writeStr(buf, dv, o, relayU);
  buf.set(uuidToBytes(f.connectionId && f.connectionId.length > 0 ? f.connectionId : NIL_UUID), o); o += 16;
  dv.setBigInt64(o, BigInt(f.reservationExpiresAtMs ?? 0), true); o += 8;
  dv.setInt32(o, f.limitDurationSeconds ?? 0, true); o += 4;
  dv.setBigInt64(o, BigInt(f.limitDataBytes ?? 0), true); o += 8;
  if (payload.length > MAX_RELAY_PAYLOAD) throw new Error("Relay payload too large");
  dv.setInt32(o, payload.length, true); o += 4;
  buf.set(payload, o);
  return buf;
}

export function deserializeRelayFrame(data: Uint8Array): RelayFrame {
  const r = new Reader(data);
  r.expectVersion();
  const type = r.u8();
  if (type === 0 || type > RelayMessageType.Data) throw new Error(`Relay: invalid message type ${type}`);
  const status = r.u8();
  if (status > RelayStatus.MalformedMessage) throw new Error(`Relay: invalid status ${status}`);
  const sourceUhid = r.str();
  const destinationUhid = r.str();
  const relayUhid = r.str();
  const connectionId = r.uuid();
  const reservationExpiresAtMs = r.i64();
  const limitDurationSeconds = r.i32();
  const limitDataBytes = r.i64();
  const payload = r.bytes32();
  return {
    type: type as RelayMessageType,
    status: status as RelayStatus,
    sourceUhid,
    destinationUhid,
    relayUhid,
    connectionId,
    reservationExpiresAtMs,
    limitDurationSeconds,
    limitDataBytes,
    payload,
  };
}

// ── Low-level helpers (identical idiom to DtnEnvelope) ───────────────────────

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

function writeStr(buf: Uint8Array, dv: DataView, o: number, bytes: Uint8Array): number {
  if (bytes.length > 65535) throw new Error("Relay string too long");
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
    if (v !== RELAY_FRAME_VERSION) throw new Error(`Relay: unsupported frame version 0x${v.toString(16)}`);
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
    if (n < 0 || n > MAX_RELAY_PAYLOAD) throw new Error(`Relay: invalid payload length ${n}`);
    const b = this.data.slice(this.o, this.o + n);
    this.o += n;
    return b;
  }
}
