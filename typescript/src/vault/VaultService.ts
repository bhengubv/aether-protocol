// SPDX-License-Identifier: MIT

// In-memory aether-vault service (Phase-2 extension): erasure-coded distributed
// backup over this package's ReedSolomon vault codec. Port of the C# reference
// (AetherNet.Vault.InMemoryVaultService) — K=10 / M=4, shard layout byte-identical
// so a shard set produced here is decodable by any other node.

import { createHash } from "node:crypto";
import { ReedSolomonCodec } from "./ReedSolomonCodec.js";
import { encodeData, reconstructData } from "./VaultCodec.js";

const VAULT_K = 10;
const VAULT_M = 4;

/** The only thing the owner must retain to reconstruct a vaulted file. */
export interface VaultManifest {
  contentHash: string; // SHA-256 hex of the plaintext
  shardHashes: string[]; // SHA-256 hex of each of the K+M shards
  k: number;
  m: number;
  sizeBytes: number;
  label: string;
  createdAtUtc: Date;
}

/** Total shards for a manifest (K + M). */
export function vaultTotalShards(m: VaultManifest): number {
  return m.k + m.m;
}

/** A current reachability report for a vaulted file. */
export interface VaultHealth {
  totalShards: number;
  reachableShards: number;
  isRecoverable: boolean;
  redundancyScore: number;
}

/** The aether-vault erasure-coded backup store. */
export interface IVaultService {
  store(data: Uint8Array, label: string): Promise<VaultManifest>;
  recover(manifest: VaultManifest): Promise<Uint8Array>;
  checkHealth(manifest: VaultManifest): VaultHealth;
  replicate(manifest: VaultManifest, targetRedundancy?: number): Promise<void>;
}

function sha256Hex(data: Uint8Array): string {
  return createHash("sha256").update(data).digest("hex");
}

/** In-memory IVaultService for testing / single-node use; shards lost on restart. */
export class InMemoryVaultService implements IVaultService {
  private readonly shards = new Map<string, Uint8Array>(); // shard hash -> bytes

  async store(data: Uint8Array, label: string): Promise<VaultManifest> {
    const contentHash = sha256Hex(data);
    const codec = new ReedSolomonCodec(VAULT_K, VAULT_M);

    let shards: Uint8Array[];
    if (data.length === 0) {
      // Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
      const ds = Array.from({ length: VAULT_K }, () => new Uint8Array(1));
      shards = codec.encode(ds);
    } else {
      shards = encodeData(codec, data);
    }

    const shardHashes: string[] = [];
    for (const sh of shards) {
      const h = sha256Hex(sh);
      shardHashes.push(h);
      this.shards.set(h, sh);
    }

    return {
      contentHash,
      shardHashes,
      k: VAULT_K,
      m: VAULT_M,
      sizeBytes: data.length,
      label,
      createdAtUtc: new Date(),
    };
  }

  async recover(manifest: VaultManifest): Promise<Uint8Array> {
    const total = manifest.shardHashes.length;
    const k = manifest.k;
    const m = total - k;
    const codec = new ReedSolomonCodec(k, m);

    const available = new Map<number, Uint8Array>();
    manifest.shardHashes.forEach((h, i) => {
      const sh = this.shards.get(h);
      if (sh) available.set(i, sh);
    });
    if (available.size < k) {
      throw new Error(`vault: cannot recover — only ${available.size}/${k} shards available`);
    }
    return reconstructData(codec, available, manifest.sizeBytes);
  }

  checkHealth(manifest: VaultManifest): VaultHealth {
    let reachable = 0;
    for (const h of manifest.shardHashes) {
      if (this.shards.has(h)) reachable++;
    }
    const total = vaultTotalShards(manifest);
    return {
      totalShards: total,
      reachableShards: reachable,
      isRecoverable: reachable >= manifest.k,
      redundancyScore: total > 0 ? reachable / total : 0,
    };
  }

  async replicate(_manifest: VaultManifest, _targetRedundancy = 14): Promise<void> {
    // No-op in the in-memory implementation.
  }
}
