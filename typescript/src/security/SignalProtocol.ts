/**
 * Signal Protocol implementation for end-to-end encryption.
 *
 * Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
 *   DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
 *   DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
 *   DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
 *   DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
 *
 * Root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
 * Symmetric ratchet: HMAC-SHA256, single-byte domain separation
 *   (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
 * Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
 * Identity signing: Ed25519.
 *
 * SPDX-License-Identifier: MIT
 */

import {
  createCipheriv,
  createDecipheriv,
  createHmac,
  createPrivateKey,
  createPublicKey,
  diffieHellman,
  generateKeyPairSync,
  randomBytes,
} from "crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";
import {
  MAX_SKIPPED_KEYS,
  AES_GCM_NONCE_SIZE,
  AES_GCM_TAG_SIZE,
} from "../constants.js";
import { Ed25519Service } from "./Ed25519Service.js";

// HKDF info strings — these MUST match the C# reference (and every other
// language). Any drift breaks cross-language interop.
const HKDF_ROOT_INFO = Buffer.from("aether-x3dh-root-v1", "utf8");
const HKDF_CHAIN_INITIATOR_SEND_INFO = Buffer.from("aether-chain-initiator-send-v1", "utf8");
const HKDF_CHAIN_INITIATOR_RECV_INFO = Buffer.from("aether-chain-initiator-recv-v1", "utf8");

const X25519_PUBLIC_KEY_SIZE = 32;
const X25519_PRIVATE_KEY_SIZE = 32;
const AES_KEY_SIZE = 32;

export const MESSAGE_TYPE_NORMAL = 0;
export const MESSAGE_TYPE_PRE_KEY = 1;

export interface PreKeyBundle {
  uhid: string;
  /** Long-term Ed25519 identity public key (32 bytes). */
  identityKey: Uint8Array;
  /** Long-term X25519 identity public key (32 bytes raw, RFC 7748). */
  identityKeyX25519: Uint8Array;
  preKeyId: number;
  /** One-time pre-key X25519 public key (32 bytes raw). */
  preKey: Uint8Array;
  signedPreKeyId: number;
  /** Signed pre-key X25519 public key (32 bytes raw). */
  signedPreKey: Uint8Array;
  /** Ed25519 signature over signedPreKey (64 bytes). */
  signedPreKeySignature: Uint8Array;
}

export interface EncryptedPayload {
  ciphertext: Uint8Array;
  nonce: Uint8Array;
  /** 0 = normal, 1 = PreKey (initial). */
  messageType: number;
  senderUhid: string;
  counter: number;
  encryptedAt: Date;

  /** PreKey messages: initiator's long-term X25519 identity public key (32 bytes). */
  initiatorIdentityKeyX25519?: Uint8Array;
  /** PreKey messages: initiator's ephemeral X25519 public key (32 bytes). */
  initiatorEphemeralKeyX25519?: Uint8Array;
  /** PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed. */
  usedSignedPreKeyId?: number;
  /** PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed. */
  usedOneTimePreKeyId?: number;
}

interface SignalSession {
  rootKey: Uint8Array;
  sendChainKey: Uint8Array;
  recvChainKey: Uint8Array;
  sendCounter: number;
  recvCounter: number;
  skippedMessageKeys: Map<number, Uint8Array>;

  pendingPreKeyMessage: boolean;
  initiatorIdentityKeyX25519: Uint8Array;
  initiatorEphemeralKeyX25519: Uint8Array;
  usedSignedPreKeyId: number;
  usedOneTimePreKeyId: number;
}

interface OneTimePreKey {
  priv: Uint8Array;
  pub: Uint8Array;
}

interface PreKeyState {
  signedPreKeyId: number;
  signedPreKeyPriv: Uint8Array;
  signedPreKeyPub: Uint8Array;
  signedPreKeySignature: Uint8Array;
  oneTimePreKeys: Map<number, OneTimePreKey>;
}

/**
 * X25519 helpers backed by Node's built-in `crypto` (no third-party crypto
 * dependency required for the curve itself).
 */
function generateX25519KeyPair(): { priv: Uint8Array; pub: Uint8Array } {
  const { publicKey, privateKey } = generateKeyPairSync("x25519");
  // jwk export gives raw 32-byte d (priv) and x (pub) base64url.
  const privJwk = privateKey.export({ format: "jwk" }) as { d?: string; x?: string };
  const pubJwk = publicKey.export({ format: "jwk" }) as { x?: string };
  if (!privJwk.d || !pubJwk.x) {
    throw new Error("X25519 key export missing fields");
  }
  return {
    priv: new Uint8Array(Buffer.from(privJwk.d, "base64url")),
    pub: new Uint8Array(Buffer.from(pubJwk.x, "base64url")),
  };
}

function x25519DerivePublic(priv: Uint8Array): Uint8Array {
  const privKey = createPrivateKey({
    key: { kty: "OKP", crv: "X25519", d: Buffer.from(priv).toString("base64url"), x: "" },
    format: "jwk",
  } as any);
  // Re-export with private key derives public.
  const pubKey = createPublicKey(privKey);
  const pubJwk = pubKey.export({ format: "jwk" }) as { x?: string };
  if (!pubJwk.x) throw new Error("X25519 derive public failed");
  return new Uint8Array(Buffer.from(pubJwk.x, "base64url"));
}

function x25519Agree(localPriv: Uint8Array, remotePub: Uint8Array): Uint8Array {
  if (localPriv.length !== X25519_PRIVATE_KEY_SIZE) {
    throw new Error(`X25519 private key must be ${X25519_PRIVATE_KEY_SIZE} bytes`);
  }
  if (remotePub.length !== X25519_PUBLIC_KEY_SIZE) {
    throw new Error(`X25519 public key must be ${X25519_PUBLIC_KEY_SIZE} bytes`);
  }
  const privKey = createPrivateKey({
    key: { kty: "OKP", crv: "X25519", d: Buffer.from(localPriv).toString("base64url"), x: "" },
    format: "jwk",
  } as any);
  const pubKey = createPublicKey({
    key: { kty: "OKP", crv: "X25519", x: Buffer.from(remotePub).toString("base64url") },
    format: "jwk",
  } as any);
  const shared = diffieHellman({ privateKey: privKey, publicKey: pubKey });
  // RFC 7748 §6.1: detect the all-zero output (low-order point).
  let nonZero = 0;
  for (const b of shared) nonZero |= b;
  if (nonZero === 0) {
    throw new Error("X25519 produced an all-zero shared secret (low-order point)");
  }
  return new Uint8Array(shared);
}

/**
 * Single Double-Ratchet step (Signal §5.1):
 *
 *   message_key   = HMAC-SHA256(chain_key, 0x01)
 *   new_chain_key = HMAC-SHA256(chain_key, 0x02)
 */
function ratchetStep(chainKey: Uint8Array): { newChainKey: Uint8Array; messageKey: Uint8Array } {
  const messageKey = createHmac("sha256", chainKey).update(Buffer.from([0x01])).digest();
  const newChainKey = createHmac("sha256", chainKey).update(Buffer.from([0x02])).digest();
  return {
    newChainKey: new Uint8Array(newChainKey),
    messageKey: new Uint8Array(messageKey),
  };
}

/** HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey. */
function hkdf32(ikm: Uint8Array, info: Uint8Array): Uint8Array {
  return new Uint8Array(hkdf(sha256, ikm, undefined, info, AES_KEY_SIZE));
}

function concat(...arrays: Uint8Array[]): Uint8Array {
  let total = 0;
  for (const a of arrays) total += a.length;
  const out = new Uint8Array(total);
  let offset = 0;
  for (const a of arrays) {
    out.set(a, offset);
    offset += a.length;
  }
  return out;
}

function randomPositiveInt32(): number {
  // 31-bit positive non-zero.
  const r = randomBytes(4).readUInt32BE() & 0x7fffffff;
  return r === 0 ? 1 : r;
}

export class SignalProtocol {
  private sessions: Map<string, SignalSession> = new Map();

  // Long-term identity keys — two distinct keypairs per node.
  private identityX25519Priv: Uint8Array;
  private identityX25519Pub: Uint8Array;
  private ed25519PrivateKey: Uint8Array;
  private ed25519PublicKey: Uint8Array;

  // Local UHID — captured when generatePreKeyBundle is called or via setLocalUhid.
  private localUhid: string | undefined;

  // Pre-key state held for responder-side X3DH.
  private preKeys: PreKeyState = {
    signedPreKeyId: 0,
    signedPreKeyPriv: new Uint8Array(),
    signedPreKeyPub: new Uint8Array(),
    signedPreKeySignature: new Uint8Array(),
    oneTimePreKeys: new Map(),
  };

  constructor() {
    const ed25519KeyPair = Ed25519Service.generateKeyPair();
    this.ed25519PrivateKey = ed25519KeyPair.privateKey;
    this.ed25519PublicKey = ed25519KeyPair.publicKey;

    const x = generateX25519KeyPair();
    this.identityX25519Priv = x.priv;
    this.identityX25519Pub = x.pub;
  }

  /** Sets the local node's UHID. Required before any encrypt() call. */
  setLocalUhid(uhid: string): void {
    if (!uhid) throw new Error("uhid cannot be empty");
    this.localUhid = uhid;
  }

  hasSession(peerUhid: string): boolean {
    return this.sessions.has(peerUhid);
  }

  async encrypt(peerUhid: string, plaintext: Uint8Array): Promise<EncryptedPayload> {
    const session = this.sessions.get(peerUhid);
    if (!session) {
      throw new Error(`No session established with peer ${peerUhid}`);
    }
    if (!this.localUhid) {
      throw new Error(
        "Local UHID is not set. Call generatePreKeyBundle(uhid) or setLocalUhid(uhid) before encrypting."
      );
    }

    const { newChainKey, messageKey } = ratchetStep(session.sendChainKey);
    session.sendChainKey = newChainKey;

    const nonce = randomBytes(AES_GCM_NONCE_SIZE);
    const cipher = createCipheriv("aes-256-gcm", messageKey, nonce);
    const ct = cipher.update(plaintext);
    const finalCt = Buffer.concat([ct, cipher.final()]);
    const tag = cipher.getAuthTag();
    const combined = Buffer.concat([finalCt, tag]);

    const counter = session.sendCounter++;
    messageKey.fill(0);

    const base: EncryptedPayload = {
      ciphertext: new Uint8Array(combined),
      nonce: new Uint8Array(nonce),
      messageType: MESSAGE_TYPE_NORMAL,
      senderUhid: this.localUhid,
      counter,
      encryptedAt: new Date(),
    };

    if (session.pendingPreKeyMessage) {
      const payload: EncryptedPayload = {
        ...base,
        messageType: MESSAGE_TYPE_PRE_KEY,
        initiatorIdentityKeyX25519: new Uint8Array(session.initiatorIdentityKeyX25519),
        initiatorEphemeralKeyX25519: new Uint8Array(session.initiatorEphemeralKeyX25519),
        usedSignedPreKeyId: session.usedSignedPreKeyId,
        usedOneTimePreKeyId: session.usedOneTimePreKeyId,
      };
      session.pendingPreKeyMessage = false;
      return payload;
    }

    return base;
  }

  async decrypt(peerUhid: string, payload: EncryptedPayload): Promise<Uint8Array> {
    if (payload.messageType === MESSAGE_TYPE_PRE_KEY) {
      if (!payload.initiatorIdentityKeyX25519 || !payload.initiatorEphemeralKeyX25519) {
        throw new Error("PreKey message missing initiator key material");
      }
      this.establishResponderSession(peerUhid, payload);
    }

    const session = this.sessions.get(peerUhid);
    if (!session) {
      throw new Error(`No session established with peer ${peerUhid}`);
    }

    if (payload.ciphertext.length < AES_GCM_TAG_SIZE) {
      throw new Error("Ciphertext too short");
    }

    let messageKey: Uint8Array;
    if (session.skippedMessageKeys.has(payload.counter)) {
      messageKey = session.skippedMessageKeys.get(payload.counter)!;
      session.skippedMessageKeys.delete(payload.counter);
    } else {
      const gap = payload.counter - session.recvCounter;
      if (gap > MAX_SKIPPED_KEYS) {
        throw new Error(
          `Message counter gap (${gap}) exceeds maximum (${MAX_SKIPPED_KEYS}). Session must be re-established.`
        );
      }
      while (session.recvCounter < payload.counter) {
        const step = ratchetStep(session.recvChainKey);
        session.recvChainKey = step.newChainKey;
        session.skippedMessageKeys.set(session.recvCounter, step.messageKey);
        session.recvCounter++;
      }
      const step = ratchetStep(session.recvChainKey);
      session.recvChainKey = step.newChainKey;
      messageKey = step.messageKey;
      session.recvCounter++;
    }

    const ciphertextLength = payload.ciphertext.length - AES_GCM_TAG_SIZE;
    const ciphertext = payload.ciphertext.slice(0, ciphertextLength);
    const tag = payload.ciphertext.slice(ciphertextLength);
    const decipher = createDecipheriv("aes-256-gcm", messageKey, payload.nonce);
    decipher.setAuthTag(tag);
    const plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
    messageKey.fill(0);
    return new Uint8Array(plaintext);
  }

  async generatePreKeyBundle(localUhid: string): Promise<PreKeyBundle> {
    if (!localUhid) throw new Error("localUhid cannot be empty");
    this.localUhid = localUhid;

    const otpk = generateX25519KeyPair();
    const preKeyId = randomPositiveInt32();
    this.preKeys.oneTimePreKeys.set(preKeyId, { priv: otpk.priv, pub: otpk.pub });

    const spk = generateX25519KeyPair();
    const signedPreKeyId = randomPositiveInt32();
    const signature = Ed25519Service.sign(this.ed25519PrivateKey, spk.pub);
    this.preKeys.signedPreKeyId = signedPreKeyId;
    this.preKeys.signedPreKeyPriv = spk.priv;
    this.preKeys.signedPreKeyPub = spk.pub;
    this.preKeys.signedPreKeySignature = signature;

    return {
      uhid: localUhid,
      identityKey: new Uint8Array(this.ed25519PublicKey),
      identityKeyX25519: new Uint8Array(this.identityX25519Pub),
      preKeyId,
      preKey: new Uint8Array(otpk.pub),
      signedPreKeyId,
      signedPreKey: new Uint8Array(spk.pub),
      signedPreKeySignature: signature,
    };
  }

  async processPreKeyBundle(bundle: PreKeyBundle): Promise<void> {
    const ok = Ed25519Service.verify(
      bundle.identityKey,
      bundle.signedPreKey,
      bundle.signedPreKeySignature
    );
    if (!ok) throw new Error("Signed pre-key signature verification failed");

    if (bundle.identityKeyX25519.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Bundle has malformed identityKeyX25519 (length ${bundle.identityKeyX25519.length})`);
    }
    if (bundle.signedPreKey.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Bundle has malformed signedPreKey (length ${bundle.signedPreKey.length})`);
    }
    if (bundle.preKey.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Bundle has malformed preKey (length ${bundle.preKey.length})`);
    }

    // Fresh ephemeral X25519 keypair, generated per-session.
    const ek = generateX25519KeyPair();

    // X3DH 4-DH key agreement (initiator side).
    const dh1 = x25519Agree(this.identityX25519Priv, bundle.signedPreKey);
    const dh2 = x25519Agree(ek.priv, bundle.identityKeyX25519);
    const dh3 = x25519Agree(ek.priv, bundle.signedPreKey);
    const dh4 = x25519Agree(ek.priv, bundle.preKey);

    const shared = concat(dh1, dh2, dh3, dh4);
    const rootKey = hkdf32(shared, HKDF_ROOT_INFO);
    const sendChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_SEND_INFO);
    const recvChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_RECV_INFO);

    const session: SignalSession = {
      rootKey,
      sendChainKey: sendChain,
      recvChainKey: recvChain,
      sendCounter: 0,
      recvCounter: 0,
      skippedMessageKeys: new Map(),
      pendingPreKeyMessage: true,
      initiatorIdentityKeyX25519: new Uint8Array(this.identityX25519Pub),
      initiatorEphemeralKeyX25519: new Uint8Array(ek.pub),
      usedSignedPreKeyId: bundle.signedPreKeyId,
      usedOneTimePreKeyId: bundle.preKeyId,
    };
    this.sessions.set(bundle.uhid, session);

    // Best-effort scrubbing.
    ek.priv.fill(0);
    shared.fill(0);
    dh1.fill(0);
    dh2.fill(0);
    dh3.fill(0);
    dh4.fill(0);
  }

  /**
   * Mirrors the initiator's 4 X3DH DHs to derive the same root key, then
   * derives chain keys with send/recv roles SWAPPED relative to the
   * initiator. Consumes (and zeros) the one-time pre-key.
   */
  private establishResponderSession(peerUhid: string, payload: EncryptedPayload): void {
    const ik = payload.initiatorIdentityKeyX25519!;
    const ek = payload.initiatorEphemeralKeyX25519!;
    if (ik.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Initiator IK_X25519 wrong size: ${ik.length}`);
    }
    if (ek.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Initiator EK_X25519 wrong size: ${ek.length}`);
    }
    if (
      this.preKeys.signedPreKeyId !== (payload.usedSignedPreKeyId ?? 0) ||
      this.preKeys.signedPreKeyPriv.length === 0
    ) {
      throw new Error(
        `PreKey message references signed pre-key id ${payload.usedSignedPreKeyId} which is not held by this node.`
      );
    }
    const opkId = payload.usedOneTimePreKeyId ?? 0;
    const otpk = this.preKeys.oneTimePreKeys.get(opkId);
    if (!otpk) {
      throw new Error(
        `PreKey message references one-time pre-key id ${opkId} which is not held (already consumed?).`
      );
    }

    // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
    const dh1 = x25519Agree(this.preKeys.signedPreKeyPriv, ik);
    const dh2 = x25519Agree(this.identityX25519Priv, ek);
    const dh3 = x25519Agree(this.preKeys.signedPreKeyPriv, ek);
    const dh4 = x25519Agree(otpk.priv, ek);

    const shared = concat(dh1, dh2, dh3, dh4);
    const rootKey = hkdf32(shared, HKDF_ROOT_INFO);
    // SWAPPED: initiator's send-chain info derives our recv-chain.
    const recvChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_SEND_INFO);
    const sendChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_RECV_INFO);

    this.sessions.set(peerUhid, {
      rootKey,
      sendChainKey: sendChain,
      recvChainKey: recvChain,
      sendCounter: 0,
      recvCounter: 0,
      skippedMessageKeys: new Map(),
      pendingPreKeyMessage: false,
      initiatorIdentityKeyX25519: new Uint8Array(),
      initiatorEphemeralKeyX25519: new Uint8Array(),
      usedSignedPreKeyId: 0,
      usedOneTimePreKeyId: 0,
    });

    // Consume one-time pre-key — never reuse.
    otpk.priv.fill(0);
    this.preKeys.oneTimePreKeys.delete(opkId);

    shared.fill(0);
    dh1.fill(0);
    dh2.fill(0);
    dh3.fill(0);
    dh4.fill(0);
  }

  getPublicKey(): Uint8Array {
    return new Uint8Array(this.ed25519PublicKey);
  }

  getX25519PublicKey(): Uint8Array {
    return new Uint8Array(this.identityX25519Pub);
  }
}
