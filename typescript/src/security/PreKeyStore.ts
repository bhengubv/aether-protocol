/**
 * Persistent storage for the long-term identity keys, signed-pre-key
 * history, and one-time pre-key pool that survive a process restart.
 *
 * All methods are best-effort from the caller's perspective: failures are
 * logged but never propagate up the message-flow stack. Implementations
 * are not required to be thread-safe; {@link SignalProtocol} serialises
 * access through its own pre-key lock before calling.
 *
 * Mirrors the C# {@code IPreKeyStore} interface and its companion DTOs in
 * src/AetherNet.Security/Services/ISecurityServices.cs (with persistence
 * adapters in src/AetherNet.Storage/KeyValuePreKeyStore.cs).
 *
 * SPDX-License-Identifier: MIT
 */
import { KeyValueStore } from "../storage/KeyValueStore.js";

/**
 * Long-term identity key material that survives across process restarts.
 * The Ed25519 keypair signs pre-key bundles; the X25519 keypair
 * participates in X3DH agreement. Both private halves stay on the node
 * and are never transmitted.
 *
 * {@link localUhid} is persisted alongside the keys so that
 * {@code encrypt} still works after a restart without the host having to
 * call {@code setLocalUhid} again.
 */
export interface StoredIdentityKeys {
  ed25519PrivateKey: Uint8Array;
  ed25519PublicKey: Uint8Array;
  x25519PrivateKey: Uint8Array;
  x25519PublicKey: Uint8Array;
  localUhid?: string | null;
}

/**
 * One signed-pre-key entry as stored in the SPK history. Each rotation
 * generates a new entry; the active entry is the most-recently-generated
 * one. Older entries are retained for the configured rotation window so
 * that messages signed under a recently-rotated SPK can still decrypt.
 */
export interface StoredSignedPreKey {
  id: number;
  privateKey: Uint8Array;
  publicKey: Uint8Array;
  signature: Uint8Array;
  /** Unix epoch milliseconds. */
  generatedAtUnixMs: number;
}

/**
 * Full signed-pre-key history: the active SPK plus the retained prior
 * entries in generation order (oldest first). Empty until the first
 * {@code generatePreKeyBundle} call.
 */
export interface StoredSignedPreKeyHistory {
  entries: StoredSignedPreKey[];
}

/**
 * One one-time pre-key in the pool. Removed from the store on consumption
 * (Signal §3.3 — each OPK is consumed exactly once).
 */
export interface StoredOneTimePreKey {
  id: number;
  privateKey: Uint8Array;
  publicKey: Uint8Array;
  /** True iff this OPK has been issued in a bundle but not yet consumed. */
  issued: boolean;
}

export interface PreKeyStore {
  loadIdentity(): Promise<StoredIdentityKeys | null>;
  saveIdentity(identity: StoredIdentityKeys): Promise<void>;

  loadSignedPreKeys(): Promise<StoredSignedPreKeyHistory>;
  saveSignedPreKeys(history: StoredSignedPreKeyHistory): Promise<void>;

  loadOneTimePreKeys(): Promise<Map<number, StoredOneTimePreKey>>;
  saveOneTimePreKeys(pool: Map<number, StoredOneTimePreKey>): Promise<void>;
  consumeOneTimePreKey(id: number): Promise<void>;
}

// ─── DTOs / JSON envelopes ────────────────────────────────────────────────

interface IdentityJson {
  ed_pk: string;
  ed_pub: string;
  x_pk: string;
  x_pub: string;
  uhid?: string | null;
}

interface SpkEntryJson {
  id: number;
  priv: string;
  pub: string;
  sig: string;
  at: number;
}

interface SpkHistoryJson {
  entries: SpkEntryJson[];
}

interface OpkJson {
  id: number;
  priv: string;
  pub: string;
  issued: boolean;
}

function bytesToB64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

function b64ToBytes(s: string): Uint8Array {
  return new Uint8Array(Buffer.from(s, "base64"));
}

// ─── Implementations ──────────────────────────────────────────────────────

/**
 * Process-local, volatile {@link PreKeyStore} backed by ordinary
 * reference fields. Suitable for tests and demos. Loses everything on
 * process exit.
 */
export class InMemoryPreKeyStore implements PreKeyStore {
  private identity: StoredIdentityKeys | null = null;
  private spkHistory: StoredSignedPreKeyHistory = { entries: [] };
  private opks: Map<number, StoredOneTimePreKey> = new Map();

  async loadIdentity(): Promise<StoredIdentityKeys | null> {
    return this.identity;
  }

  async saveIdentity(identity: StoredIdentityKeys): Promise<void> {
    if (!identity) throw new Error("identity cannot be null");
    // Defensive copy.
    this.identity = {
      ed25519PrivateKey: new Uint8Array(identity.ed25519PrivateKey),
      ed25519PublicKey: new Uint8Array(identity.ed25519PublicKey),
      x25519PrivateKey: new Uint8Array(identity.x25519PrivateKey),
      x25519PublicKey: new Uint8Array(identity.x25519PublicKey),
      localUhid: identity.localUhid ?? null,
    };
  }

  async loadSignedPreKeys(): Promise<StoredSignedPreKeyHistory> {
    return {
      entries: this.spkHistory.entries.map((e) => ({
        id: e.id,
        privateKey: new Uint8Array(e.privateKey),
        publicKey: new Uint8Array(e.publicKey),
        signature: new Uint8Array(e.signature),
        generatedAtUnixMs: e.generatedAtUnixMs,
      })),
    };
  }

  async saveSignedPreKeys(history: StoredSignedPreKeyHistory): Promise<void> {
    if (!history) throw new Error("history cannot be null");
    this.spkHistory = {
      entries: history.entries.map((e) => ({
        id: e.id,
        privateKey: new Uint8Array(e.privateKey),
        publicKey: new Uint8Array(e.publicKey),
        signature: new Uint8Array(e.signature),
        generatedAtUnixMs: e.generatedAtUnixMs,
      })),
    };
  }

  async loadOneTimePreKeys(): Promise<Map<number, StoredOneTimePreKey>> {
    const out = new Map<number, StoredOneTimePreKey>();
    for (const [id, opk] of this.opks.entries()) {
      out.set(id, {
        id: opk.id,
        privateKey: new Uint8Array(opk.privateKey),
        publicKey: new Uint8Array(opk.publicKey),
        issued: opk.issued,
      });
    }
    return out;
  }

  async saveOneTimePreKeys(pool: Map<number, StoredOneTimePreKey>): Promise<void> {
    if (!pool) throw new Error("pool cannot be null");
    this.opks.clear();
    for (const [id, opk] of pool.entries()) {
      this.opks.set(id, {
        id: opk.id,
        privateKey: new Uint8Array(opk.privateKey),
        publicKey: new Uint8Array(opk.publicKey),
        issued: opk.issued,
      });
    }
  }

  async consumeOneTimePreKey(id: number): Promise<void> {
    this.opks.delete(id);
  }
}

/**
 * {@link PreKeyStore} implementation backed by an arbitrary
 * {@link KeyValueStore}. Layout:
 *
 *   - {@code signal:identity}      — {@link StoredIdentityKeys} JSON
 *   - {@code signal:spk-history}   — {@link StoredSignedPreKeyHistory} JSON
 *   - {@code signal:opk:<id>}      — {@link StoredOneTimePreKey} JSON, one entry per id
 *
 * OPKs are written as one entry per id rather than one combined blob so
 * that {@link consumeOneTimePreKey} is a single
 * {@link KeyValueStore.remove} call without a read-modify-write cycle on
 * the whole pool.
 */
export class KeyValuePreKeyStore implements PreKeyStore {
  static readonly IDENTITY_KEY = "signal:identity";
  static readonly SPK_HISTORY_KEY = "signal:spk-history";
  static readonly OPK_PREFIX = "signal:opk:";

  private readonly kv: KeyValueStore;

  constructor(kv: KeyValueStore) {
    if (!kv) throw new Error("kv cannot be null");
    this.kv = kv;
  }

  async loadIdentity(): Promise<StoredIdentityKeys | null> {
    const bytes = await this.kv.get(KeyValuePreKeyStore.IDENTITY_KEY);
    if (bytes === null) return null;
    let parsed: IdentityJson;
    try {
      parsed = JSON.parse(Buffer.from(bytes).toString("utf8"));
    } catch {
      return null;
    }
    return {
      ed25519PrivateKey: b64ToBytes(parsed.ed_pk ?? ""),
      ed25519PublicKey: b64ToBytes(parsed.ed_pub ?? ""),
      x25519PrivateKey: b64ToBytes(parsed.x_pk ?? ""),
      x25519PublicKey: b64ToBytes(parsed.x_pub ?? ""),
      localUhid: parsed.uhid ?? null,
    };
  }

  async saveIdentity(identity: StoredIdentityKeys): Promise<void> {
    if (!identity) throw new Error("identity cannot be null");
    const json: IdentityJson = {
      ed_pk: bytesToB64(identity.ed25519PrivateKey),
      ed_pub: bytesToB64(identity.ed25519PublicKey),
      x_pk: bytesToB64(identity.x25519PrivateKey),
      x_pub: bytesToB64(identity.x25519PublicKey),
      uhid: identity.localUhid ?? null,
    };
    await this.kv.put(
      KeyValuePreKeyStore.IDENTITY_KEY,
      new Uint8Array(Buffer.from(JSON.stringify(json), "utf8"))
    );
  }

  async loadSignedPreKeys(): Promise<StoredSignedPreKeyHistory> {
    const bytes = await this.kv.get(KeyValuePreKeyStore.SPK_HISTORY_KEY);
    if (bytes === null) return { entries: [] };
    let parsed: SpkHistoryJson;
    try {
      parsed = JSON.parse(Buffer.from(bytes).toString("utf8"));
    } catch {
      return { entries: [] };
    }
    if (!parsed || !Array.isArray(parsed.entries)) return { entries: [] };
    return {
      entries: parsed.entries.map((e) => ({
        id: e.id,
        privateKey: b64ToBytes(e.priv ?? ""),
        publicKey: b64ToBytes(e.pub ?? ""),
        signature: b64ToBytes(e.sig ?? ""),
        generatedAtUnixMs: typeof e.at === "number" ? e.at : 0,
      })),
    };
  }

  async saveSignedPreKeys(history: StoredSignedPreKeyHistory): Promise<void> {
    if (!history) throw new Error("history cannot be null");
    const json: SpkHistoryJson = {
      entries: history.entries.map((e) => ({
        id: e.id,
        priv: bytesToB64(e.privateKey),
        pub: bytesToB64(e.publicKey),
        sig: bytesToB64(e.signature),
        at: e.generatedAtUnixMs,
      })),
    };
    await this.kv.put(
      KeyValuePreKeyStore.SPK_HISTORY_KEY,
      new Uint8Array(Buffer.from(JSON.stringify(json), "utf8"))
    );
  }

  async loadOneTimePreKeys(): Promise<Map<number, StoredOneTimePreKey>> {
    const out = new Map<number, StoredOneTimePreKey>();
    const keys = await this.kv.listKeys(KeyValuePreKeyStore.OPK_PREFIX);
    for (const k of keys) {
      const bytes = await this.kv.get(k);
      if (bytes === null) continue;
      let parsed: OpkJson;
      try {
        parsed = JSON.parse(Buffer.from(bytes).toString("utf8"));
      } catch {
        continue;
      }
      if (!parsed || typeof parsed.id !== "number") continue;
      out.set(parsed.id, {
        id: parsed.id,
        privateKey: b64ToBytes(parsed.priv ?? ""),
        publicKey: b64ToBytes(parsed.pub ?? ""),
        issued: !!parsed.issued,
      });
    }
    return out;
  }

  async saveOneTimePreKeys(pool: Map<number, StoredOneTimePreKey>): Promise<void> {
    if (!pool) throw new Error("pool cannot be null");

    const existing = new Set<number>();
    const keys = await this.kv.listKeys(KeyValuePreKeyStore.OPK_PREFIX);
    for (const k of keys) {
      const idStr = k.substring(KeyValuePreKeyStore.OPK_PREFIX.length);
      const id = parseInt(idStr, 10);
      if (Number.isInteger(id)) existing.add(id);
    }

    for (const [id, opk] of pool.entries()) {
      const json: OpkJson = {
        id: opk.id,
        priv: bytesToB64(opk.privateKey),
        pub: bytesToB64(opk.publicKey),
        issued: opk.issued,
      };
      await this.kv.put(
        this.opkKey(id),
        new Uint8Array(Buffer.from(JSON.stringify(json), "utf8"))
      );
      existing.delete(id);
    }

    for (const id of existing) {
      await this.kv.remove(this.opkKey(id));
    }
  }

  async consumeOneTimePreKey(id: number): Promise<void> {
    await this.kv.remove(this.opkKey(id));
  }

  private opkKey(id: number): string {
    return KeyValuePreKeyStore.OPK_PREFIX + id.toString(10);
  }
}
