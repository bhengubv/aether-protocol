/**
 * Generic "value-earned" relay-tip envelope carried inside a
 * PacketType.TipPacket (24). TypeScript port of AetherNet.Incentive.TipPacketPayload,
 * byte-identical to the C# reference and every other language implementation
 * (Go, etc.).
 *
 * This model is deliberately value-agnostic. `amount` is a bare number with NO
 * units, NO policy, and NO settlement semantics attached at the protocol layer.
 * The protocol carries the signal that one node wishes to credit another for
 * some kind of relayed traffic; what (if anything) that signal is worth is
 * entirely the host's business. A bare node accepts and relays the packet but
 * settles nothing — only a host that has wired a MeshTipSettlementProvider
 * override decides how to interpret the value.
 *
 * The payload is self-signed by the tipper: `signature` is an Ed25519 signature
 * over the canonical byte layout produced by `buildCanonicalData`. The signature
 * binds the tipper, recipient, amount, traffic type, reference, and timestamp
 * together so an intermediate relay cannot tamper with any field without
 * invalidating it.
 *
 * SPDX-License-Identifier: MIT
 */

const UTF8 = new TextEncoder();

/**
 * The JSON body (snake_case) carried inside a TipPacket(24).
 *
 * `amount` is the INVARIANT decimal string (the .NET
 * decimal.ToString(InvariantCulture) round-trip form, e.g. "12.50", "0.0001",
 * "123456.789") — NOT a JS `number`. Keeping it a string is what makes the
 * signed bytes stable across locales and decimal scales without baking in any
 * unit or fixed-point assumption, and is required for byte-identity with the C#
 * canonical data. A JS `number` cannot represent these values losslessly (it
 * has no fixed decimal scale), so the protocol layer must never coerce it.
 */
export interface TipPacketPayloadInit {
  /** UHID of the node offering the tip (the signer of this payload). */
  tipperUhid: string;
  /** UHID of the node the tip is addressed to. */
  recipientUhid: string;
  /**
   * Generic value being credited, as the invariant decimal string. The protocol
   * imposes NO unit, NO minimum, NO maximum, and NO policy.
   */
  amount: string;
  /**
   * Free-form tag describing the kind of relayed traffic this tip is for,
   * e.g. "message-relay" or "gateway-share". Opaque to the protocol.
   */
  trafficType: string;
  /**
   * Optional correlation id linking this tip to some host-defined unit of work.
   * `null`/`undefined` when the tip stands alone (serialised as 16 zero bytes in
   * the canonical data). When present it is a hyphenated GUID string, e.g.
   * "11112222-3333-4444-5555-666677778888".
   */
  referenceId?: string | null;
  /** When the tipper created this payload, in Unix milliseconds (i64). */
  timestampUnixMs: bigint;
}

/** snake_case wire shape (UTF-8 JSON) matching the C# serializer. */
interface TipPacketPayloadWire {
  tipper_uhid: string;
  recipient_uhid: string;
  amount: string;
  traffic_type: string;
  reference_id?: string | null;
  /** Unix milliseconds. Serialised as a JSON number (matches C#/Go `timestamp`). */
  timestamp: number;
  /** Base64-encoded Ed25519 signature, omitted until signed. */
  signature?: string;
}

const ED25519_SIGNATURE_LENGTH = 64;

export class TipPacketPayload {
  tipperUhid: string;
  recipientUhid: string;
  /** Invariant decimal string — NEVER a JS number. */
  amount: string;
  trafficType: string;
  /** Hyphenated GUID string, or null when the tip stands alone. */
  referenceId: string | null;
  /** Unix milliseconds, i64. */
  timestampUnixMs: bigint;
  /** 64-byte Ed25519 signature over `buildCanonicalData()`, or null until signed. */
  signature: Uint8Array | null;

  constructor(init: TipPacketPayloadInit) {
    this.tipperUhid = init.tipperUhid;
    this.recipientUhid = init.recipientUhid;
    this.amount = init.amount;
    this.trafficType = init.trafficType;
    this.referenceId = init.referenceId ?? null;
    this.timestampUnixMs = init.timestampUnixMs;
    this.signature = null;
  }

  /**
   * Builds the canonical byte array that is signed/verified for this payload.
   * The `signature` field itself is excluded from the canonical data.
   *
   * Layout (little-endian lengths, matching PacketSigningService.BuildSignableData
   * conventions):
   *
   *   TipperLen(4 LE i32)    || Tipper(UTF-8)
   *   RecipientLen(4 LE i32) || Recipient(UTF-8)
   *   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
   *   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
   *   ReferenceId(16, all-zero GUID when null, .NET mixed-endian byte order)
   *   TimestampUnixMs(8 LE i64)
   */
  buildCanonicalData(): Uint8Array {
    const tipperBytes = UTF8.encode(this.tipperUhid);
    const recipientBytes = UTF8.encode(this.recipientUhid);
    const amountBytes = UTF8.encode(this.amount);
    const trafficBytes = UTF8.encode(this.trafficType);

    const totalLength =
      4 + tipperBytes.length +
      4 + recipientBytes.length +
      4 + amountBytes.length +
      4 + trafficBytes.length +
      16 + // ReferenceId GUID
      8; // Timestamp (i64 LE)

    const buffer = new Uint8Array(totalLength);
    const view = new DataView(buffer.buffer);
    let offset = 0;

    offset += writeLengthPrefixed(buffer, view, offset, tipperBytes);
    offset += writeLengthPrefixed(buffer, view, offset, recipientBytes);
    offset += writeLengthPrefixed(buffer, view, offset, amountBytes);
    offset += writeLengthPrefixed(buffer, view, offset, trafficBytes);

    // ReferenceId — 16 bytes, all-zero when null, .NET GUID byte order otherwise.
    if (this.referenceId) {
      buffer.set(guidBytesDotNet(this.referenceId), offset);
    }
    offset += 16;

    // Timestamp — Unix milliseconds, little-endian int64 (via BigInt).
    view.setBigInt64(offset, BigInt.asIntN(64, this.timestampUnixMs), true);

    return buffer;
  }

  /** Serialises the payload to its snake_case UTF-8 JSON wire form. */
  toJSON(): string {
    const wire: TipPacketPayloadWire = {
      tipper_uhid: this.tipperUhid,
      recipient_uhid: this.recipientUhid,
      amount: this.amount,
      traffic_type: this.trafficType,
      timestamp: Number(this.timestampUnixMs),
    };
    if (this.referenceId) {
      wire.reference_id = this.referenceId;
    }
    if (this.signature) {
      wire.signature = Buffer.from(this.signature).toString("base64");
    }
    return JSON.stringify(wire);
  }

  /** Deserialises a snake_case UTF-8 JSON tip payload. */
  static parse(data: string | Uint8Array): TipPacketPayload {
    const text =
      typeof data === "string" ? data : new TextDecoder().decode(data);
    const wire = JSON.parse(text) as TipPacketPayloadWire;
    const payload = new TipPacketPayload({
      tipperUhid: wire.tipper_uhid ?? "",
      recipientUhid: wire.recipient_uhid ?? "",
      amount: wire.amount ?? "",
      trafficType: wire.traffic_type ?? "",
      referenceId: wire.reference_id ?? null,
      timestampUnixMs: BigInt(wire.timestamp ?? 0),
    });
    if (wire.signature) {
      payload.signature = new Uint8Array(Buffer.from(wire.signature, "base64"));
    }
    return payload;
  }

  /** True when a well-formed (64-byte) Ed25519 signature is present. */
  hasWellFormedSignature(): boolean {
    return (
      this.signature !== null &&
      this.signature.length === ED25519_SIGNATURE_LENGTH
    );
  }
}

/**
 * Writes a 4-byte LE int32 length prefix followed by `value`, returning the
 * total bytes written.
 */
function writeLengthPrefixed(
  buffer: Uint8Array,
  view: DataView,
  offset: number,
  value: Uint8Array,
): number {
  view.setInt32(offset, value.length, true);
  buffer.set(value, offset + 4);
  return 4 + value.length;
}

/**
 * Returns the 16-byte .NET in-memory representation of a hyphenated GUID string,
 * which is what System.Guid.TryWriteBytes produces. The canonical RFC-4122
 * string is big-endian; .NET stores the first three groups little-endian (Data1:
 * 4 bytes, Data2: 2 bytes, Data3: 2 bytes) and the final 8 bytes (Data4) as-is.
 * This mixed-endian layout is required for byte-identity with the C# canonical
 * data.
 *
 * Example: "11112222-3333-4444-5555-666677778888" →
 *          22 22 11 11 33 33 44 44 55 55 66 66 77 77 88 88
 */
export function guidBytesDotNet(guid: string): Uint8Array {
  const hex = guid.replace(/-/g, "");
  if (hex.length !== 32 || /[^0-9a-fA-F]/.test(hex)) {
    throw new Error(`invalid GUID string: ${guid}`);
  }
  // RFC-4122 big-endian bytes (u[0..15]).
  const u = new Uint8Array(16);
  for (let i = 0; i < 16; i++) {
    u[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
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
