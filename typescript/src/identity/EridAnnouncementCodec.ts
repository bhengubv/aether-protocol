/**
 * EridAnnouncementCodec — frames the in-session ERID announcement: the message a node sends a peer
 * INSIDE an established Signal session to share its secret routingKey.
 *
 * Layout: magic "AERD" (4) + version (1) + epochSeconds (int32 BE) + eridLength (int32 BE) +
 * routingKeyLen (int32 BE) + routingKey. Integer fields big-endian so every port frames identically.
 *
 * Port of the C# reference (src/AetherNet.Core/Identity/EridAnnouncementCodec.cs). Verified against
 * fixtures/erid/vectors.json (announcement_encode_hex).
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH } from "./EphemeralRoutingId.js";

const MAGIC = Uint8Array.from([0x41, 0x45, 0x52, 0x44]); // "AERD"
const VERSION = 1;
const HEADER_LENGTH = 17; // magic(4) + version(1) + epochSeconds(4) + eridLength(4) + keyLen(4)

/** Frame an announcement carrying `routingKey` and the rotation parameters. */
export function encode(
  routingKey: Uint8Array,
  epochSeconds: number = DEFAULT_EPOCH_SECONDS,
  eridLength: number = DEFAULT_LENGTH,
): Uint8Array {
  if (routingKey.length === 0) throw new Error("ERID: routingKey cannot be empty");
  if (epochSeconds <= 0) throw new Error("ERID: epochSeconds must be positive");
  if (eridLength < 1 || eridLength > 51) throw new Error("ERID: eridLength must be 1..51");

  const buf = Buffer.alloc(HEADER_LENGTH + routingKey.length);
  buf.set(MAGIC, 0);
  buf.writeUInt8(VERSION, 4);
  buf.writeInt32BE(epochSeconds, 5);
  buf.writeInt32BE(eridLength, 9);
  buf.writeInt32BE(routingKey.length, 13);
  buf.set(routingKey, HEADER_LENGTH);
  return new Uint8Array(buf);
}

export interface EridAnnouncement {
  routingKey: Uint8Array;
  epochSeconds: number;
  eridLength: number;
}

/**
 * Parse an announcement. Returns null (rather than throwing) when the bytes are not a well-formed
 * ERID announcement, so a receiver can cheaply test an arbitrary decrypted payload against the magic.
 */
export function tryDecode(data: Uint8Array): EridAnnouncement | null {
  if (data.length < HEADER_LENGTH) return null;
  const buf = Buffer.from(data.buffer, data.byteOffset, data.byteLength);
  for (let i = 0; i < 4; i++) if (buf[i] !== MAGIC[i]) return null;
  if (buf[4] !== VERSION) return null;

  const epochSeconds = buf.readInt32BE(5);
  const eridLength = buf.readInt32BE(9);
  const keyLen = buf.readInt32BE(13);

  if (epochSeconds <= 0) return null;
  if (eridLength < 1 || eridLength > 51) return null;
  if (keyLen <= 0 || HEADER_LENGTH + keyLen > data.length) return null;

  return {
    routingKey: new Uint8Array(buf.subarray(HEADER_LENGTH, HEADER_LENGTH + keyLen)),
    epochSeconds,
    eridLength,
  };
}
