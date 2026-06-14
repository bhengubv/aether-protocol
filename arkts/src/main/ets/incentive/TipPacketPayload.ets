/*
 * Generic "value-earned" relay-tip envelope carried inside a
 * PacketType.TipPacket (24). ArkTS (HarmonyOS) port of
 * AetherNet.Incentive.TipPacketPayload, byte-identical to the C# reference and
 * every other language implementation (TypeScript, Go, …).
 *
 * The model is deliberately value-agnostic. `amount` is a bare invariant decimal
 * STRING with NO units, NO policy, and NO settlement semantics at the protocol
 * layer. Keeping it a string (the .NET decimal.ToString(InvariantCulture)
 * round-trip form, e.g. "12.50", "0.0001", "123456.789") is what makes the signed
 * bytes stable across locales and decimal scales — a floating-point `number`
 * cannot represent these losslessly, so the protocol layer must never coerce it.
 *
 * The payload is self-signed by the tipper: `signature` is an Ed25519 signature
 * over the canonical byte layout produced by `buildCanonicalData()`. The signature
 * binds tipper, recipient, amount, traffic type, reference, and timestamp together
 * so an intermediate relay cannot tamper with any field without invalidating it.
 *
 * SPDX-License-Identifier: MIT
 */

import { utf8Encode, utf8Decode, toBase64, fromBase64 } from '../protocol/Bytes';

const ED25519_SIGNATURE_LENGTH: number = 64;

/** Construction parameters for a TipPacketPayload (explicit, ArkTS-clean). */
export class TipPacketPayloadInit {
  /** UHID of the node offering the tip (the signer of this payload). */
  tipperUhid: string = '';
  /** UHID of the node the tip is addressed to. */
  recipientUhid: string = '';
  /** Generic value being credited, as the invariant decimal string. */
  amount: string = '';
  /** Free-form tag describing the relayed traffic, e.g. "message-relay". */
  trafficType: string = '';
  /** Optional GUID correlating this tip to a host unit of work; null = stands alone. */
  referenceId: string | null = null;
  /** When the tipper created this payload, in Unix milliseconds (i64). */
  timestampUnixMs: bigint = 0n;
}

/**
 * The JSON body (snake_case UTF-8) carried inside a TipPacket(24). `amount` is the
 * INVARIANT decimal string — NEVER a number. `timestamp` is Unix milliseconds; it
 * stays within the IEEE-754 safe-integer range for realistic values and so is
 * emitted as a bare JSON number, matching the C# / Go / TypeScript serializer.
 */
export class TipPacketPayload {
  tipperUhid: string;
  recipientUhid: string;
  /** Invariant decimal string — NEVER a number. */
  amount: string;
  trafficType: string;
  /** Hyphenated GUID string, or null when the tip stands alone. */
  referenceId: string | null;
  /** Unix milliseconds, i64. */
  timestampUnixMs: bigint;
  /** 64-byte Ed25519 signature over buildCanonicalData(), or null until signed. */
  signature: Uint8Array | null;

  constructor(init: TipPacketPayloadInit) {
    this.tipperUhid = init.tipperUhid;
    this.recipientUhid = init.recipientUhid;
    this.amount = init.amount;
    this.trafficType = init.trafficType;
    this.referenceId = init.referenceId;
    this.timestampUnixMs = init.timestampUnixMs;
    this.signature = null;
  }

  /**
   * Builds the canonical byte array that is signed/verified for this payload. The
   * `signature` field itself is excluded.
   *
   * Layout (little-endian lengths):
   *   TipperLen(4 LE i32)    || Tipper(UTF-8)
   *   RecipientLen(4 LE i32) || Recipient(UTF-8)
   *   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
   *   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
   *   ReferenceId(16, all-zero GUID when null, .NET mixed-endian byte order)
   *   TimestampUnixMs(8 LE i64)
   */
  buildCanonicalData(): Uint8Array {
    const tipperBytes: Uint8Array = utf8Encode(this.tipperUhid);
    const recipientBytes: Uint8Array = utf8Encode(this.recipientUhid);
    const amountBytes: Uint8Array = utf8Encode(this.amount);
    const trafficBytes: Uint8Array = utf8Encode(this.trafficType);

    const totalLength: number =
      4 + tipperBytes.length +
      4 + recipientBytes.length +
      4 + amountBytes.length +
      4 + trafficBytes.length +
      16 + // ReferenceId GUID
      8; // Timestamp (i64 LE)

    const buffer = new Uint8Array(totalLength);
    const view = new DataView(buffer.buffer);
    let offset: number = 0;

    offset += writeLengthPrefixed(buffer, view, offset, tipperBytes);
    offset += writeLengthPrefixed(buffer, view, offset, recipientBytes);
    offset += writeLengthPrefixed(buffer, view, offset, amountBytes);
    offset += writeLengthPrefixed(buffer, view, offset, trafficBytes);

    // ReferenceId — 16 bytes, all-zero when null, .NET GUID byte order otherwise.
    if (this.referenceId !== null && this.referenceId.length > 0) {
      buffer.set(guidBytesDotNet(this.referenceId), offset);
    }
    offset += 16;

    // Timestamp — Unix milliseconds, little-endian int64 (via bigint).
    view.setBigInt64(offset, BigInt.asIntN(64, this.timestampUnixMs), true);

    return buffer;
  }

  /** True when a well-formed (64-byte) Ed25519 signature is present. */
  hasWellFormedSignature(): boolean {
    return this.signature !== null && this.signature.length === ED25519_SIGNATURE_LENGTH;
  }

  /**
   * Serialises the payload to its snake_case UTF-8 JSON wire form. Keys are emitted
   * in a fixed order matching the C# serializer; `reference_id` and `signature` are
   * omitted when absent.
   */
  toJsonString(): string {
    let json: string = '{';
    json += `"tipper_uhid":${jsonString(this.tipperUhid)},`;
    json += `"recipient_uhid":${jsonString(this.recipientUhid)},`;
    json += `"amount":${jsonString(this.amount)},`;
    json += `"traffic_type":${jsonString(this.trafficType)},`;
    json += `"timestamp":${this.timestampUnixMs.toString()}`;
    if (this.referenceId !== null && this.referenceId.length > 0) {
      json += `,"reference_id":${jsonString(this.referenceId)}`;
    }
    if (this.signature !== null) {
      json += `,"signature":${jsonString(toBase64(this.signature))}`;
    }
    json += '}';
    return json;
  }

  /** Serialises to UTF-8 JSON bytes (the wire body). */
  toJsonBytes(): Uint8Array {
    return utf8Encode(this.toJsonString());
  }

  /** Deserialises a snake_case JSON tip payload from a string. */
  static parse(text: string): TipPacketPayload {
    const obj: Record<string, ESObject> = JSON.parse(text) as Record<string, ESObject>;

    const init = new TipPacketPayloadInit();
    init.tipperUhid = readString(obj, 'tipper_uhid');
    init.recipientUhid = readString(obj, 'recipient_uhid');
    init.amount = readString(obj, 'amount');
    init.trafficType = readString(obj, 'traffic_type');
    const ref: string = readString(obj, 'reference_id');
    init.referenceId = ref.length > 0 ? ref : null;
    // timestamp is Unix-ms (safe-integer range); BigInt() accepts the number.
    init.timestampUnixMs = BigInt(Math.trunc(readNumber(obj, 'timestamp')));

    const payload = new TipPacketPayload(init);
    const sig: string = readString(obj, 'signature');
    if (sig.length > 0) {
      payload.signature = fromBase64(sig);
    }
    return payload;
  }

  /** Deserialises from UTF-8 JSON bytes. */
  static parseBytes(bytes: Uint8Array): TipPacketPayload {
    return TipPacketPayload.parse(utf8Decode(bytes));
  }
}

// ── helpers ────────────────────────────────────────────────────────────────────

/** Writes a 4-byte LE int32 length prefix then `value`; returns bytes written. */
function writeLengthPrefixed(
  buffer: Uint8Array,
  view: DataView,
  offset: number,
  value: Uint8Array
): number {
  view.setInt32(offset, value.length, true);
  buffer.set(value, offset + 4);
  return 4 + value.length;
}

/**
 * Returns the 16-byte .NET in-memory representation of a hyphenated GUID string,
 * matching System.Guid.TryWriteBytes. The RFC-4122 string is big-endian; .NET
 * stores the first three groups little-endian (Data1: 4 bytes, Data2: 2 bytes,
 * Data3: 2 bytes) and the final 8 bytes (Data4) as-is. This mixed-endian layout is
 * required for byte-identity with the C# canonical data.
 *
 * Example: "11112222-3333-4444-5555-666677778888" →
 *          22 22 11 11 33 33 44 44 55 55 66 66 77 77 88 88
 */
export function guidBytesDotNet(guid: string): Uint8Array {
  let hex: string = '';
  for (let i = 0; i < guid.length; i++) {
    const c: string = guid.charAt(i);
    if (c !== '-') {
      hex += c;
    }
  }
  if (hex.length !== 32) {
    throw new Error(`invalid GUID string: ${guid}`);
  }

  // RFC-4122 big-endian bytes (u[0..15]).
  const u = new Uint8Array(16);
  for (let i = 0; i < 16; i++) {
    const byte: number = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    if (Number.isNaN(byte)) {
      throw new Error(`invalid GUID string: ${guid}`);
    }
    u[i] = byte;
  }

  const out = new Uint8Array(16);
  // Data1 (bytes 0..3) — reversed.
  out[0] = u[3];
  out[1] = u[2];
  out[2] = u[1];
  out[3] = u[0];
  // Data2 (bytes 4..5) — reversed.
  out[4] = u[5];
  out[5] = u[4];
  // Data3 (bytes 6..7) — reversed.
  out[6] = u[7];
  out[7] = u[6];
  // Data4 (bytes 8..15) — as-is.
  out.set(u.subarray(8, 16), 8);
  return out;
}

/** JSON-escapes and quotes a string value. */
function jsonString(value: string): string {
  return JSON.stringify(value);
}

/** Reads a string field, returning '' when absent or not a string. */
function readString(obj: Record<string, ESObject>, key: string): string {
  const v: ESObject = obj[key];
  return typeof v === 'string' ? v as string : '';
}

/** Reads a numeric field, returning 0 when absent or not a number. */
function readNumber(obj: Record<string, ESObject>, key: string): number {
  const v: ESObject = obj[key];
  return typeof v === 'number' ? v as number : 0;
}
