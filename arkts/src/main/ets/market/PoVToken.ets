/*
 * Proof-of-Vicinity token model and canonical signable-body codec. ArkTS
 * (HarmonyOS) port of AetherNet.Market.Models.PoVToken / PoVTransportType and
 * AetherNet.Market.PoVTokenCodec, byte-identical to the C# reference and every
 * other language implementation (TypeScript, Go, …).
 *
 * The canonical body that BOTH the witness and the subject sign with their real
 * Ed25519 identity keys must stay byte-identical across every language
 * implementation so a token signed by one node verifies on any other:
 *
 *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
 *
 * timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01). Its
 * magnitude (~6.38e17) exceeds the IEEE-754 safe-integer range, so it is carried as
 * a bigint and spliced into / out of the JSON wire form as an exact integer
 * literal — never coerced through a floating-point number, which would corrupt it
 * and break canonical-body parity.
 *
 * SPDX-License-Identifier: MIT
 */

import { utf8Encode, utf8Decode, toBase64, fromBase64 } from '../protocol/Bytes';

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
      return 'ble';
    case PoVTransportType.Nfc:
      return 'nfc';
    case PoVTransportType.NearLink:
      return 'nearlink';
    default:
      return 'unknown';
  }
}

/** Number of .NET DateTime ticks (100ns) per second. */
export const TICKS_PER_SECOND: bigint = 10000000n;

/**
 * The .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z), i.e.
 * ticks between 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and a
 * millisecond timestamp.
 */
export const UNIX_EPOCH_TICKS: bigint = 621355968000000000n;

/** Construction parameters for a PoVToken (explicit, ArkTS-clean). */
export class PoVTokenInit {
  witnessUhid: string = '';
  subjectUhid: string = '';
  timestampTicks: bigint = 0n;
  transportUsed: PoVTransportType = PoVTransportType.Ble;
  witnessSignature: Uint8Array | null = null;
  subjectSignature: Uint8Array | null = null;
}

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
   * Co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01). Stored
   * as ticks (i64 via bigint) so the signed canonical body is byte-identical to C#.
   */
  timestampTicks: bigint;
  /** Transport channel used (must be short-range). */
  transportUsed: PoVTransportType;
  /** Ed25519 signature by the witness over the canonical body, or null. */
  witnessSignature: Uint8Array | null;
  /** Ed25519 countersignature by the subject — required for validity, or null. */
  subjectSignature: Uint8Array | null;

  constructor(init: PoVTokenInit) {
    this.witnessUhid = init.witnessUhid;
    this.subjectUhid = init.subjectUhid;
    this.timestampTicks = init.timestampTicks;
    this.transportUsed = init.transportUsed;
    this.witnessSignature = init.witnessSignature;
    this.subjectSignature = init.subjectSignature;
  }

  /** Canonical signable bytes for this token. */
  signableData(): Uint8Array {
    return buildSignableTokenData(this.subjectUhid, this.timestampTicks, this.transportUsed);
  }

  /**
   * Serialises the token to its snake_case UTF-8 JSON wire form. `timestamp_ticks`
   * is emitted as a bare integer (i64) via bigint.toString(), so a value beyond the
   * safe-integer range survives intact. Signatures are emitted (base64) only when
   * present.
   */
  toJsonString(): string {
    let json: string = '{';
    json += `"witness_uhid":${JSON.stringify(this.witnessUhid)},`;
    json += `"subject_uhid":${JSON.stringify(this.subjectUhid)},`;
    json += `"timestamp_ticks":${this.timestampTicks.toString()},`;
    json += `"transport_used":${this.transportUsed.toString()}`;
    if (this.witnessSignature !== null) {
      json += `,"witness_signature":${JSON.stringify(toBase64(this.witnessSignature))}`;
    }
    if (this.subjectSignature !== null) {
      json += `,"subject_signature":${JSON.stringify(toBase64(this.subjectSignature))}`;
    }
    json += '}';
    return json;
  }

  /** Serialises to UTF-8 JSON bytes (the wire body). */
  toJsonBytes(): Uint8Array {
    return utf8Encode(this.toJsonString());
  }

  /**
   * Deserialises a snake_case JSON PoV token. `timestamp_ticks` is read as a
   * lossless bigint straight from the JSON text (NOT via a floating-point coercion),
   * so a value beyond the safe-integer range is preserved exactly. The remaining
   * fields are read with a small typed JSON reader.
   */
  static parse(text: string): PoVToken {
    const ticks: bigint = parseTicksLiteral(text);

    const obj: Record<string, ESObject> = JSON.parse(text) as Record<string, ESObject>;
    const init = new PoVTokenInit();
    init.witnessUhid = readString(obj, 'witness_uhid');
    init.subjectUhid = readString(obj, 'subject_uhid');
    init.timestampTicks = ticks;
    init.transportUsed = readNumber(obj, 'transport_used') as PoVTransportType;

    const token = new PoVToken(init);
    const wSig: string = readString(obj, 'witness_signature');
    if (wSig.length > 0) {
      token.witnessSignature = fromBase64(wSig);
    }
    const sSig: string = readString(obj, 'subject_signature');
    if (sSig.length > 0) {
      token.subjectSignature = fromBase64(sSig);
    }
    return token;
  }

  /** Deserialises from UTF-8 JSON bytes. */
  static parseBytes(bytes: Uint8Array): PoVToken {
    return PoVToken.parse(utf8Decode(bytes));
  }
}

/**
 * Reads the `timestamp_ticks` integer literal directly out of a JSON token body as
 * a bigint, avoiding a lossy floating-point coercion for values beyond the
 * safe-integer range. Returns 0n when the field is absent.
 */
export function parseTicksLiteral(json: string): bigint {
  const re = new RegExp('"timestamp_ticks"\\s*:\\s*(-?\\d+)');
  const m: RegExpExecArray | null = re.exec(json);
  return m !== null ? BigInt(m[1]) : 0n;
}

/**
 * Builds the canonical signable bytes for a PoV token body. The same layout is
 * signed by the witness (on issue) and counter-signed by the subject (on accept).
 *
 *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
 */
export function buildSignableTokenData(
  subjectUhid: string,
  timestampTicks: bigint,
  transport: PoVTransportType
): Uint8Array {
  const subjectBytes: Uint8Array = utf8Encode(subjectUhid);
  const data = new Uint8Array(4 + subjectBytes.length + 8 + 1);
  const view = new DataView(data.buffer);
  let offset: number = 0;

  view.setInt32(offset, subjectBytes.length, true);
  offset += 4;

  data.set(subjectBytes, offset);
  offset += subjectBytes.length;

  view.setBigInt64(offset, BigInt.asIntN(64, timestampTicks), true);
  offset += 8;

  data[offset] = (transport as number) & 0xff;

  return data;
}

/**
 * Converts a .NET DateTime.Ticks value to a millisecond Unix timestamp (truncated
 * to ms; ticks have 100ns resolution).
 */
export function ticksToUnixMs(ticks: bigint): bigint {
  const unixTicks: bigint = ticks - UNIX_EPOCH_TICKS;
  return unixTicks / 10000n; // 10,000 ticks per millisecond
}

/** Converts a millisecond Unix timestamp to a .NET DateTime.Ticks value. */
export function unixMsToTicks(unixMs: bigint): bigint {
  return unixMs * 10000n + UNIX_EPOCH_TICKS;
}

/**
 * The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil
 * routing/identity signal that attaches NO value semantics.
 */
export class PoVScore {
  /** UHID of the scored node. */
  uhid: string = '';
  /** Number of distinct witnesses who have issued PoV tokens to this node. */
  uniqueWitnesses: number = 0;
  /** Weighted score (0.0–1.0). */
  weightedScore: number = 0;
  /** Time of the most recent score update (Unix ms). */
  lastUpdatedMs: bigint = 0n;
}

// ── typed JSON readers (ESObject is ArkTS's controlled dynamic-JSON type) ───────

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
