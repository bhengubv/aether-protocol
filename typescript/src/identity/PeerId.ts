/**
 * Derives a libp2p **PeerID** from a node's Ed25519 public key — the bridge between an
 * AetherNet identity and the global libp2p relay / DHT used by the decentralised relay layer.
 *
 * Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID is a
 * *pure, deterministic* function of that key — no lookup table, no network. A node can compute its
 * own PeerID (to announce on the libp2p DHT) and any peer's PeerID (to dial it) from the public
 * key alone.
 *
 * ## Encoding (must be byte-identical across every SDK language)
 *  1. **protobuf PublicKey** = `08 01` (field 1 Type = Ed25519) `12 20` (field 2 Data, length 32)
 *     followed by the 32-byte key — 36 bytes total.
 *  2. **identity multihash** = `00` (identity hash code) `24` (length 36) followed by the protobuf
 *     — 38 bytes. libp2p uses the identity multihash for keys whose serialized form is small.
 *  3. **PeerID string** = base58btc (Bitcoin alphabet) of the 38-byte multihash, WITHOUT a
 *     multibase prefix. Always renders as `12D3Koo…` for Ed25519.
 *
 * Verified byte-for-byte against real `js-libp2p` output; see `fixtures/peerid/`.
 *
 * No libp2p dependency — base58 is implemented here (standard bitcoinj algorithm).
 *
 * SPDX-License-Identifier: MIT
 */

// Bitcoin base58 alphabet (no 0, O, I, l).
const BASE58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

// identity-multihash(code 0x00, len 0x24=36) || protobuf PublicKey(type Ed25519: 08 01; data len 32: 12 20)
const ED25519_PEERID_PREFIX = Uint8Array.from([0x00, 0x24, 0x08, 0x01, 0x12, 0x20]);

/** Length in bytes of a raw Ed25519 public key. */
export const ED25519_PUBLIC_KEY_LENGTH = 32;

/**
 * Returns the libp2p PeerID string (e.g. `12D3Koo…`) for a 32-byte Ed25519 public key.
 *
 * @throws {Error} if `publicKey` is not exactly 32 bytes.
 */
export function fromEd25519PublicKey(publicKey: Uint8Array): string {
  if (publicKey.length !== ED25519_PUBLIC_KEY_LENGTH) {
    throw new Error(
      `Ed25519 public key must be ${ED25519_PUBLIC_KEY_LENGTH} bytes, got ${publicKey.length}.`,
    );
  }

  const multihash = new Uint8Array(ED25519_PEERID_PREFIX.length + ED25519_PUBLIC_KEY_LENGTH);
  multihash.set(ED25519_PEERID_PREFIX, 0);
  multihash.set(publicKey, ED25519_PEERID_PREFIX.length);
  return base58Encode(multihash);
}

/**
 * Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading '1's.
 * Big-endian base-256 → base-58 by repeated divmod.
 */
function base58Encode(input: Uint8Array): string {
  if (input.length === 0) return "";

  let zeros = 0;
  while (zeros < input.length && input[zeros] === 0) zeros++;

  const buffer = Uint8Array.from(input); // divmod mutates in place
  const encoded = new Array<string>(input.length * 2); // safe upper bound
  let outputStart = encoded.length;

  for (let inputStart = zeros; inputStart < buffer.length; ) {
    encoded[--outputStart] = BASE58_ALPHABET[divmod58(buffer, inputStart)];
    if (buffer[inputStart] === 0) inputStart++; // a digit fully consumed
  }
  // Drop extra leading '1's the loop may have produced.
  while (outputStart < encoded.length && encoded[outputStart] === BASE58_ALPHABET[0]) outputStart++;
  // Re-add one '1' per leading zero byte of the input.
  for (; zeros > 0; zeros--) encoded[--outputStart] = BASE58_ALPHABET[0];

  return encoded.slice(outputStart).join("");
}

/**
 * Divides the big-endian base-256 number in `number[firstDigit..]` by 58, in place,
 * returning the remainder.
 */
function divmod58(number: Uint8Array, firstDigit: number): number {
  let remainder = 0;
  for (let i = firstDigit; i < number.length; i++) {
    const temp = remainder * 256 + number[i];
    number[i] = Math.floor(temp / 58);
    remainder = temp % 58;
  }
  return remainder;
}
