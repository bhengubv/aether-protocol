/**
 * Proof-of-Vicinity token model and canonical signable-body codec. TypeScript
 * port of AetherNet.Market.Models.PoVToken / PoVTransportType / PoVScore and
 * AetherNet.Market.PoVTokenCodec, byte-identical to the C# reference and every
 * other language implementation (Go, etc.).
 *
 * The canonical body that BOTH the witness and the subject sign with their real
 * Ed25519 identity keys must stay byte-identical across every language
 * implementation so a token signed by one node verifies on any other:
 *
 *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
 *
 * timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).
 *
 * SPDX-License-Identifier: MIT
 */

const UTF8 = new TextEncoder();

/**
 * Transport used for a co-presence Proof-of-Vicinity exchange. Only short-range
 * transports are valid (prevents remote minting). Numeric values are the wire
 * bytes: ble=0, nfc=1, nearlink=2.
 */
export enum PoVTransportType {
  /** Bluetooth Low Energy (short range — prevents remote forgery). */
  Ble = 0,
  /** Near-Field Communication (requires physical proximity). */
  Nfc = 1,
  /** Huawei NearLink (short range, similar to BLE). */
  NearLink = 2,
}

/** Whether `transport` is a valid short-range PoV channel. */
export function isShortRange(transport: PoVTransportType): boolean {
  switch (transport) {
    case PoVTransportType.Ble:
    case PoVTransportType.Nfc:
    case PoVTransportType.NearLink:
      return true;
    default:
      return false;
  }
}

/** Lowercase wire name of the transport. */
export function transportToString(transport: PoVTransportType): string {
  switch (transport) {
    case PoVTransportType.Ble:
      return "ble";
    case PoVTransportType.Nfc:
      return "nfc";
    case PoVTransportType.NearLink:
      return "nearlink";
    default:
      return "unknown";
  }
}

/** Number of .NET DateTime ticks (100ns) per second. */
export const TICKS_PER_SECOND = 10_000_000n;

/**
 * The .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z), i.e.
 * ticks between 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and
 * a JS millisecond timestamp.
 */
export const UNIX_EPOCH_TICKS = 621_355_968_000_000_000n;

/**
 * The JSON wire form (snake_case) of a Proof-of-Vicinity token.
 *
 * `timestamp_ticks` is emitted as a bare JSON integer (matching the Go port's
 * i64 intent), but it is handled losslessly via BigInt — NOT through a JS
 * `number`. .NET DateTime.Ticks reaches ~3.15e18, far beyond
 * Number.MAX_SAFE_INTEGER (~9.0e15), so coercing it through a double would
 * corrupt the value and break canonical-body parity. `toJSON`/`parse` therefore
 * splice the ticks literal in/out as a BigInt rather than letting
 * JSON.stringify/parse touch it.
 */
interface PoVTokenWire {
  witness_uhid: string;
  subject_uhid: string;
  /** Transport byte: ble=0, nfc=1, nearlink=2. */
  transport_used: number;
  /** Base64-encoded witness Ed25519 signature, omitted when absent. */
  witness_signature?: string;
  /** Base64-encoded subject Ed25519 countersignature, omitted when absent. */
  subject_signature?: string;
}

/**
 * Placeholder spliced into the serialized JSON in place of the real ticks value
 * so JSON.stringify never sees the BigInt; replaced with the exact integer
 * literal afterward. Chosen to be a value JSON.stringify renders verbatim.
 */
const TICKS_PLACEHOLDER = 0;

/**
 * A Proof-of-Vicinity token issued by one node (the witness) to another (the
 * subject) during a physical co-presence event. Both parties must countersign —
 * this prevents unilateral forgery. The token is transmitted over a short-range
 * transport (BLE/NFC/NearLink only) to prevent remote minting.
 */
export class PoVToken {
  /** UHID of the node issuing the voucher. */
  witnessUhid: string;
  /** UHID of the node being vouched for. */
  subjectUhid: string;
  /**
   * Co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01).
   * Stored as ticks (i64 via BigInt — not a JS Date) so the signed canonical
   * body is byte-identical to C#.
   */
  timestampTicks: bigint;
  /** Transport channel used (must be short-range). */
  transportUsed: PoVTransportType;
  /** Ed25519 signature by the witness over the canonical body, or null. */
  witnessSignature: Uint8Array | null;
  /**
   * Ed25519 countersignature by the subject — required for token validity, or
   * null until countersigned.
   */
  subjectSignature: Uint8Array | null;

  constructor(init: {
    witnessUhid: string;
    subjectUhid: string;
    timestampTicks: bigint;
    transportUsed: PoVTransportType;
    witnessSignature?: Uint8Array | null;
    subjectSignature?: Uint8Array | null;
  }) {
    this.witnessUhid = init.witnessUhid;
    this.subjectUhid = init.subjectUhid;
    this.timestampTicks = init.timestampTicks;
    this.transportUsed = init.transportUsed;
    this.witnessSignature = init.witnessSignature ?? null;
    this.subjectSignature = init.subjectSignature ?? null;
  }

  /** Canonical signable bytes for this token. */
  signableData(): Uint8Array {
    return buildSignableTokenData(
      this.subjectUhid,
      this.timestampTicks,
      this.transportUsed,
    );
  }

  /**
   * Serialises the token to its snake_case UTF-8 JSON wire form. `timestamp_ticks`
   * is emitted as a bare integer (i64), spliced in losslessly so a value beyond
   * Number.MAX_SAFE_INTEGER survives intact.
   */
  toJSON(): string {
    // Stable key order matching the wire shape, with a placeholder for ticks.
    const wire: PoVTokenWire & { timestamp_ticks: number } = {
      witness_uhid: this.witnessUhid,
      subject_uhid: this.subjectUhid,
      timestamp_ticks: TICKS_PLACEHOLDER,
      transport_used: this.transportUsed,
    };
    if (this.witnessSignature) {
      wire.witness_signature = Buffer.from(this.witnessSignature).toString(
        "base64",
      );
    }
    if (this.subjectSignature) {
      wire.subject_signature = Buffer.from(this.subjectSignature).toString(
        "base64",
      );
    }
    const json = JSON.stringify(wire);
    // Replace the placeholder with the exact ticks integer literal (no double).
    return json.replace(
      /"timestamp_ticks":0/,
      `"timestamp_ticks":${this.timestampTicks.toString()}`,
    );
  }

  /**
   * Deserialises a snake_case UTF-8 JSON PoV token. `timestamp_ticks` is read as
   * a lossless BigInt straight from the JSON text (NOT via JSON.parse's double
   * coercion), so a value beyond Number.MAX_SAFE_INTEGER is preserved exactly.
   */
  static parse(data: string | Uint8Array): PoVToken {
    const text =
      typeof data === "string" ? data : new TextDecoder().decode(data);

    // Extract the raw ticks integer literal before JSON.parse can round it.
    const ticks = parseTicksLiteral(text);

    const wire = JSON.parse(text) as PoVTokenWire;
    const token = new PoVToken({
      witnessUhid: wire.witness_uhid ?? "",
      subjectUhid: wire.subject_uhid ?? "",
      timestampTicks: ticks,
      transportUsed: (wire.transport_used ?? 0) as PoVTransportType,
    });
    if (wire.witness_signature) {
      token.witnessSignature = new Uint8Array(
        Buffer.from(wire.witness_signature, "base64"),
      );
    }
    if (wire.subject_signature) {
      token.subjectSignature = new Uint8Array(
        Buffer.from(wire.subject_signature, "base64"),
      );
    }
    return token;
  }
}

/**
 * Reads the `timestamp_ticks` integer literal directly out of a JSON token body
 * as a BigInt, avoiding JSON.parse's lossy double coercion for values beyond
 * Number.MAX_SAFE_INTEGER. Returns 0n when the field is absent.
 */
export function parseTicksLiteral(json: string): bigint {
  const m = /"timestamp_ticks"\s*:\s*(-?\d+)/.exec(json);
  return m ? BigInt(m[1]) : 0n;
}

/**
 * Builds the canonical signable bytes for a PoV token body. The same layout is
 * signed by the witness (on issue) and counter-signed by the subject (on
 * accept).
 *
 *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
 */
export function buildSignableTokenData(
  subjectUhid: string,
  timestampTicks: bigint,
  transport: PoVTransportType,
): Uint8Array {
  const subjectBytes = UTF8.encode(subjectUhid);
  const data = new Uint8Array(4 + subjectBytes.length + 8 + 1);
  const view = new DataView(data.buffer);
  let offset = 0;

  view.setInt32(offset, subjectBytes.length, true);
  offset += 4;

  data.set(subjectBytes, offset);
  offset += subjectBytes.length;

  view.setBigInt64(offset, BigInt.asIntN(64, timestampTicks), true);
  offset += 8;

  data[offset] = transport & 0xff;

  return data;
}

/**
 * Converts a .NET DateTime.Ticks value to a JS millisecond Unix timestamp
 * (truncated to ms; ticks have 100ns resolution). Provided for hosts that want a
 * JS timestamp; the canonical body always uses the raw ticks.
 */
export function ticksToUnixMs(ticks: bigint): bigint {
  const unixTicks = ticks - UNIX_EPOCH_TICKS;
  return unixTicks / 10_000n; // 10,000 ticks per millisecond
}

/** Converts a JS millisecond Unix timestamp to a .NET DateTime.Ticks value. */
export function unixMsToTicks(unixMs: bigint): bigint {
  return unixMs * 10_000n + UNIX_EPOCH_TICKS;
}

/**
 * The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil
 * routing/identity signal that attaches NO value semantics.
 */
export interface PoVScore {
  /** UHID of the scored node. */
  uhid: string;
  /** Number of distinct witnesses who have issued PoV tokens to this node. */
  uniqueWitnesses: number;
  /** Weighted score (0.0–1.0). */
  weightedScore: number;
  /** Time of the most recent score update. */
  lastUpdated: Date;
}
