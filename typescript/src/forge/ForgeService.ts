// SPDX-License-Identifier: MIT

// aether-forge: a mesh-native package cache proxy (Phase-2 extension). The first
// internet pull of a package is cached as Aether content; subsequent pulls by
// anyone in the mesh are served locally at mesh speeds. Port of the C# reference
// (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.

/** Metadata record for one cached package artifact. */
export interface ForgeEntry {
  /** Aether content hash of the cached artifact. */
  contentHash: string;
  /** Package identifier in "ecosystem:name@version" format (e.g. "npm:react@18.2.0"). */
  packageId: string;
  /** UTC timestamp when the artifact was first fetched and cached. */
  fetchedAtUtc: Date;
  /** Size of the cached artifact in bytes. */
  sizeBytes: number;
  /** Times this artifact has been served from the mesh cache. */
  downloadCount: number;
}

/** Aggregate statistics for the local Forge cache. */
export interface ForgeStats {
  totalBytesSaved: number;
  totalPeersServed: number;
  catalogueSize: number;
  /** Top packages by download count (most popular first, up to 10). */
  topPackages: ForgeEntry[];
}

/** The mesh-native package cache. */
export interface IForgeService {
  /** Look up a cached entry by package ID; null if not cached. */
  query(packageId: string): Promise<ForgeEntry | null>;
  /** Store a new artifact (idempotent — first write wins). */
  cache(packageId: string, contentHash: string, sizeBytes: number): Promise<ForgeEntry>;
  /** Increment the download counter and return the entry, or null if not cached. */
  fetch(packageId: string): Promise<ForgeEntry | null>;
  /** Current aggregate cache statistics. */
  getStats(): Promise<ForgeStats>;
}

/** In-memory IForgeService for testing / single-node use; state lost on restart. */
export class InMemoryForgeService implements IForgeService {
  private readonly store = new Map<string, ForgeEntry>(); // key = packageId

  /** Fires when a new artifact is added via cache(). */
  onNewEntryAnnounced?: (entry: ForgeEntry) => void;

  async query(packageId: string): Promise<ForgeEntry | null> {
    return this.store.get(packageId) ?? null;
  }

  async cache(packageId: string, contentHash: string, sizeBytes: number): Promise<ForgeEntry> {
    const existing = this.store.get(packageId);
    if (existing) return existing; // idempotent — first write wins
    const entry: ForgeEntry = {
      packageId,
      contentHash,
      sizeBytes,
      fetchedAtUtc: new Date(),
      downloadCount: 0,
    };
    this.store.set(packageId, entry);
    this.onNewEntryAnnounced?.(entry);
    return entry;
  }

  async fetch(packageId: string): Promise<ForgeEntry | null> {
    const entry = this.store.get(packageId);
    if (!entry) return null;
    entry.downloadCount++;
    return entry;
  }

  async getStats(): Promise<ForgeStats> {
    const entries = [...this.store.values()];
    const totalBytesSaved = entries.reduce((sum, e) => sum + e.downloadCount * e.sizeBytes, 0);
    const topPackages = [...entries].sort((a, b) => b.downloadCount - a.downloadCount).slice(0, 10);
    return {
      totalBytesSaved,
      totalPeersServed: 0,
      catalogueSize: entries.length,
      topPackages,
    };
  }
}
