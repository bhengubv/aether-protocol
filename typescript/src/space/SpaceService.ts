// SPDX-License-Identifier: MIT

// aether-space: geo-pinned community noticeboards (Phase-2 extension). Nodes drop
// breadcrumbs at geohash coordinates; passing devices auto-pull and re-host them
// for other passersby — fully offline. Port of the C# reference (AetherNet.Space).
// Wire format: JSON, transmitted as PacketType.SpaceBreadcrumb (40).

/** Category of a geo-pinned breadcrumb. */
export enum BreadcrumbType {
  /** General community notice (default). */
  Notice = 0,
  /** Emergency alert — bypasses flood-guard; TTL extended to 720 h. */
  Emergency = 1,
  /** Commercial listing or market offer. */
  Commerce = 2,
  /** Local event announcement. */
  Event = 3,
  /** Job posting or opportunity. */
  JobPosting = 4,
}

/** Fixed TTL applied to Emergency breadcrumbs. */
export const EMERGENCY_TTL_HOURS = 720;
/** Bounds for a non-emergency breadcrumb's lifetime. */
export const MIN_TTL_HOURS = 1;
export const MAX_TTL_HOURS = 168;

/**
 * A geo-pinned digital notice dropped by a user at a physical location. Content
 * is addressed by hash; the breadcrumb carries only metadata.
 */
export interface SpaceBreadcrumb {
  /** Content-service hash of the actual payload. */
  contentHash: string;
  /** 6-character geohash of the drop location (~1.2 km² cell). */
  geoHash: string;
  /** UHID of the node that dropped the breadcrumb. */
  anchorUhid: string;
  /** UTC creation timestamp. */
  createdAtUtc: Date;
  /** Time-to-live in hours. */
  ttlHours: number;
  /** Category of the breadcrumb. */
  type: BreadcrumbType;
  /** Ed25519 signature over (contentHash + geoHash + createdAt ISO-8601); empty if unsigned. */
  signature: Uint8Array;
}

/** UTC expiry = createdAtUtc + ttlHours. */
export function breadcrumbExpiresAtUtc(b: SpaceBreadcrumb): Date {
  return new Date(b.createdAtUtc.getTime() + b.ttlHours * 3_600_000);
}

/** True once the breadcrumb's TTL has passed. */
export function breadcrumbIsExpired(b: SpaceBreadcrumb): boolean {
  return Date.now() >= breadcrumbExpiresAtUtc(b).getTime();
}

/** The aether-space breadcrumb store. */
export interface ISpaceService {
  drop(
    geoHash: string,
    contentHash: string,
    anchorUhid: string,
    type?: BreadcrumbType,
    ttlHours?: number,
  ): Promise<SpaceBreadcrumb>;
  scan(centerGeoHash: string, radiusCells?: number): Promise<SpaceBreadcrumb[]>;
  pin(breadcrumb: SpaceBreadcrumb): Promise<void>;
  /** Creator-only delete: succeeds only if requestorUhid is the breadcrumb's anchorUhid. */
  delete(breadcrumb: SpaceBreadcrumb, requestorUhid: string): Promise<boolean>;
  /** Drops every expired breadcrumb; returns the count removed. */
  pruneExpired(): number;
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

/**
 * In-memory ISpaceService for testing and single-node use; state is lost on
 * restart. Proximity matching uses a geohash-prefix heuristic.
 */
export class InMemorySpaceService implements ISpaceService {
  private readonly store = new Map<string, SpaceBreadcrumb>(); // key = contentHash

  /** Fires when a breadcrumb is dropped locally or pinned from the mesh. */
  onBreadcrumbReceived?: (b: SpaceBreadcrumb) => void;
  /** Fires when a cached breadcrumb passes its TTL. */
  onBreadcrumbExpired?: (b: SpaceBreadcrumb) => void;

  async drop(
    geoHash: string,
    contentHash: string,
    anchorUhid: string,
    type: BreadcrumbType = BreadcrumbType.Notice,
    ttlHours = 72,
  ): Promise<SpaceBreadcrumb> {
    const effectiveTtl =
      type === BreadcrumbType.Emergency ? EMERGENCY_TTL_HOURS : clamp(ttlHours, MIN_TTL_HOURS, MAX_TTL_HOURS);
    const crumb: SpaceBreadcrumb = {
      contentHash,
      geoHash,
      anchorUhid,
      createdAtUtc: new Date(),
      ttlHours: effectiveTtl,
      type,
      signature: new Uint8Array(0),
    };
    this.store.set(contentHash, crumb);
    this.onBreadcrumbReceived?.(crumb);
    return crumb;
  }

  async scan(centerGeoHash: string, radiusCells = 1): Promise<SpaceBreadcrumb[]> {
    const prefixLen = clamp(6 - radiusCells, 1, 6);
    const prefix = (centerGeoHash.length >= prefixLen ? centerGeoHash.slice(0, prefixLen) : centerGeoHash).toLowerCase();
    const results: SpaceBreadcrumb[] = [];
    for (const c of this.store.values()) {
      if (!breadcrumbIsExpired(c) && c.geoHash.toLowerCase().startsWith(prefix)) {
        results.push(c);
      }
    }
    return results;
  }

  async pin(breadcrumb: SpaceBreadcrumb): Promise<void> {
    this.store.set(breadcrumb.contentHash, breadcrumb);
    this.onBreadcrumbReceived?.(breadcrumb);
  }

  async delete(breadcrumb: SpaceBreadcrumb, requestorUhid: string): Promise<boolean> {
    const stored = this.store.get(breadcrumb.contentHash);
    if (!stored) return false;
    if (stored.anchorUhid !== requestorUhid) return false; // creator-only delete
    return this.store.delete(breadcrumb.contentHash);
  }

  pruneExpired(): number {
    const expired = [...this.store.values()].filter(breadcrumbIsExpired);
    for (const c of expired) {
      if (this.store.delete(c.contentHash)) {
        this.onBreadcrumbExpired?.(c);
      }
    }
    return expired.length;
  }
}
