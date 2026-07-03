/**
 * A signed device-membership record. A user links a new device by having their
 * long-term Ed25519 identity key sign the new device's own public key; every
 * other device verifies that signature to admit the newcomer into the "self"
 * device set — no central directory, no server. Because Ed25519 signatures are
 * deterministic, the serialized record is byte-identical across SDKs (verified
 * against fixtures/sync/vectors.json).
 *
 * Signed body: version(u8=1) · device_id(u16 len + utf8) · device_public_key(32)
 * · issued_at_ms(i64 LE). Serialized form is the signed body followed by the
 * 64-byte Ed25519 signature.
 *
 * SPDX-License-Identifier: MIT
 */

import { Ed25519Service } from "../security/Ed25519Service.js";

/** A signed device-membership record. */
export interface DeviceLink {
  /** The linked device's identifier. */
  deviceId: string;
  /** The device's own 32-byte Ed25519 public key. */
  devicePublicKey: Uint8Array;
  /** When the link was issued (Unix ms; i64). */
  issuedAtMs: bigint;
  /** 64-byte Ed25519 signature by the user's identity key over the signed body. */
  signature: Uint8Array;
}

/** Wire format version; readers reject any other value. */
export const DEVICE_LINK_VERSION = 0x01;

/**
 * The canonical signed body (everything but the signature). Signer and verifier
 * operate over exactly these bytes.
 */
export function signedBody(
  deviceId: string,
  devicePublicKey: Uint8Array,
  issuedAtMs: bigint,
): Uint8Array {
  if (devicePublicKey.length !== 32) {
    throw new Error("Device public key must be 32 bytes.");
  }
  const id = new TextEncoder().encode(deviceId);
  if (id.length > 0xffff) throw new Error("DeviceId is too long.");

  const body = new Uint8Array(1 + 2 + id.length + 32 + 8);
  const dv = new DataView(body.buffer);
  let o = 0;
  body[o++] = DEVICE_LINK_VERSION;
  dv.setUint16(o, id.length, true); o += 2;
  body.set(id, o); o += id.length;
  body.set(devicePublicKey, o); o += 32;
  dv.setBigInt64(o, BigInt.asIntN(64, issuedAtMs), true);
  return body;
}

/** Creates a device-link signed by the user's 32-byte Ed25519 identity seed. */
export function createDeviceLink(
  deviceId: string,
  devicePublicKey: Uint8Array,
  issuedAtMs: bigint,
  identitySeed: Uint8Array,
): DeviceLink {
  const body = signedBody(deviceId, devicePublicKey, issuedAtMs);
  const signature = Ed25519Service.sign(identitySeed, body);
  return { deviceId, devicePublicKey, issuedAtMs, signature };
}

/**
 * True if `link` was signed by the identity behind `identityPublicKey` — i.e.
 * this device belongs to that user.
 */
export function verifyDeviceLink(link: DeviceLink, identityPublicKey: Uint8Array): boolean {
  if (link.signature?.length !== 64) return false;
  if (link.devicePublicKey?.length !== 32) return false;
  const body = signedBody(link.deviceId, link.devicePublicKey, link.issuedAtMs);
  return Ed25519Service.verify(identityPublicKey, body, link.signature);
}

/** Serializes a link as its signed body followed by the 64-byte signature. */
export function serializeDeviceLink(link: DeviceLink): Uint8Array {
  if (link.signature?.length !== 64) throw new Error("Signature must be 64 bytes.");
  const body = signedBody(link.deviceId, link.devicePublicKey, link.issuedAtMs);
  const buf = new Uint8Array(body.length + 64);
  buf.set(body, 0);
  buf.set(link.signature, body.length);
  return buf;
}

/** Parses a serialized link, validating framing. */
export function deserializeDeviceLink(data: Uint8Array): DeviceLink {
  if (data.length < 1 + 2 + 32 + 8 + 64) throw new Error("DeviceLink is too short.");
  const dv = new DataView(data.buffer, data.byteOffset, data.length);
  let o = 0;
  if (data[o++] !== DEVICE_LINK_VERSION) throw new Error("Unsupported DeviceLink format version.");

  const idLen = dv.getUint16(o, true); o += 2;
  if (o + idLen + 32 + 8 + 64 > data.length) throw new Error("DeviceLink is truncated.");
  const deviceId = new TextDecoder().decode(data.subarray(o, o + idLen)); o += idLen;
  const devicePublicKey = data.slice(o, o + 32); o += 32;
  const issuedAtMs = dv.getBigInt64(o, true); o += 8;
  const signature = data.slice(o, o + 64);

  return { deviceId, devicePublicKey, issuedAtMs, signature };
}
