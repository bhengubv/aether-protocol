/**
 * DTN bundle store + in-memory default.
 * SPDX-License-Identifier: MIT
 */

import {
  BundleStatus,
  CustodyRecord,
  DtnBundle,
  isBundleExpired,
} from "../models/index.js";

export interface IDtnBundleStore {
  get(bundleId: string): Promise<DtnBundle | null>;
  getActive(): Promise<DtnBundle[]>;
  save(bundle: DtnBundle): Promise<void>;
  remove(bundleId: string): Promise<void>;
  getActiveCount(): Promise<number>;
  saveCustody(record: CustodyRecord): Promise<void>;
  getCustodyRecords(bundleId: string): Promise<CustodyRecord[]>;
  expireStale(): Promise<number>;
}

export class InMemoryDtnBundleStore implements IDtnBundleStore {
  private readonly bundles = new Map<string, DtnBundle>();
  private readonly custody = new Map<string, CustodyRecord>();

  async get(bundleId: string): Promise<DtnBundle | null> {
    return this.bundles.get(bundleId) ?? null;
  }

  async getActive(): Promise<DtnBundle[]> {
    const out: DtnBundle[] = [];
    for (const b of this.bundles.values()) {
      if (
        !isBundleExpired(b) &&
        (b.status === BundleStatus.Pending || b.status === BundleStatus.InCustody)
      ) {
        out.push(b);
      }
    }
    return out;
  }

  async save(bundle: DtnBundle): Promise<void> {
    this.bundles.set(bundle.id, bundle);
  }

  async remove(bundleId: string): Promise<void> {
    this.bundles.delete(bundleId);
  }

  async getActiveCount(): Promise<number> {
    let count = 0;
    for (const b of this.bundles.values()) {
      if (
        !isBundleExpired(b) &&
        (b.status === BundleStatus.Pending || b.status === BundleStatus.InCustody)
      ) {
        count++;
      }
    }
    return count;
  }

  async saveCustody(record: CustodyRecord): Promise<void> {
    this.custody.set(record.id, record);
  }

  async getCustodyRecords(bundleId: string): Promise<CustodyRecord[]> {
    return Array.from(this.custody.values()).filter((r) => r.bundleId === bundleId);
  }

  async expireStale(): Promise<number> {
    let expired = 0;
    for (const b of this.bundles.values()) {
      if (isBundleExpired(b) && b.status !== BundleStatus.Expired) {
        b.status = BundleStatus.Expired;
        expired++;
      }
    }
    return expired;
  }
}
