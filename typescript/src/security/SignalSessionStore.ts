/**
 * Persistent storage for Signal-Protocol session state, plus its in-memory
 * and KV-backed reference implementations. Each session is keyed by the
 * peer's UHID. Implementations are responsible for atomicity and
 * durability — the protocol layer hands an opaque {@link StoredSignalSession}
 * in and trusts that {@link SignalSessionStore.load} later returns the
 * exact same state (or null if no session was previously stored).
 *
 * Mirrors the C# {@code ISignalSessionStore} interface and its companions
 * in src/AetherNet.Security/Services/ISecurityServices.cs (with persistence
 * adapters in src/AetherNet.Storage/KeyValueSignalSessionStore.cs).
 *
 * SPDX-License-Identifier: MIT
 */
import { KeyValueStore } from "../storage/KeyValueStore.js";

/**
 * Snapshot of one Signal-Protocol session — both X3DH session-establishment
 * metadata and full Double-Ratchet state. Field meanings mirror the
 * internal {@code SignalSession} struct in {@code SignalProtocol.ts}.
 *
 * The on-disk format is a JSON envelope of these fields with stable
 * short keys (matching the C# {@code SignalSessionDto}). New fields can be
 * added at the end without breaking previously stored snapshots; existing
 * fields must never change shape.
 */
export interface StoredSignalSession {
  rootKey: Uint8Array;
  sendChainKey: Uint8Array | null;
  recvChainKey: Uint8Array | null;

  sendCounter: number;
  recvCounter: number;
  /** PN — messages sent in the previous sending chain. */
  previousChainCount: number;

  myEphemeralPriv: Uint8Array;
  myEphemeralPub: Uint8Array;
  remoteEphemeralPub: Uint8Array | null;

  /** Skipped message keys keyed by "Hex(remoteEphPub):counter". */
  skippedMessageKeys: Map<string, Uint8Array>;

  pendingPreKeyMessage: boolean;
  initiatorIdentityKeyX25519: Uint8Array;
  usedSignedPreKeyId: number;
  usedOneTimePreKeyId: number;
}

/**
 * Persistent storage for {@link StoredSignalSession}s. Every encrypt /
 * decrypt mutation triggers a save when a store is configured.
 */
export interface SignalSessionStore {
  load(peerUhid: string): Promise<StoredSignalSession | null>;
  save(peerUhid: string, session: StoredSignalSession): Promise<void>;
  delete(peerUhid: string): Promise<void>;
  listPeers(): Promise<string[]>;
}

// ─── JSON envelope ────────────────────────────────────────────────────────

interface SignalSessionJson {
  /** rk — root key (base64). */
  rk: string;
  /** cks — sending chain key (base64), or null. */
  cks: string | null;
  /** ckr — receiving chain key (base64), or null. */
  ckr: string | null;
  ns: number;
  nr: number;
  pn: number;
  dhs_priv: string;
  dhs_pub: string;
  dhr: string | null;
  /** mkskipped — map of "hexPub:counter" to base64 message key. */
  mkskipped: Record<string, string>;
  pending_pkmsg: boolean;
  init_ik: string;
  used_spk_id: number;
  used_opk_id: number;
}

function bytesToB64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

function b64ToBytes(s: string): Uint8Array {
  return new Uint8Array(Buffer.from(s, "base64"));
}

export function serializeSignalSession(session: StoredSignalSession): Uint8Array {
  if (!session) throw new Error("session must not be null");

  const skipped: Record<string, string> = {};
  for (const [k, v] of session.skippedMessageKeys.entries()) {
    skipped[k] = bytesToB64(v);
  }

  const json: SignalSessionJson = {
    rk: bytesToB64(session.rootKey),
    cks: session.sendChainKey ? bytesToB64(session.sendChainKey) : null,
    ckr: session.recvChainKey ? bytesToB64(session.recvChainKey) : null,
    ns: session.sendCounter,
    nr: session.recvCounter,
    pn: session.previousChainCount,
    dhs_priv: bytesToB64(session.myEphemeralPriv),
    dhs_pub: bytesToB64(session.myEphemeralPub),
    dhr: session.remoteEphemeralPub ? bytesToB64(session.remoteEphemeralPub) : null,
    mkskipped: skipped,
    pending_pkmsg: session.pendingPreKeyMessage,
    init_ik: bytesToB64(session.initiatorIdentityKeyX25519),
    used_spk_id: session.usedSignedPreKeyId,
    used_opk_id: session.usedOneTimePreKeyId,
  };
  return new Uint8Array(Buffer.from(JSON.stringify(json), "utf8"));
}

export function deserializeSignalSession(bytes: Uint8Array): StoredSignalSession | null {
  if (!bytes || bytes.length === 0) return null;
  let parsed: any;
  try {
    parsed = JSON.parse(Buffer.from(bytes).toString("utf8"));
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== "object") return null;

  const skipped = new Map<string, Uint8Array>();
  if (parsed.mkskipped && typeof parsed.mkskipped === "object") {
    for (const [k, v] of Object.entries(parsed.mkskipped)) {
      if (typeof v === "string") skipped.set(k, b64ToBytes(v));
    }
  }

  return {
    rootKey: b64ToBytes(parsed.rk ?? ""),
    sendChainKey: typeof parsed.cks === "string" ? b64ToBytes(parsed.cks) : null,
    recvChainKey: typeof parsed.ckr === "string" ? b64ToBytes(parsed.ckr) : null,
    sendCounter: typeof parsed.ns === "number" ? parsed.ns : 0,
    recvCounter: typeof parsed.nr === "number" ? parsed.nr : 0,
    previousChainCount: typeof parsed.pn === "number" ? parsed.pn : 0,
    myEphemeralPriv: b64ToBytes(parsed.dhs_priv ?? ""),
    myEphemeralPub: b64ToBytes(parsed.dhs_pub ?? ""),
    remoteEphemeralPub: typeof parsed.dhr === "string" ? b64ToBytes(parsed.dhr) : null,
    skippedMessageKeys: skipped,
    pendingPreKeyMessage: !!parsed.pending_pkmsg,
    initiatorIdentityKeyX25519: b64ToBytes(parsed.init_ik ?? ""),
    usedSignedPreKeyId: typeof parsed.used_spk_id === "number" ? parsed.used_spk_id : 0,
    usedOneTimePreKeyId: typeof parsed.used_opk_id === "number" ? parsed.used_opk_id : 0,
  };
}

// ─── Implementations ──────────────────────────────────────────────────────

/**
 * Process-local, volatile {@link SignalSessionStore} backed by a plain
 * {@link Map}. The session bytes are stored as the same JSON envelope a
 * durable store would emit, which keeps the round-trip path identical to
 * the production code path and makes accidental in-place mutation of
 * stored state impossible.
 */
export class InMemorySignalSessionStore implements SignalSessionStore {
  private readonly sessions: Map<string, Uint8Array> = new Map();

  async load(peerUhid: string): Promise<StoredSignalSession | null> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    const bytes = this.sessions.get(peerUhid);
    if (!bytes) return null;
    return deserializeSignalSession(bytes);
  }

  async save(peerUhid: string, session: StoredSignalSession): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    if (!session) throw new Error("session cannot be null");
    this.sessions.set(peerUhid, serializeSignalSession(session));
  }

  async delete(peerUhid: string): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    this.sessions.delete(peerUhid);
  }

  async listPeers(): Promise<string[]> {
    return Array.from(this.sessions.keys());
  }
}

/**
 * {@link SignalSessionStore} implementation backed by an arbitrary
 * {@link KeyValueStore}. Sessions are JSON-encoded under
 * {@code signal:session:<peerUhid>}. Hosts that want a different on-disk
 * format (sqlite, encrypted-at-rest) wrap the inner store with
 * {@code EncryptedKeyValueStore}, or supply a different
 * {@link SignalSessionStore} implementation outright.
 */
export class KeyValueSignalSessionStore implements SignalSessionStore {
  static readonly KEY_PREFIX = "signal:session:";
  private readonly kv: KeyValueStore;

  constructor(kv: KeyValueStore) {
    if (!kv) throw new Error("kv cannot be null");
    this.kv = kv;
  }

  async load(peerUhid: string): Promise<StoredSignalSession | null> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    const bytes = await this.kv.get(this.key(peerUhid));
    return bytes === null ? null : deserializeSignalSession(bytes);
  }

  async save(peerUhid: string, session: StoredSignalSession): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    if (!session) throw new Error("session cannot be null");
    const bytes = serializeSignalSession(session);
    await this.kv.put(this.key(peerUhid), bytes);
  }

  async delete(peerUhid: string): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid cannot be empty");
    await this.kv.remove(this.key(peerUhid));
  }

  async listPeers(): Promise<string[]> {
    const keys = await this.kv.listKeys(KeyValueSignalSessionStore.KEY_PREFIX);
    return keys.map((k) => k.substring(KeyValueSignalSessionStore.KEY_PREFIX.length));
  }

  private key(peerUhid: string): string {
    return KeyValueSignalSessionStore.KEY_PREFIX + peerUhid;
  }
}
