// SPDX-License-Identifier: MIT

/**
 * BIP-39 mnemonic codec and AetherNet identity recovery-phrase backup.
 *
 * This is the real, standard BIP-39 algorithm, verified against the official
 * Trezor test vectors (see fixtures/bip39/vectors.json) — a phrase produced here
 * restores on any conformant BIP-39 wallet, and every AetherNet language SDK
 * reproduces the same words and seed byte-for-byte.
 *
 *   entropy (16..32 bytes, multiple of 4)  --entropyToMnemonic-->  phrase
 *   phrase  --mnemonicToEntropy-->  entropy      (SHA-256 checksum enforced)
 *   phrase  --mnemonicToSeed-->  64-byte seed     (PBKDF2-HMAC-SHA512, 2048 rounds)
 *
 * An AetherNet identity is an Ed25519 key pair whose private key is a 32-byte
 * seed — exactly 256 bits, which map cleanly onto a 24-word BIP-39 phrase. The
 * user writes down 24 ordinary words; from those words alone the identity is
 * fully reconstructed on any device. No server, no account, no custodian — the
 * phrase *is* the identity.
 */

import { createHash, pbkdf2Sync } from "crypto";
import nacl from "tweetnacl";

import { BIP39_WORDLIST } from "./Bip39Wordlist.js";
import { type Ed25519KeyPair } from "./Ed25519Service.js";

const PBKDF_ITERATIONS = 2048;
const SEED_LENGTH_BYTES = 64;

// word -> index, built once from the embedded official wordlist.
const WORD_INDEX: ReadonlyMap<string, number> = (() => {
  const map = new Map<string, number>();
  for (let i = 0; i < BIP39_WORDLIST.length; i++) map.set(BIP39_WORDLIST[i], i);
  return map;
})();

/** Splits a phrase on runs of whitespace, dropping empty entries. */
function splitWords(mnemonic: string): string[] {
  return mnemonic.split(/\s+/).filter((w) => w.length > 0);
}

function sha256(data: Uint8Array): Uint8Array {
  return new Uint8Array(createHash("sha256").update(data).digest());
}

/**
 * Encodes entropy as a BIP-39 mnemonic phrase (single-space-separated words).
 *
 * @param entropy 16, 20, 24, 28, or 32 bytes (128..256 bits).
 */
export function entropyToMnemonic(entropy: Uint8Array): string {
  if (entropy.length < 16 || entropy.length > 32 || entropy.length % 4 !== 0) {
    throw new Error("Entropy must be 16, 20, 24, 28, or 32 bytes.");
  }

  const entBits = entropy.length * 8;
  const csBits = entBits / 32; // 4..8 checksum bits
  const checksum = sha256(entropy)[0]; // only the top csBits are used

  // Read the big-endian bit stream entropy||checksum in 11-bit groups.
  const wordCount = (entBits + csBits) / 11;
  const words: string[] = new Array(wordCount);

  for (let w = 0; w < wordCount; w++) {
    let index = 0;
    for (let b = 0; b < 11; b++) {
      const bitPos = w * 11 + b;
      const bit =
        bitPos < entBits
          ? (entropy[bitPos >> 3] >> (7 - (bitPos & 7))) & 1
          : (checksum >> (7 - (bitPos - entBits))) & 1;
      index = (index << 1) | bit;
    }
    words[w] = BIP39_WORDLIST[index];
  }

  return words.join(" ");
}

/**
 * Decodes a BIP-39 mnemonic back to its entropy, enforcing the SHA-256
 * checksum. Throws on an unknown word, a wrong word count, or a checksum
 * mismatch — so a mistyped phrase is rejected rather than silently yielding the
 * wrong secret.
 */
export function mnemonicToEntropy(mnemonic: string): Uint8Array {
  const words = splitWords(mnemonic);
  if (![12, 15, 18, 21, 24].includes(words.length)) {
    throw new Error(
      `Mnemonic must be 12, 15, 18, 21, or 24 words (got ${words.length}).`,
    );
  }

  const totalBits = words.length * 11;
  const csBits = Math.floor(totalBits / 33);
  const entBits = totalBits - csBits;
  const entropy = new Uint8Array(entBits / 8);
  let actualChecksum = 0;

  for (let w = 0; w < words.length; w++) {
    const index = WORD_INDEX.get(words[w]);
    if (index === undefined) {
      throw new Error(`Unknown mnemonic word: '${words[w]}'.`);
    }

    for (let b = 0; b < 11; b++) {
      const bit = (index >> (10 - b)) & 1;
      const bitPos = w * 11 + b;
      if (bitPos < entBits) {
        entropy[bitPos >> 3] |= bit << (7 - (bitPos & 7));
      } else {
        actualChecksum = (actualChecksum << 1) | bit;
      }
    }
  }

  const expectedChecksum = sha256(entropy)[0] >> (8 - csBits);
  if (actualChecksum !== expectedChecksum) {
    throw new Error("Mnemonic checksum is invalid.");
  }

  return entropy;
}

/**
 * Derives the 64-byte BIP-39 seed from a mnemonic and optional passphrase,
 * using PBKDF2-HMAC-SHA512 with 2048 iterations and salt "mnemonic"+passphrase.
 * Both inputs are NFKD-normalized per the spec.
 */
export function mnemonicToSeed(mnemonic: string, passphrase = ""): Uint8Array {
  const normalizedMnemonic = splitWords(mnemonic).join(" ").normalize("NFKD");
  const salt = ("mnemonic" + passphrase).normalize("NFKD");

  return new Uint8Array(
    pbkdf2Sync(
      Buffer.from(normalizedMnemonic, "utf8"),
      Buffer.from(salt, "utf8"),
      PBKDF_ITERATIONS,
      SEED_LENGTH_BYTES,
      "sha512",
    ),
  );
}

/**
 * Returns true if `mnemonic` is a well-formed BIP-39 phrase with a valid
 * checksum.
 */
export function isValidMnemonic(mnemonic: string): boolean {
  try {
    mnemonicToEntropy(mnemonic);
    return true;
  } catch {
    return false;
  }
}

/**
 * Produces the 24-word recovery phrase for an identity's private key.
 *
 * @param ed25519PrivateKey The 32-byte Ed25519 private seed
 *   (as returned by {@link Ed25519Service.generateKeyPair}).
 */
export function toRecoveryPhrase(ed25519PrivateKey: Uint8Array): string {
  if (ed25519PrivateKey.length !== 32) {
    throw new Error("An AetherNet identity private key must be 32 bytes.");
  }
  return entropyToMnemonic(ed25519PrivateKey);
}

/**
 * Restores a full identity key pair from a 24-word recovery phrase. The BIP-39
 * checksum is enforced, so a mistyped word is rejected rather than silently
 * reconstructing a different identity.
 *
 * @throws if the phrase is malformed, fails its checksum, or does not encode a
 *   256-bit (24-word) identity seed.
 */
export function fromRecoveryPhrase(recoveryPhrase: string): Ed25519KeyPair {
  const privateKey = mnemonicToEntropy(recoveryPhrase);
  if (privateKey.length !== 32) {
    throw new Error(
      "An AetherNet recovery phrase must be 24 words (a 256-bit identity seed).",
    );
  }

  const publicKey = deriveEd25519PublicKey(privateKey);
  return { publicKey, privateKey };
}

/**
 * Derives the 32-byte Ed25519 public key from a 32-byte private seed, using the
 * same TweetNaCl primitive the rest of the SDK signs with (Ed25519Service.sign
 * rebuilds the key pair from the seed via this exact path). Lets an identity be
 * reconstructed from a recovery phrase without having stored the public key —
 * the C# reference does the same via Ed25519SigningService.DerivePublicKey.
 */
function deriveEd25519PublicKey(privateKey: Uint8Array): Uint8Array {
  if (privateKey.length !== 32) {
    throw new Error("Ed25519 private key must be 32 bytes.");
  }
  return nacl.sign.keyPair.fromSeed(privateKey).publicKey;
}
