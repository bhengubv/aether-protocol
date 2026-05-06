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
 *
 * Double Ratchet (Signal §5): each side maintains a current X25519 ratchet
 * keypair. Whenever the receiver sees a peer message bearing a new ratchet
 * public key, it does a DH-ratchet step:
 *   newRecvChain = KDF_RK(rootKey, DH(myDHs_priv, newDHr))
 *   newDHs       = fresh X25519 keypair
 *   newSendChain = KDF_RK(rootKey, DH(newDHs_priv, newDHr))
 * Signal-canonical X3DH↔Double-Ratchet integration: the initiator's X3DH
 * ephemeral becomes its first DHs; the peer's signed pre-key is the initial
 * DHr. CKs is computed lazily on the initiator's first send.
 *
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
  timingSafeEqual,
} from "crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";
import {
  MAX_SKIPPED_KEYS,
  AES_GCM_NONCE_SIZE,
  AES_GCM_TAG_SIZE,
} from "../constants.js";
import { Ed25519Service } from "./Ed25519Service.js";
import {
  PreKeyStore,
  StoredIdentityKeys,
  StoredSignedPreKey,
  StoredSignedPreKeyHistory,
  StoredOneTimePreKey,
} from "./PreKeyStore.js";
import {
  SignalSessionStore,
  StoredSignalSession,
} from "./SignalSessionStore.js";

// HKDF info strings — these MUST match the C# reference (and every other
// language). Any drift breaks cross-language interop.
const HKDF_ROOT_INFO = Buffer.from("aether-x3dh-root-v1", "utf8");
// KDF_RK info string for Double-Ratchet step (Signal §5: KDF_RK). Each
// DH-ratchet step derives a 64-byte block, split into the new root key
// (first 32 bytes) and the new chain key (second 32 bytes).
const HKDF_RATCHET_INFO = Buffer.from("aether-ratchet-rk-v1", "utf8");

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

/**
 * An encrypted payload with all metadata needed for decryption.
 *
 * Two layered ratchets contribute fields:
 *
 * 1. X3DH session-establishment (Signal §3) — populated only on the first
 *    message a new initiator sends to a peer (messageType=1):
 *    initiatorIdentityKeyX25519, usedSignedPreKeyId, usedOneTimePreKeyId.
 *    The responder uses these to run X3DH on its side and derive the same
 *    root key.
 *
 * 2. Double Ratchet (Signal §5) — senderEphemeralKeyX25519 and
 *    previousChainCount populated on EVERY message. senderEphemeralKeyX25519
 *    is the sender's current DH-ratchet public key; when it changes between
 *    messages, the receiver runs a DH-ratchet step that re-keys the chain.
 *    On the very first PreKey message, this equals the X3DH ephemeral
 *    public key (Signal-canonical integration).
 */
export interface EncryptedPayload {
  ciphertext: Uint8Array;
  nonce: Uint8Array;
  /** 0 = normal, 1 = PreKey (initial). */
  messageType: number;
  senderUhid: string;
  /** Counter within the current sending chain (Signal §5: Ns). */
  counter: number;
  encryptedAt: Date;

  /** PreKey messages: initiator's long-term X25519 identity public key (32 bytes). */
  initiatorIdentityKeyX25519?: Uint8Array;
  /**
   * DEPRECATED backward-compat field — equals senderEphemeralKeyX25519 on
   * PreKey messages, undefined on normal messages. Kept so older peers
   * (pre-Double-Ratchet wire envelope) can still read the initiator's
   * ratchet pub. New consumers should read senderEphemeralKeyX25519.
   */
  initiatorEphemeralKeyX25519?: Uint8Array;
  /** PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed. */
  usedSignedPreKeyId?: number;
  /** PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed. */
  usedOneTimePreKeyId?: number;

  /**
   * Sender's current DH-ratchet X25519 public key (32 bytes). Populated on
   * EVERY message. Drives the DH-ratchet step on the receiver side: when
   * this value changes, the receiver re-keys the chain via
   * KDF_RK(rootKey, DH(myDHs, newDHr)).
   */
  senderEphemeralKeyX25519?: Uint8Array;
  /**
   * Number of messages the sender sent in its previous sending chain
   * (Signal §5: PN). Used by the receiver to compute skipped message keys
   * when crossing a DH-ratchet boundary.
   */
  previousChainCount?: number;
}

/**
 * State of a Signal-Protocol session with a single peer — both X3DH
 * session-establishment metadata and Double-Ratchet (Signal §5) state.
 *
 *   rootKey            — RK. Re-keyed on every DH-ratchet step.
 *   myEphemeralPriv/Pub — DHs. My current ratchet keypair.
 *   remoteEphemeralPub  — DHr. Peer's last-known ratchet pub. null until first DH-ratchet.
 *   sendChainKey        — CKs. null until I've sent (or initialized) on this chain.
 *   recvChainKey        — CKr. null until I've received on this chain.
 *   sendCounter/recvCounter — Ns/Nr. Reset on each DH-ratchet step.
 *   previousChainCount  — PN. Number of messages sent in my previous sending chain.
 *   skippedMessageKeys  — Skipped keys keyed by "Hex(remoteEphPub):counter".
 */
interface SignalSession {
  rootKey: Uint8Array;
  /** Sending chain key. null until first send (or until DH-ratchet rekeys it). */
  sendChainKey: Uint8Array | null;
  /** Receiving chain key. null until first receive that triggers a DH-ratchet step. */
  recvChainKey: Uint8Array | null;

  sendCounter: number;
  recvCounter: number;
  /** Messages sent in the previous sending chain (Signal §5: PN). */
  previousChainCount: number;

  /** My current DH-ratchet private key (X25519, 32 bytes). */
  myEphemeralPriv: Uint8Array;
  /** My current DH-ratchet public key (X25519, 32 bytes). */
  myEphemeralPub: Uint8Array;
  /** Peer's last-seen DH-ratchet public key. null until first DH-ratchet step. */
  remoteEphemeralPub: Uint8Array | null;

  /**
   * Skipped message keys keyed by "Hex(remoteEphPub):counter". The
   * remoteEphPub binding is essential — out-of-order messages from a
   * previous chain (different DHr) can still arrive after a DH-ratchet
   * step, and they need their own per-chain key set.
   */
  skippedMessageKeys: Map<string, Uint8Array>;

  /**
   * True iff this session was established in the initiator role and the
   * first outbound message has not yet been sent. While true, the next
   * encrypt() emits a PreKey message (messageType=1) carrying the X3DH
   * inputs.
   */
  pendingPreKeyMessage: boolean;
  initiatorIdentityKeyX25519: Uint8Array;
  usedSignedPreKeyId: number;
  usedOneTimePreKeyId: number;
}

interface OneTimePreKey {
  priv: Uint8Array;
  pub: Uint8Array;
}

/**
 * Pre-key state held by the responder side: signed pre-key (rotated
 * periodically) and a pool of one-time pre-keys (each consumed exactly once).
 * The private halves stay on the responder so that when a PreKey message
 * arrives, the matching X3DH DHs can be computed.
 *
 * One-time pre-keys are managed as a pool of {@link SignalProtocol.opkPoolSize}
 * (default 100) entries. Bundle generation hands out the next-unused id from
 * {@link availableOpkIds}; the OPK stays in {@link oneTimePreKeys} until a
 * responder consumes it via X3DH, at which point it is zeroed and removed.
 * Top-up runs each time a bundle is generated so the available queue never
 * empties under steady load.
 *
 * Mirrors the C# {@code PreKeyState} layout in
 * src/Aether.Security/Services/SignalProtocolService.cs.
 */
interface PreKeyState {
  /**
   * Active signed-pre-key id. Mirrors the id of {@code signedPreKeyHistory[length-1]}
   * — kept as a denormalised field for the existing fast-path code that
   * references it directly without a list lookup.
   */
  signedPreKeyId: number;
  signedPreKeyPriv: Uint8Array;
  signedPreKeyPub: Uint8Array;
  signedPreKeySignature: Uint8Array;
  /**
   * Signed-pre-key history: oldest first, newest last. The newest entry
   * (i.e. the last) is the active SPK that gets handed out in bundles.
   * Older entries are retained for the rotation window so that messages
   * signed under a recently-rotated SPK can still complete X3DH.
   */
  signedPreKeyHistory: SignedPreKeyEntry[];
  /** All OPKs currently held — un-issued AND issued-but-not-consumed. */
  oneTimePreKeys: Map<number, OneTimePreKey>;
  /**
   * IDs of OPKs that exist in {@link oneTimePreKeys} and have NOT yet been
   * issued in any bundle. Bundle generation pops from the front (FIFO);
   * top-up generates new OPKs and pushes onto the back.
   */
  availableOpkIds: number[];
}

/**
 * One signed-pre-key in the history (active or retained-prior). The
 * private half is held so that responder-side X3DH can still complete
 * when a peer presents a slightly-stale SPK during the rotation window.
 */
interface SignedPreKeyEntry {
  id: number;
  privateKey: Uint8Array;
  publicKey: Uint8Array;
  signature: Uint8Array;
  /** Unix epoch milliseconds. */
  generatedAtUnixMs: number;
}

/**
 * Configuration for periodic signed-pre-key rotation (Signal §3.3 — keys
 * SHOULD be rotated periodically).
 *
 * On every {@link SignalProtocol.generatePreKeyBundle} call the service
 * checks whether the active SPK is older than {@link rotationIntervalMs};
 * if it is, a fresh SPK is generated and the old one is appended to the
 * history. The history is then trimmed to keep at most
 * {@link retainedHistoryCount} prior entries (plus the new active one).
 * Messages signed under any retained SPK still decrypt; messages signed
 * under a pruned SPK fail.
 *
 * Mirrors the C# {@code SignedPreKeyRotationOptions} record.
 */
export interface SignedPreKeyRotationOptions {
  /** Rotation interval in milliseconds. Default: 7 days. */
  rotationIntervalMs: number;
  /** Number of retained prior entries (in addition to the active one). Default: 3. */
  retainedHistoryCount: number;
}

/** Default rotation options: 7-day interval, 3 retained prior entries. */
export const DEFAULT_SPK_ROTATION_OPTIONS: SignedPreKeyRotationOptions = Object.freeze({
  rotationIntervalMs: 7 * 24 * 60 * 60 * 1000,
  retainedHistoryCount: 3,
});

/** Default size of the one-time pre-key pool (matches C# DefaultOpkPoolSize). */
export const DEFAULT_OPK_POOL_SIZE = 100;

/** Construction options for {@link SignalProtocol}. */
export interface SignalProtocolOptions {
  /**
   * Target size of the one-time pre-key pool. Topped up to this many
   * available (un-issued) keys on every bundle generation; consumed keys
   * are replaced lazily on the next bundle call. Defaults to
   * {@link DEFAULT_OPK_POOL_SIZE}.
   */
  opkPoolSize?: number;

  /**
   * Persistent session store. When supplied, every encrypt / decrypt
   * mutation triggers a save and existing sessions are loaded on
   * construction. Saves are best-effort: failures are logged via
   * {@code onPersistenceError} (or swallowed) and the message flow
   * continues uninterrupted.
   */
  sessionStore?: SignalSessionStore;

  /**
   * Persistent pre-key store. When supplied, identity keys, the SPK
   * history and the OPK pool are loaded on construction (or generated
   * + saved if no prior state exists), and every mutation triggers a
   * best-effort save.
   */
  preKeyStore?: PreKeyStore;

  /**
   * Configuration for periodic signed-pre-key rotation. Defaults to
   * {@link DEFAULT_SPK_ROTATION_OPTIONS} (7-day interval, 3 retained
   * prior entries).
   */
  rotationOptions?: SignedPreKeyRotationOptions;

  /**
   * Synthetic clock — used by tests to drive rotation deterministically.
   * Defaults to {@code () => new Date()}.
   */
  nowProvider?: () => Date;

  /**
   * Optional sink for persistence-layer errors. Invoked with a message
   * string; receivers typically forward to a logger. Defaults to a
   * silent no-op so persistence failures never bubble up the message
   * flow.
   */
  onPersistenceError?: (message: string, err: unknown) => void;
}

/** Snapshot of the OPK pool's current size, exposed for tests/observability. */
export interface OpkPoolStatus {
  /** OPKs currently held — un-issued AND issued-but-not-yet-consumed. */
  held: number;
  /** OPKs in the pool that have not yet been issued in any bundle. */
  available: number;
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
 * Single Double-Ratchet symmetric step (Signal §5.1):
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

/**
 * KDF_RK per Signal §5.2: derives a new root key + new chain key from the
 * current root key and a fresh DH output. HKDF-SHA256 over 64 bytes;
 * salt=rootKey, ikm=dhOutput, info="aether-ratchet-rk-v1". First 32 bytes =
 * new root, second 32 bytes = new chain key.
 */
function kdfRk(rootKey: Uint8Array, dhOutput: Uint8Array): { newRootKey: Uint8Array; newChainKey: Uint8Array } {
  const derived = hkdf(sha256, dhOutput, rootKey, HKDF_RATCHET_INFO, 64);
  const newRootKey = new Uint8Array(32);
  const newChainKey = new Uint8Array(32);
  newRootKey.set(derived.subarray(0, 32));
  newChainKey.set(derived.subarray(32, 64));
  // Best-effort scrub of the combined block.
  derived.fill(0);
  return { newRootKey, newChainKey };
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

function constantTimeEquals(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  return timingSafeEqual(Buffer.from(a), Buffer.from(b));
}

function toHex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("hex").toUpperCase();
}

/** Skipped-keys cache key — binds to (remote DHr pub, counter) per Signal §5. */
function skippedKey(dhrPub: Uint8Array, counter: number): string {
  return `${toHex(dhrPub)}:${counter}`;
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

  /**
   * Target pool size — top-up to this many un-issued OPKs on each
   * generatePreKeyBundle call. Defaults to {@link DEFAULT_OPK_POOL_SIZE}.
   */
  readonly opkPoolSize: number;

  // Pre-key state held for responder-side X3DH.
  private preKeys: PreKeyState = {
    signedPreKeyId: 0,
    signedPreKeyPriv: new Uint8Array(),
    signedPreKeyPub: new Uint8Array(),
    signedPreKeySignature: new Uint8Array(),
    signedPreKeyHistory: [],
    oneTimePreKeys: new Map(),
    availableOpkIds: [],
  };

  /**
   * Promise-chain serialising mutations against the OPK pool. JS is
   * single-threaded but `await` interleaves between Promises, so two
   * concurrent generatePreKeyBundle calls could otherwise observe each
   * other's partial state. We chain them so each completes atomically.
   */
  private opkLock: Promise<void> = Promise.resolve();

  // ─── Persistence wiring ────────────────────────────────────────────────
  private readonly sessionStore?: SignalSessionStore;
  private readonly preKeyStore?: PreKeyStore;
  private readonly rotationOptions: SignedPreKeyRotationOptions;
  private readonly nowProvider: () => Date;
  private readonly onPersistenceError: (message: string, err: unknown) => void;
  /**
   * Promise that resolves once the constructor's hydration pass has
   * finished. Every async public method awaits this before touching
   * state so a fresh instance handed to encrypt() before its stores
   * have loaded behaves identically to one whose hydration completed
   * first.
   */
  private readonly hydration: Promise<void>;

  constructor(options: SignalProtocolOptions = {}) {
    const opkPoolSize = options.opkPoolSize ?? DEFAULT_OPK_POOL_SIZE;
    if (!Number.isInteger(opkPoolSize) || opkPoolSize < 1) {
      throw new Error(`opkPoolSize must be an integer >= 1 (got ${opkPoolSize}).`);
    }
    this.opkPoolSize = opkPoolSize;

    this.sessionStore = options.sessionStore;
    this.preKeyStore = options.preKeyStore;
    this.rotationOptions = options.rotationOptions ?? DEFAULT_SPK_ROTATION_OPTIONS;
    this.nowProvider = options.nowProvider ?? (() => new Date());
    this.onPersistenceError =
      options.onPersistenceError ?? ((_msg, _err) => undefined);

    if (this.rotationOptions.rotationIntervalMs <= 0) {
      throw new Error("rotationOptions.rotationIntervalMs must be > 0.");
    }
    if (this.rotationOptions.retainedHistoryCount < 0) {
      throw new Error("rotationOptions.retainedHistoryCount must be >= 0.");
    }

    const ed25519KeyPair = Ed25519Service.generateKeyPair();
    this.ed25519PrivateKey = ed25519KeyPair.privateKey;
    this.ed25519PublicKey = ed25519KeyPair.publicKey;

    const x = generateX25519KeyPair();
    this.identityX25519Priv = x.priv;
    this.identityX25519Pub = x.pub;

    // Hydration runs asynchronously off the constructor; every public
    // async entry point awaits this.hydration before touching state.
    this.hydration = this.hydrate();
  }

  /**
   * Returns a promise that resolves once the constructor's hydration
   * pass (loading identity, SPK history, OPK pool, sessions from the
   * configured stores) has completed. Tests that want to assert state
   * immediately after construction should await this. Public async
   * methods already await internally — callers can ignore this for
   * normal use.
   */
  ready(): Promise<void> {
    return this.hydration;
  }

  /**
   * Loads persisted identity, SPK history, OPK pool, and active
   * sessions from the configured stores. Best-effort: any failure
   * surfaces via {@link onPersistenceError} but never throws — the
   * caller continues with the freshly-generated identity.
   */
  private async hydrate(): Promise<void> {
    if (this.preKeyStore) {
      try {
        const stored = await this.preKeyStore.loadIdentity();
        if (stored) {
          this.ed25519PrivateKey = new Uint8Array(stored.ed25519PrivateKey);
          this.ed25519PublicKey = new Uint8Array(stored.ed25519PublicKey);
          this.identityX25519Priv = new Uint8Array(stored.x25519PrivateKey);
          this.identityX25519Pub = new Uint8Array(stored.x25519PublicKey);
          if (stored.localUhid) this.localUhid = stored.localUhid;
        } else {
          // First boot — persist the freshly-generated identity.
          await this.preKeyStore.saveIdentity({
            ed25519PrivateKey: new Uint8Array(this.ed25519PrivateKey),
            ed25519PublicKey: new Uint8Array(this.ed25519PublicKey),
            x25519PrivateKey: new Uint8Array(this.identityX25519Priv),
            x25519PublicKey: new Uint8Array(this.identityX25519Pub),
            localUhid: this.localUhid ?? null,
          });
        }
      } catch (err) {
        this.onPersistenceError("Failed to hydrate identity keys.", err);
      }

      try {
        const history = await this.preKeyStore.loadSignedPreKeys();
        const sorted = [...history.entries].sort(
          (a, b) => a.generatedAtUnixMs - b.generatedAtUnixMs
        );
        this.preKeys.signedPreKeyHistory = sorted.map((e) => ({
          id: e.id,
          privateKey: new Uint8Array(e.privateKey),
          publicKey: new Uint8Array(e.publicKey),
          signature: new Uint8Array(e.signature),
          generatedAtUnixMs: e.generatedAtUnixMs,
        }));
        if (this.preKeys.signedPreKeyHistory.length > 0) {
          const active = this.preKeys.signedPreKeyHistory[this.preKeys.signedPreKeyHistory.length - 1];
          this.preKeys.signedPreKeyId = active.id;
          this.preKeys.signedPreKeyPriv = active.privateKey;
          this.preKeys.signedPreKeyPub = active.publicKey;
          this.preKeys.signedPreKeySignature = active.signature;
        }
      } catch (err) {
        this.onPersistenceError("Failed to hydrate SPK history.", err);
      }

      try {
        const pool = await this.preKeyStore.loadOneTimePreKeys();
        this.preKeys.oneTimePreKeys.clear();
        this.preKeys.availableOpkIds.length = 0;
        for (const [id, opk] of pool.entries()) {
          this.preKeys.oneTimePreKeys.set(id, {
            priv: new Uint8Array(opk.privateKey),
            pub: new Uint8Array(opk.publicKey),
          });
          if (!opk.issued) this.preKeys.availableOpkIds.push(id);
        }
      } catch (err) {
        this.onPersistenceError("Failed to hydrate OPK pool.", err);
      }
    }

    if (this.sessionStore) {
      try {
        const peers = await this.sessionStore.listPeers();
        for (const peer of peers) {
          try {
            const stored = await this.sessionStore.load(peer);
            if (stored) {
              this.sessions.set(peer, this.fromStoredSession(stored));
            }
          } catch (err) {
            this.onPersistenceError(`Failed to load session for peer.`, err);
          }
        }
      } catch (err) {
        this.onPersistenceError("Failed to enumerate persisted sessions.", err);
      }
    }
  }

  // ─── Best-effort persistence helpers ──────────────────────────────────

  /**
   * Promise chain tracking every in-flight fire-and-forget save. Fresh
   * write Promises are appended; tests / hosts can {@link flushPendingWrites}
   * to wait for the chain to settle before snapshotting state. Saves
   * are best-effort: errors are routed through {@link onPersistenceError}
   * but never reject the chain.
   */
  private pendingWrites: Promise<void> = Promise.resolve();

  /**
   * Awaits every fire-and-forget persistence write started up to the
   * current call. Useful in tests that need to assert the underlying
   * store contents, or for hosts implementing graceful shutdown.
   */
  flushPendingWrites(): Promise<void> {
    return this.pendingWrites;
  }

  private trackWrite(label: string, work: () => Promise<void>): void {
    const next = this.pendingWrites
      .catch(() => undefined)
      .then(() => work())
      .catch((err) => this.onPersistenceError(label, err));
    this.pendingWrites = next;
  }

  private persistSession(peerUhid: string, session: SignalSession): void {
    if (!this.sessionStore) return;
    const snapshot = this.toStoredSession(session);
    const store = this.sessionStore;
    this.trackWrite("Failed to persist session.", () => store.save(peerUhid, snapshot));
  }

  private persistIdentity(): void {
    if (!this.preKeyStore) return;
    const snapshot: StoredIdentityKeys = {
      ed25519PrivateKey: new Uint8Array(this.ed25519PrivateKey),
      ed25519PublicKey: new Uint8Array(this.ed25519PublicKey),
      x25519PrivateKey: new Uint8Array(this.identityX25519Priv),
      x25519PublicKey: new Uint8Array(this.identityX25519Pub),
      localUhid: this.localUhid ?? null,
    };
    const store = this.preKeyStore;
    this.trackWrite("Failed to persist identity keys.", () => store.saveIdentity(snapshot));
  }

  private persistSignedPreKeys(): void {
    if (!this.preKeyStore) return;
    const snapshot: StoredSignedPreKeyHistory = {
      entries: this.preKeys.signedPreKeyHistory.map((e) => ({
        id: e.id,
        privateKey: new Uint8Array(e.privateKey),
        publicKey: new Uint8Array(e.publicKey),
        signature: new Uint8Array(e.signature),
        generatedAtUnixMs: e.generatedAtUnixMs,
      })),
    };
    const store = this.preKeyStore;
    this.trackWrite("Failed to persist SPK history.", () => store.saveSignedPreKeys(snapshot));
  }

  private persistOneTimePreKeys(): void {
    if (!this.preKeyStore) return;
    const issued = new Set<number>(this.preKeys.oneTimePreKeys.keys());
    for (const id of this.preKeys.availableOpkIds) issued.delete(id);
    const snapshot = new Map<number, StoredOneTimePreKey>();
    for (const [id, opk] of this.preKeys.oneTimePreKeys.entries()) {
      snapshot.set(id, {
        id,
        privateKey: new Uint8Array(opk.priv),
        publicKey: new Uint8Array(opk.pub),
        issued: issued.has(id),
      });
    }
    const store = this.preKeyStore;
    this.trackWrite("Failed to persist OPK pool.", () => store.saveOneTimePreKeys(snapshot));
  }

  private consumeOneTimePreKey(id: number): void {
    if (!this.preKeyStore) return;
    const store = this.preKeyStore;
    this.trackWrite(`Failed to consume OPK ${id}.`, () => store.consumeOneTimePreKey(id));
  }

  // ─── Session ↔ StoredSignalSession ────────────────────────────────────

  private toStoredSession(s: SignalSession): StoredSignalSession {
    return {
      rootKey: new Uint8Array(s.rootKey),
      sendChainKey: s.sendChainKey ? new Uint8Array(s.sendChainKey) : null,
      recvChainKey: s.recvChainKey ? new Uint8Array(s.recvChainKey) : null,
      sendCounter: s.sendCounter,
      recvCounter: s.recvCounter,
      previousChainCount: s.previousChainCount,
      myEphemeralPriv: new Uint8Array(s.myEphemeralPriv),
      myEphemeralPub: new Uint8Array(s.myEphemeralPub),
      remoteEphemeralPub: s.remoteEphemeralPub ? new Uint8Array(s.remoteEphemeralPub) : null,
      skippedMessageKeys: new Map(
        Array.from(s.skippedMessageKeys.entries()).map(([k, v]) => [k, new Uint8Array(v)])
      ),
      pendingPreKeyMessage: s.pendingPreKeyMessage,
      initiatorIdentityKeyX25519: new Uint8Array(s.initiatorIdentityKeyX25519),
      usedSignedPreKeyId: s.usedSignedPreKeyId,
      usedOneTimePreKeyId: s.usedOneTimePreKeyId,
    };
  }

  private fromStoredSession(s: StoredSignalSession): SignalSession {
    return {
      rootKey: new Uint8Array(s.rootKey),
      sendChainKey: s.sendChainKey ? new Uint8Array(s.sendChainKey) : null,
      recvChainKey: s.recvChainKey ? new Uint8Array(s.recvChainKey) : null,
      sendCounter: s.sendCounter,
      recvCounter: s.recvCounter,
      previousChainCount: s.previousChainCount,
      myEphemeralPriv: new Uint8Array(s.myEphemeralPriv),
      myEphemeralPub: new Uint8Array(s.myEphemeralPub),
      remoteEphemeralPub: s.remoteEphemeralPub ? new Uint8Array(s.remoteEphemeralPub) : null,
      skippedMessageKeys: new Map(
        Array.from(s.skippedMessageKeys.entries()).map(([k, v]) => [k, new Uint8Array(v)])
      ),
      pendingPreKeyMessage: s.pendingPreKeyMessage,
      initiatorIdentityKeyX25519: new Uint8Array(s.initiatorIdentityKeyX25519),
      usedSignedPreKeyId: s.usedSignedPreKeyId,
      usedOneTimePreKeyId: s.usedOneTimePreKeyId,
    };
  }

  /**
   * Snapshot of the OPK pool — `held` is the total OPKs currently in
   * memory (un-issued + issued-but-not-consumed); `available` is the
   * un-issued subset that the next bundle call would draw from. Exposed
   * for tests and observability.
   */
  getOpkPoolStatus(): OpkPoolStatus {
    return {
      held: this.preKeys.oneTimePreKeys.size,
      available: this.preKeys.availableOpkIds.length,
    };
  }

  /** Sets the local node's UHID. Required before any encrypt() call. */
  setLocalUhid(uhid: string): void {
    if (!uhid) throw new Error("uhid cannot be empty");
    const changed = this.localUhid !== uhid;
    this.localUhid = uhid;
    if (changed) this.persistIdentity();
  }

  hasSession(peerUhid: string): boolean {
    return this.sessions.has(peerUhid);
  }

  async encrypt(peerUhid: string, plaintext: Uint8Array): Promise<EncryptedPayload> {
    await this.hydration;
    const session = this.sessions.get(peerUhid);
    if (!session) {
      throw new Error(`No session established with peer ${peerUhid}`);
    }
    if (!this.localUhid) {
      throw new Error(
        "Local UHID is not set. Call generatePreKeyBundle(uhid) or setLocalUhid(uhid) before encrypting."
      );
    }

    // Lazy CKs initialization for the initiator's first send: X3DH placed
    // DHs and DHr but did not derive CKs (the Double Ratchet defers it
    // until first send to avoid an extra KDF step when no message is ever
    // sent on a session).
    if (session.sendChainKey === null) {
      if (session.remoteEphemeralPub === null) {
        throw new Error("Cannot derive sending chain: peer's ratchet public key is unknown.");
      }
      this.dhRatchetSendOnly(session, session.remoteEphemeralPub);
    }

    const { newChainKey, messageKey } = ratchetStep(session.sendChainKey!);
    session.sendChainKey = newChainKey;

    const nonce = randomBytes(AES_GCM_NONCE_SIZE);
    const cipher = createCipheriv("aes-256-gcm", messageKey, nonce);
    const ct = cipher.update(plaintext);
    const finalCt = Buffer.concat([ct, cipher.final()]);
    const tag = cipher.getAuthTag();
    const combined = Buffer.concat([finalCt, tag]);

    const counter = session.sendCounter++;
    const ratchetPub = new Uint8Array(session.myEphemeralPub);
    messageKey.fill(0);

    const base: EncryptedPayload = {
      ciphertext: new Uint8Array(combined),
      nonce: new Uint8Array(nonce),
      messageType: MESSAGE_TYPE_NORMAL,
      senderUhid: this.localUhid,
      counter,
      encryptedAt: new Date(),
      senderEphemeralKeyX25519: ratchetPub,
      previousChainCount: session.previousChainCount,
    };

    if (session.pendingPreKeyMessage) {
      const payload: EncryptedPayload = {
        ...base,
        messageType: MESSAGE_TYPE_PRE_KEY,
        initiatorIdentityKeyX25519: new Uint8Array(session.initiatorIdentityKeyX25519),
        // Backward-compat: equals senderEphemeralKeyX25519 on PreKey msgs
        // because the initiator's X3DH ephemeral becomes its first DH-ratchet pub.
        initiatorEphemeralKeyX25519: new Uint8Array(ratchetPub),
        usedSignedPreKeyId: session.usedSignedPreKeyId,
        usedOneTimePreKeyId: session.usedOneTimePreKeyId,
      };
      session.pendingPreKeyMessage = false;
      this.persistSession(peerUhid, session);
      return payload;
    }

    this.persistSession(peerUhid, session);
    return base;
  }

  async decrypt(peerUhid: string, payload: EncryptedPayload): Promise<Uint8Array> {
    await this.hydration;
    // Every Double-Ratchet message carries the sender's current ratchet
    // public key. Fall back to initiatorEphemeralKeyX25519 for backward
    // compatibility with older PreKey messages from peers that haven't
    // upgraded to the new wire envelope.
    const senderRatchetPub =
      payload.senderEphemeralKeyX25519 ?? payload.initiatorEphemeralKeyX25519;

    // PreKey message? Establish the responder-side session via mirrored X3DH.
    if (payload.messageType === MESSAGE_TYPE_PRE_KEY) {
      if (!payload.initiatorIdentityKeyX25519 || !senderRatchetPub) {
        throw new Error(
          "PreKey message missing initiator key material " +
            "(initiatorIdentityKeyX25519 and senderEphemeralKeyX25519 / initiatorEphemeralKeyX25519)."
        );
      }
      this.establishResponderSession(peerUhid, payload, senderRatchetPub);
    }

    const session = this.sessions.get(peerUhid);
    if (!session) {
      throw new Error(`No session established with peer ${peerUhid}`);
    }

    if (!senderRatchetPub) {
      throw new Error("Message missing senderEphemeralKeyX25519 — required for the Double Ratchet.");
    }

    // DH-ratchet step? Triggered when the peer's ratchet public key changes.
    if (
      session.remoteEphemeralPub === null ||
      !constantTimeEquals(senderRatchetPub, session.remoteEphemeralPub)
    ) {
      // First, derive any skipped keys from the previous receive chain
      // (the chain keyed by the OLD remoteEphemeralPub). Then ratchet.
      this.skipMessageKeys(session, payload.previousChainCount ?? 0);
      this.dhRatchetReceive(session, senderRatchetPub);
    }

    if (payload.ciphertext.length < AES_GCM_TAG_SIZE) {
      throw new Error("Ciphertext too short");
    }

    let messageKey: Uint8Array;
    // Skipped key cached for this (DHr_pub, counter) pair?
    const cacheKey = skippedKey(senderRatchetPub, payload.counter);
    const cached = session.skippedMessageKeys.get(cacheKey);
    if (cached) {
      session.skippedMessageKeys.delete(cacheKey);
      messageKey = cached;
    } else {
      if (session.recvChainKey === null) {
        throw new Error("Receive chain not initialized (DH-ratchet step missing).");
      }

      const gap = payload.counter - session.recvCounter;
      if (gap > MAX_SKIPPED_KEYS) {
        throw new Error(
          `Message counter gap (${gap}) exceeds maximum (${MAX_SKIPPED_KEYS}). Session must be re-established.`
        );
      }

      // Skip ahead, caching intermediate keys keyed by (current DHr, counter).
      while (session.recvCounter < payload.counter) {
        const step = ratchetStep(session.recvChainKey);
        session.recvChainKey = step.newChainKey;
        session.skippedMessageKeys.set(
          skippedKey(senderRatchetPub, session.recvCounter),
          step.messageKey
        );
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

    this.persistSession(peerUhid, session);
    return new Uint8Array(plaintext);
  }

  async generatePreKeyBundle(localUhid: string): Promise<PreKeyBundle> {
    if (!localUhid) throw new Error("localUhid cannot be empty");
    await this.hydration;

    const uhidChanged = this.localUhid !== localUhid;
    this.localUhid = localUhid;
    if (uhidChanged) this.persistIdentity();

    // Serialise OPK-pool mutations: chain this call onto opkLock so two
    // concurrent generatePreKeyBundle awaits cannot interleave their pool
    // mutations. Single-threaded JS still permits this kind of race because
    // await is a yield point.
    return this.runUnderOpkLock(() => this.generatePreKeyBundleInner(localUhid));
  }

  /**
   * Forces a signed-pre-key rotation if the active SPK is older than
   * {@link SignedPreKeyRotationOptions.rotationIntervalMs}, OR if no
   * SPK has ever been generated. Returns true iff a new SPK was
   * generated and persisted.
   */
  async rotateSignedPreKey(): Promise<boolean> {
    await this.hydration;
    return this.runUnderOpkLock(() => {
      const history = this.preKeys.signedPreKeyHistory;
      const shouldRotate =
        history.length === 0 ||
        this.nowProvider().getTime() - history[history.length - 1].generatedAtUnixMs >=
          this.rotationOptions.rotationIntervalMs;
      if (!shouldRotate) return false;
      this.appendNewSignedPreKey();
      this.persistSignedPreKeys();
      return true;
    });
  }

  /**
   * Active signed-pre-key id. 0 if none has been generated yet.
   * Exposed for tests and observability.
   */
  get activeSignedPreKeyId(): number {
    return this.preKeys.signedPreKeyId;
  }

  /** Number of signed-pre-keys held — active + retained prior. */
  get signedPreKeyHistoryCount(): number {
    return this.preKeys.signedPreKeyHistory.length;
  }

  /**
   * Runs `body` while holding the OPK lock. The lock is a Promise chain —
   * we wait for the previous holder to complete (success or failure does
   * not matter) and then take the slot ourselves.
   */
  private runUnderOpkLock<T>(body: () => Promise<T> | T): Promise<T> {
    const prev = this.opkLock;
    let resolve!: () => void;
    this.opkLock = new Promise<void>((r) => { resolve = r; });
    return prev
      .catch(() => undefined)
      .then(async () => {
        try {
          return await body();
        } finally {
          resolve();
        }
      });
  }

  private async generatePreKeyBundleInner(localUhid: string): Promise<PreKeyBundle> {
    // SignedPreKey: generated lazily on the first bundle call. On
    // subsequent calls the active SPK is reused unless its age exceeds
    // rotationIntervalMs, in which case a fresh SPK is generated and
    // the history is rolled forward. The retained history (configurable)
    // lets messages signed under a recently-rotated SPK still complete
    // X3DH during the rotation window.
    let historyMutated = false;
    if (this.preKeys.signedPreKeyHistory.length === 0) {
      this.appendNewSignedPreKey();
      historyMutated = true;
    } else {
      const active = this.preKeys.signedPreKeyHistory[this.preKeys.signedPreKeyHistory.length - 1];
      const ageMs = this.nowProvider().getTime() - active.generatedAtUnixMs;
      if (ageMs >= this.rotationOptions.rotationIntervalMs) {
        this.appendNewSignedPreKey();
        historyMutated = true;
      }
    }

    const active = this.preKeys.signedPreKeyHistory[this.preKeys.signedPreKeyHistory.length - 1];
    const signedPreKeyId = active.id;
    const spkPub = active.publicKey;
    const signature = active.signature;

    // Top up the OPK pool to {@link opkPoolSize} un-issued entries, then
    // pop the next un-issued OPK off the front of the FIFO queue.
    this.topUpOpkPool();
    const preKeyId = this.preKeys.availableOpkIds.shift();
    if (preKeyId === undefined) {
      // Should be unreachable — topUpOpkPool always brings the queue up to
      // opkPoolSize >= 1. Defensive throw keeps the type-checker happy.
      throw new Error("OPK pool top-up failed to produce an available id.");
    }
    const otpk = this.preKeys.oneTimePreKeys.get(preKeyId)!;

    if (historyMutated) this.persistSignedPreKeys();
    this.persistOneTimePreKeys();

    return {
      uhid: localUhid,
      identityKey: new Uint8Array(this.ed25519PublicKey),
      identityKeyX25519: new Uint8Array(this.identityX25519Pub),
      preKeyId,
      preKey: new Uint8Array(otpk.pub),
      signedPreKeyId,
      signedPreKey: new Uint8Array(spkPub),
      signedPreKeySignature: signature,
    };
  }

  /**
   * Generates a fresh SPK, appends it to the history as the new active
   * entry, and trims the history to the retained-count budget. Caller
   * MUST hold {@link opkLock} (which serialises pre-key state mutations).
   */
  private appendNewSignedPreKey(): void {
    const spk = generateX25519KeyPair();
    const id = randomPositiveInt32();
    const sig = Ed25519Service.sign(this.ed25519PrivateKey, spk.pub);
    const entry: SignedPreKeyEntry = {
      id,
      privateKey: spk.priv,
      publicKey: spk.pub,
      signature: sig,
      generatedAtUnixMs: this.nowProvider().getTime(),
    };
    this.preKeys.signedPreKeyHistory.push(entry);

    const maxEntries = 1 + this.rotationOptions.retainedHistoryCount;
    while (this.preKeys.signedPreKeyHistory.length > maxEntries) {
      const pruned = this.preKeys.signedPreKeyHistory.shift()!;
      // Best-effort scrub of the pruned private half.
      pruned.privateKey.fill(0);
    }

    const active = this.preKeys.signedPreKeyHistory[this.preKeys.signedPreKeyHistory.length - 1];
    this.preKeys.signedPreKeyId = active.id;
    this.preKeys.signedPreKeyPriv = active.privateKey;
    this.preKeys.signedPreKeyPub = active.publicKey;
    this.preKeys.signedPreKeySignature = active.signature;
  }

  /**
   * Tops the OPK pool up to {@link opkPoolSize} un-issued (available) keys.
   * Generates a fresh X25519 keypair per missing slot, assigns it a random
   * non-colliding 31-bit positive id, and enqueues the id in
   * {@link PreKeyState.availableOpkIds}. Idempotent — safe to call repeatedly.
   *
   * Caller MUST hold {@link opkLock}.
   */
  private topUpOpkPool(): void {
    while (this.preKeys.availableOpkIds.length < this.opkPoolSize) {
      const { priv, pub } = generateX25519KeyPair();
      // Choose a non-colliding id. randomPositiveInt32 is 31-bit so
      // collisions in a 100-element pool are statistically negligible
      // (~10^-7 birthday after 100k allocations) — we still guard explicitly.
      let id = randomPositiveInt32();
      let attempts = 0;
      while (this.preKeys.oneTimePreKeys.has(id)) {
        id = randomPositiveInt32();
        if (++attempts > 64) {
          throw new Error(
            "Could not allocate a non-colliding OPK id after 64 attempts. " +
              "Pool exhaustion or RNG failure."
          );
        }
      }
      this.preKeys.oneTimePreKeys.set(id, { priv, pub });
      this.preKeys.availableOpkIds.push(id);
    }
  }

  /**
   * Establishes an initiator-side session against a pre-key bundle: runs
   * the four X3DH DHs (Signal §3.3) over X25519, derives the root key, and
   * primes the Double Ratchet by adopting the X3DH ephemeral as the
   * initiator's first DHs. The peer's signed pre-key becomes the initial
   * DHr. The first encrypt() after this returns a PreKey message
   * (messageType=1).
   */
  async processPreKeyBundle(bundle: PreKeyBundle): Promise<void> {
    await this.hydration;
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

    // Signal-canonical X3DH↔Double-Ratchet integration: the initiator's
    // X3DH ephemeral becomes its first DHs. The peer's signed pre-key is
    // the initial DHr. CKs is computed lazily on first send
    // (dhRatchetSendOnly).
    const session: SignalSession = {
      rootKey,
      sendChainKey: null,                                  // computed on first send
      recvChainKey: null,                                  // computed on first DH-ratchet receive
      sendCounter: 0,
      recvCounter: 0,
      previousChainCount: 0,
      myEphemeralPriv: ek.priv,
      myEphemeralPub: ek.pub,
      remoteEphemeralPub: new Uint8Array(bundle.signedPreKey),
      skippedMessageKeys: new Map(),
      pendingPreKeyMessage: true,
      initiatorIdentityKeyX25519: new Uint8Array(this.identityX25519Pub),
      usedSignedPreKeyId: bundle.signedPreKeyId,
      usedOneTimePreKeyId: bundle.preKeyId,
    };
    this.sessions.set(bundle.uhid, session);
    this.persistSession(bundle.uhid, session);

    // Best-effort scrubbing.
    shared.fill(0);
    dh1.fill(0);
    dh2.fill(0);
    dh3.fill(0);
    dh4.fill(0);
  }

  /**
   * Establishes the responder-side session when a PreKey message arrives.
   * Runs mirror X3DH to derive the same root key. Adopts the signed
   * pre-key (private + public) as the responder's initial DHs;
   * remoteEphemeralPub is left null so the very first decrypt
   * (immediately after this call) triggers a DH-ratchet step that rotates
   * DHs to a fresh keypair.
   */
  private establishResponderSession(
    peerUhid: string,
    payload: EncryptedPayload,
    initiatorRatchetPub: Uint8Array
  ): void {
    const ik = payload.initiatorIdentityKeyX25519!;
    if (ik.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Initiator IK_X25519 wrong size: ${ik.length}`);
    }
    if (initiatorRatchetPub.length !== X25519_PUBLIC_KEY_SIZE) {
      throw new Error(`Initiator ratchet pub wrong size: ${initiatorRatchetPub.length}`);
    }
    // Walk the FULL SPK history (active + retained prior). A pruned
    // SPK fails outright because its private half has been zeroed and
    // dropped from the history when it aged out.
    const spkId = payload.usedSignedPreKeyId ?? 0;
    const spkEntry = this.findSignedPreKey(spkId);
    if (!spkEntry || spkEntry.privateKey.length === 0) {
      throw new Error(
        `PreKey message references signed pre-key id ${spkId} which is not held by this node (rotated out or never generated).`
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
    const dh1 = x25519Agree(spkEntry.privateKey, ik);
    const dh2 = x25519Agree(this.identityX25519Priv, initiatorRatchetPub);
    const dh3 = x25519Agree(spkEntry.privateKey, initiatorRatchetPub);
    const dh4 = x25519Agree(otpk.priv, initiatorRatchetPub);

    const shared = concat(dh1, dh2, dh3, dh4);
    const rootKey = hkdf32(shared, HKDF_ROOT_INFO);

    // Adopt SPK as the initial DHs. The DH-ratchet step that follows
    // (forced by remoteEphemeralPub=null on the upcoming decrypt) will
    // rotate it to a fresh keypair.
    const newSession: SignalSession = {
      rootKey,
      sendChainKey: null,
      recvChainKey: null,
      sendCounter: 0,
      recvCounter: 0,
      previousChainCount: 0,
      myEphemeralPriv: new Uint8Array(spkEntry.privateKey),
      myEphemeralPub: new Uint8Array(spkEntry.publicKey),
      remoteEphemeralPub: null,                            // forces DH-ratchet on first decrypt
      skippedMessageKeys: new Map(),
      pendingPreKeyMessage: false,
      initiatorIdentityKeyX25519: new Uint8Array(),
      usedSignedPreKeyId: 0,
      usedOneTimePreKeyId: 0,
    };
    this.sessions.set(peerUhid, newSession);
    this.persistSession(peerUhid, newSession);

    // Consume one-time pre-key — never reuse.
    otpk.priv.fill(0);
    this.preKeys.oneTimePreKeys.delete(opkId);
    this.consumeOneTimePreKey(opkId);

    shared.fill(0);
    dh1.fill(0);
    dh2.fill(0);
    dh3.fill(0);
    dh4.fill(0);
  }

  /**
   * Looks up a signed-pre-key entry by id across the full retained
   * history. Returns null if the id is unknown (rotated-out and pruned,
   * or never generated).
   */
  private findSignedPreKey(id: number): SignedPreKeyEntry | null {
    for (let i = this.preKeys.signedPreKeyHistory.length - 1; i >= 0; i--) {
      const entry = this.preKeys.signedPreKeyHistory[i];
      if (entry.id === id) return entry;
    }
    return null;
  }

  /**
   * Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
   * derives a new receiving chain via KDF_RK(RK, DH(DHs, DHr)), generates
   * a fresh DHs, and derives a new sending chain via
   * KDF_RK(RK, DH(newDHs, DHr)).
   */
  private dhRatchetReceive(session: SignalSession, newRemoteEphemeralPub: Uint8Array): void {
    // Save send-counter as PN so the peer can compute skipped keys across
    // the ratchet boundary on subsequent decrypts.
    session.previousChainCount = session.sendCounter;
    session.sendCounter = 0;
    session.recvCounter = 0;
    session.remoteEphemeralPub = new Uint8Array(newRemoteEphemeralPub);

    // Step 1: derive new receiving chain from current DHs · new DHr.
    const dh1 = x25519Agree(session.myEphemeralPriv, session.remoteEphemeralPub);
    const r1 = kdfRk(session.rootKey, dh1);
    session.rootKey = r1.newRootKey;
    session.recvChainKey = r1.newChainKey;
    dh1.fill(0);

    // Step 2: rotate DHs to a fresh keypair, derive new sending chain
    // from new DHs · new DHr.
    session.myEphemeralPriv.fill(0);
    const fresh = generateX25519KeyPair();
    session.myEphemeralPriv = fresh.priv;
    session.myEphemeralPub = fresh.pub;

    const dh2 = x25519Agree(session.myEphemeralPriv, session.remoteEphemeralPub);
    const r2 = kdfRk(session.rootKey, dh2);
    session.rootKey = r2.newRootKey;
    session.sendChainKey = r2.newChainKey;
    dh2.fill(0);
  }

  /**
   * Lazy half-ratchet for the very first send on a freshly-established
   * initiator session. The initiator's DHs and DHr are already set (X3DH
   * placed them); we just need to derive the sending chain. We do NOT
   * rotate DHs here — only on a true DH-ratchet (i.e. on receive).
   */
  private dhRatchetSendOnly(session: SignalSession, remotePub: Uint8Array): void {
    const dh = x25519Agree(session.myEphemeralPriv, remotePub);
    const { newRootKey, newChainKey } = kdfRk(session.rootKey, dh);
    session.rootKey = newRootKey;
    session.sendChainKey = newChainKey;
    dh.fill(0);
  }

  /**
   * Saves any unread message keys on the current receive chain up to the
   * given counter, so they can be consumed if those messages eventually
   * arrive after a DH-ratchet step. Bounded by MAX_SKIPPED_KEYS.
   */
  private skipMessageKeys(session: SignalSession, until: number): void {
    if (session.recvChainKey === null || session.remoteEphemeralPub === null) {
      return; // no chain to skip on
    }
    if (until <= session.recvCounter) return;
    if (until - session.recvCounter > MAX_SKIPPED_KEYS) {
      throw new Error(
        `Skipped-key request exceeds maximum (${MAX_SKIPPED_KEYS}). Session must be re-established.`
      );
    }

    while (session.recvCounter < until) {
      const step = ratchetStep(session.recvChainKey);
      session.recvChainKey = step.newChainKey;
      session.skippedMessageKeys.set(
        skippedKey(session.remoteEphemeralPub, session.recvCounter),
        step.messageKey
      );
      session.recvCounter++;
    }
  }

  getPublicKey(): Uint8Array {
    return new Uint8Array(this.ed25519PublicKey);
  }

  getX25519PublicKey(): Uint8Array {
    return new Uint8Array(this.identityX25519Pub);
  }
}
