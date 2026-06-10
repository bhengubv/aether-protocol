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
import { PreKeyStore } from "./PreKeyStore.js";
import { SignalSessionStore } from "./SignalSessionStore.js";
export declare const MESSAGE_TYPE_NORMAL = 0;
export declare const MESSAGE_TYPE_PRE_KEY = 1;
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
export declare const DEFAULT_SPK_ROTATION_OPTIONS: SignedPreKeyRotationOptions;
/** Default size of the one-time pre-key pool (matches C# DefaultOpkPoolSize). */
export declare const DEFAULT_OPK_POOL_SIZE = 100;
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
export declare class SignalProtocol {
    private sessions;
    private identityX25519Priv;
    private identityX25519Pub;
    private ed25519PrivateKey;
    private ed25519PublicKey;
    private localUhid;
    /**
  
     * Target pool size — top-up to this many un-issued OPKs on each
  
     * generatePreKeyBundle call. Defaults to {@link DEFAULT_OPK_POOL_SIZE}.
  
     */
    readonly opkPoolSize: number;
    private preKeys;
    /**
  
     * Promise-chain serialising mutations against the OPK pool. JS is
  
     * single-threaded but `await` interleaves between Promises, so two
  
     * concurrent generatePreKeyBundle calls could otherwise observe each
  
     * other's partial state. We chain them so each completes atomically.
  
     */
    private opkLock;
    private readonly sessionStore?;
    private readonly preKeyStore?;
    private readonly rotationOptions;
    private readonly nowProvider;
    private readonly onPersistenceError;
    /**
  
     * Promise that resolves once the constructor's hydration pass has
  
     * finished. Every async public method awaits this before touching
  
     * state so a fresh instance handed to encrypt() before its stores
  
     * have loaded behaves identically to one whose hydration completed
  
     * first.
  
     */
    private readonly hydration;
    constructor(options?: SignalProtocolOptions);
    /**
  
     * Returns a promise that resolves once the constructor's hydration
  
     * pass (loading identity, SPK history, OPK pool, sessions from the
  
     * configured stores) has completed. Tests that want to assert state
  
     * immediately after construction should await this. Public async
  
     * methods already await internally — callers can ignore this for
  
     * normal use.
  
     */
    ready(): Promise<void>;
    /**
  
     * Loads persisted identity, SPK history, OPK pool, and active
  
     * sessions from the configured stores. Best-effort: any failure
  
     * surfaces via {@link onPersistenceError} but never throws — the
  
     * caller continues with the freshly-generated identity.
  
     */
    private hydrate;
    /**
  
     * Promise chain tracking every in-flight fire-and-forget save. Fresh
  
     * write Promises are appended; tests / hosts can {@link flushPendingWrites}
  
     * to wait for the chain to settle before snapshotting state. Saves
  
     * are best-effort: errors are routed through {@link onPersistenceError}
  
     * but never reject the chain.
  
     */
    private pendingWrites;
    /**
  
     * Awaits every fire-and-forget persistence write started up to the
  
     * current call. Useful in tests that need to assert the underlying
  
     * store contents, or for hosts implementing graceful shutdown.
  
     */
    flushPendingWrites(): Promise<void>;
    private trackWrite;
    private persistSession;
    private persistIdentity;
    private persistSignedPreKeys;
    private persistOneTimePreKeys;
    private consumeOneTimePreKey;
    private toStoredSession;
    private fromStoredSession;
    /**
  
     * Snapshot of the OPK pool — `held` is the total OPKs currently in
  
     * memory (un-issued + issued-but-not-consumed); `available` is the
  
     * un-issued subset that the next bundle call would draw from. Exposed
  
     * for tests and observability.
  
     */
    getOpkPoolStatus(): OpkPoolStatus;
    /** Sets the local node's UHID. Required before any encrypt() call. */
    setLocalUhid(uhid: string): void;
    hasSession(peerUhid: string): boolean;
    encrypt(peerUhid: string, plaintext: Uint8Array): Promise<EncryptedPayload>;
    decrypt(peerUhid: string, payload: EncryptedPayload): Promise<Uint8Array>;
    generatePreKeyBundle(localUhid: string): Promise<PreKeyBundle>;
    /**
  
     * Forces a signed-pre-key rotation if the active SPK is older than
  
     * {@link SignedPreKeyRotationOptions.rotationIntervalMs}, OR if no
  
     * SPK has ever been generated. Returns true iff a new SPK was
  
     * generated and persisted.
  
     */
    rotateSignedPreKey(): Promise<boolean>;
    /**
  
     * Active signed-pre-key id. 0 if none has been generated yet.
  
     * Exposed for tests and observability.
  
     */
    get activeSignedPreKeyId(): number;
    /** Number of signed-pre-keys held — active + retained prior. */
    get signedPreKeyHistoryCount(): number;
    /**
  
     * Runs `body` while holding the OPK lock. The lock is a Promise chain —
  
     * we wait for the previous holder to complete (success or failure does
  
     * not matter) and then take the slot ourselves.
  
     */
    private runUnderOpkLock;
    private generatePreKeyBundleInner;
    /**
  
     * Generates a fresh SPK, appends it to the history as the new active
  
     * entry, and trims the history to the retained-count budget. Caller
  
     * MUST hold {@link opkLock} (which serialises pre-key state mutations).
  
     */
    private appendNewSignedPreKey;
    /**
  
     * Tops the OPK pool up to {@link opkPoolSize} un-issued (available) keys.
  
     * Generates a fresh X25519 keypair per missing slot, assigns it a random
  
     * non-colliding 31-bit positive id, and enqueues the id in
  
     * {@link PreKeyState.availableOpkIds}. Idempotent — safe to call repeatedly.
  
     *
  
     * Caller MUST hold {@link opkLock}.
  
     */
    private topUpOpkPool;
    /**
  
     * Establishes an initiator-side session against a pre-key bundle: runs
  
     * the four X3DH DHs (Signal §3.3) over X25519, derives the root key, and
  
     * primes the Double Ratchet by adopting the X3DH ephemeral as the
  
     * initiator's first DHs. The peer's signed pre-key becomes the initial
  
     * DHr. The first encrypt() after this returns a PreKey message
  
     * (messageType=1).
  
     */
    processPreKeyBundle(bundle: PreKeyBundle): Promise<void>;
    /**
  
     * Establishes the responder-side session when a PreKey message arrives.
  
     * Runs mirror X3DH to derive the same root key. Adopts the signed
  
     * pre-key (private + public) as the responder's initial DHs;
  
     * remoteEphemeralPub is left null so the very first decrypt
  
     * (immediately after this call) triggers a DH-ratchet step that rotates
  
     * DHs to a fresh keypair.
  
     */
    private establishResponderSession;
    /**
  
     * Looks up a signed-pre-key entry by id across the full retained
  
     * history. Returns null if the id is unknown (rotated-out and pruned,
  
     * or never generated).
  
     */
    private findSignedPreKey;
    /**
  
     * Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
  
     * derives a new receiving chain via KDF_RK(RK, DH(DHs, DHr)), generates
  
     * a fresh DHs, and derives a new sending chain via
  
     * KDF_RK(RK, DH(newDHs, DHr)).
  
     */
    private dhRatchetReceive;
    /**
  
     * Lazy half-ratchet for the very first send on a freshly-established
  
     * initiator session. The initiator's DHs and DHr are already set (X3DH
  
     * placed them); we just need to derive the sending chain. We do NOT
  
     * rotate DHs here — only on a true DH-ratchet (i.e. on receive).
  
     */
    private dhRatchetSendOnly;
    /**
  
     * Saves any unread message keys on the current receive chain up to the
  
     * given counter, so they can be consumed if those messages eventually
  
     * arrive after a DH-ratchet step. Bounded by MAX_SKIPPED_KEYS.
  
     */
    private skipMessageKeys;
    getPublicKey(): Uint8Array;
    getX25519PublicKey(): Uint8Array;
}
//# sourceMappingURL=SignalProtocol.d.ts.map