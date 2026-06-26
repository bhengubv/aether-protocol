// SPDX-License-Identifier: MIT

// Free Media Heck Yeah (FMHY) content catalogue (Phase-2 extension), propagated
// over the Aether mesh so offline peers benefit from entries fetched by connected
// peers. Port of the C# reference (AetherNet.Fmhy): a markdown parser for the
// FMHY single-page dump plus an in-memory catalogue.

/** A single resource parsed from the FMHY directory. */
export interface FmhyEntry {
  name: string;
  url: string;
  description: string | null;
  category: string; // "H1" or "H1 / H2"
  isStarred: boolean;
  mirrors: string[];
}

/** All URLs for an entry: primary + mirrors. */
export function fmhyAllUrls(e: FmhyEntry): string[] {
  return e.mirrors.length === 0 ? [e.url] : [e.url, ...e.mirrors];
}

/** A known torrent tracker-list aggregator. */
export interface TrackerSource {
  name: string;
  url: string;
  description: string;
}

/** Well-known public tracker-list aggregators bundled with this release. */
export const BUILT_IN_TRACKER_SOURCES: readonly TrackerSource[] = [
  { name: "ngosang/trackerslist", url: "https://ngosang.github.io/trackerslist/trackers_all.txt", description: "Community-maintained list of all known public BitTorrent trackers." },
  { name: "XIU2/TrackersListCollection (all)", url: "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", description: "Comprehensive tracker collection maintained by XIU2, updated daily." },
  { name: "XIU2/TrackersListCollection (best)", url: "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", description: "Curated best-performing tracker subset from the XIU2 collection." },
  { name: "newtrackon (stable)", url: "https://newtrackon.com/api/stable", description: "Live-monitored stable tracker list from newtrackon.com." },
  { name: "openwebtorrent", url: "https://openwebtorrent.com/", description: "Free WebTorrent-compatible tracker for browser-based torrenting." },
];

/** The public FMHY single-page endpoint. */
export const FMHY_API_URL = "https://api.fmhy.net/single-page";

const BOLD_LINK_RE = /\*\*\[([^\]]+)\]\(([^)]+)\)\*\*/;
const PLAIN_LINK_RE = /\[([^\]]+)\]\(([^)]+)\)/g;
const HEADING_RE = /^(#{1,2})\s+(.+)$/;
const BULLET_RE = /^\s*[*\-]\s+(.+)$/;

/** Parse a raw FMHY markdown string into a flat list of entries in document order. */
export function parseFmhyMarkdown(markdown: string): FmhyEntry[] {
  const entries: FmhyEntry[] = [];
  let h1 = "";
  let h2 = "";

  for (const rawLine of markdown.split("\n")) {
    const line = rawLine.replace(/[ \t\r]+$/, "");
    if (line.length === 0) continue;

    const hm = HEADING_RE.exec(line);
    if (hm) {
      const level = hm[1].length;
      const title = hm[2].trim();
      if (level === 1) {
        h1 = title;
        h2 = "";
      } else {
        h2 = title;
      }
      continue;
    }

    const bm = BULLET_RE.exec(line);
    if (!bm) continue;
    const content = bm[1];
    const isStarred = content.includes("⭐");

    const bold = BOLD_LINK_RE.exec(content);
    if (!bold) continue;
    const name = bold[1].trim();
    const url = bold[2].trim();
    if (url === "" || url.startsWith("#")) continue;
    const boldEnd = bold.index + bold[0].length;

    let description: string | null = null;
    const descSepRel = content.slice(boldEnd).indexOf(" - ");
    const descSep = descSepRel >= 0 ? descSepRel + boldEnd : -1;
    if (descSep >= 0) {
      description = content.slice(descSep + 3).trim();
      description = description.replace(/\[([^\]]+)\]\([^)]+\)/g, "$1").trim();
      if (description === "") description = null;
    }

    const mirrorRegion = descSep >= 0 ? content.slice(boldEnd, descSep) : content.slice(boldEnd);
    const mirrors: string[] = [];
    PLAIN_LINK_RE.lastIndex = 0;
    let pm: RegExpExecArray | null;
    while ((pm = PLAIN_LINK_RE.exec(mirrorRegion)) !== null) {
      const mu = pm[2].trim();
      if (mu !== "" && mu !== url && !mu.startsWith("#")) mirrors.push(mu);
    }

    const category = h2.length > 0 ? `${h1} / ${h2}` : h1;
    entries.push({ name, url, description, category, isStarred, mirrors });
  }
  return entries;
}

/** Provides access to the FMHY content catalogue. */
export interface IFmhyCatalogueService {
  sync(markdown: string): void;
  browse(categoryFilter?: string): FmhyEntry[];
  getStarred(categoryFilter?: string): FmhyEntry[];
  getTrackerSources(): readonly TrackerSource[];
  readonly entryCount: number;
  readonly lastSyncedAt: Date | null;
}

/** In-memory IFmhyCatalogueService seeded optionally and updated via sync(). */
export class InMemoryFmhyCatalogueService implements IFmhyCatalogueService {
  private entries: FmhyEntry[];
  private _lastSyncedAt: Date | null = null;

  /** Fires when sync installs new entries: (total, added, syncedAt). */
  onSynced?: (total: number, added: number, syncedAt: Date) => void;

  constructor(seed: FmhyEntry[] = []) {
    this.entries = seed;
  }

  get entryCount(): number {
    return this.entries.length;
  }

  get lastSyncedAt(): Date | null {
    return this._lastSyncedAt;
  }

  sync(markdown: string): void {
    const before = this.entries.length;
    const parsed = parseFmhyMarkdown(markdown);
    const now = new Date();
    this.entries = parsed;
    this._lastSyncedAt = now;
    this.onSynced?.(parsed.length, parsed.length - before, now);
  }

  browse(categoryFilter?: string): FmhyEntry[] {
    if (!categoryFilter) return this.entries;
    const cf = categoryFilter.toLowerCase();
    return this.entries.filter((e) => e.category.toLowerCase().includes(cf));
  }

  getStarred(categoryFilter?: string): FmhyEntry[] {
    const cf = categoryFilter?.toLowerCase();
    return this.entries.filter(
      (e) => e.isStarred && (!cf || e.category.toLowerCase().includes(cf)),
    );
  }

  getTrackerSources(): readonly TrackerSource[] {
    return BUILT_IN_TRACKER_SOURCES;
  }
}
