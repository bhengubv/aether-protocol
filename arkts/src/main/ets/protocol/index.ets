/*
 * Protocol layer — packet model, type enumeration, byte utilities, and the
 * Ed25519 identity provider.
 * SPDX-License-Identifier: MIT
 */

export { PacketType, packetTypeToString } from './PacketType';
export { MeshPacket } from './MeshPacket';
export {
  utf8Encode,
  utf8Decode,
  toHex,
  fromHex,
  toBase64,
  fromBase64,
  bytesEqual,
} from './Bytes';
export {
  NobleEd25519Signer,
  publicKeyFromSeed,
  signWithSeed,
  verifyEd25519,
  ED25519_SEED_LENGTH,
  ED25519_PUBLIC_KEY_LENGTH,
  ED25519_SIGNATURE_LENGTH,
} from './Ed25519Provider';
export type { Ed25519Signer } from './Ed25519Provider';
