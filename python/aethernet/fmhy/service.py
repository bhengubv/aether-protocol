# SPDX-License-Identifier: MIT
"""Free Media Heck Yeah (FMHY) content catalogue (Phase-2 extension).

Propagated over the Aether mesh so offline peers benefit from entries fetched by
connected peers. Port of the C# reference (AetherNet.Fmhy): a markdown parser for
the FMHY single-page dump plus an in-memory catalogue.
"""
from __future__ import annotations

import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable, Optional

FMHY_API_URL = "https://api.fmhy.net/single-page"


@dataclass
class FmhyEntry:
    """A single resource parsed from the FMHY directory."""

    name: str
    url: str
    description: Optional[str]
    category: str  # "H1" or "H1 / H2"
    is_starred: bool
    mirrors: list[str] = field(default_factory=list)

    @property
    def all_urls(self) -> list[str]:
        return [self.url] if not self.mirrors else [self.url, *self.mirrors]


@dataclass
class TrackerSource:
    """A known torrent tracker-list aggregator."""

    name: str
    url: str
    description: str


BUILT_IN_TRACKER_SOURCES: list[TrackerSource] = [
    TrackerSource("ngosang/trackerslist", "https://ngosang.github.io/trackerslist/trackers_all.txt", "Community-maintained list of all known public BitTorrent trackers."),
    TrackerSource("XIU2/TrackersListCollection (all)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", "Comprehensive tracker collection maintained by XIU2, updated daily."),
    TrackerSource("XIU2/TrackersListCollection (best)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", "Curated best-performing tracker subset from the XIU2 collection."),
    TrackerSource("newtrackon (stable)", "https://newtrackon.com/api/stable", "Live-monitored stable tracker list from newtrackon.com."),
    TrackerSource("openwebtorrent", "https://openwebtorrent.com/", "Free WebTorrent-compatible tracker for browser-based torrenting."),
]

_BOLD_LINK_RE = re.compile(r"\*\*\[([^\]]+)\]\(([^)]+)\)\*\*")
_PLAIN_LINK_RE = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")
_HEADING_RE = re.compile(r"^(#{1,2})\s+(.+)$")
_BULLET_RE = re.compile(r"^\s*[*\-]\s+(.+)$")


def parse_fmhy_markdown(markdown: str) -> list[FmhyEntry]:
    """Parse a raw FMHY markdown string into a flat list of entries in document order."""
    entries: list[FmhyEntry] = []
    h1 = ""
    h2 = ""

    for raw_line in markdown.split("\n"):
        line = raw_line.rstrip(" \t\r")
        if not line:
            continue

        hm = _HEADING_RE.match(line)
        if hm:
            level = len(hm.group(1))
            title = hm.group(2).strip()
            if level == 1:
                h1, h2 = title, ""
            else:
                h2 = title
            continue

        bm = _BULLET_RE.match(line)
        if not bm:
            continue
        content = bm.group(1)
        is_starred = "⭐" in content  # ⭐

        bold = _BOLD_LINK_RE.search(content)
        if not bold:
            continue
        name = bold.group(1).strip()
        url = bold.group(2).strip()
        if not url or url.startswith("#"):
            continue
        bold_end = bold.end()

        description: Optional[str] = None
        rel = content[bold_end:].find(" - ")
        desc_sep = rel + bold_end if rel >= 0 else -1
        if desc_sep >= 0:
            description = content[desc_sep + 3:].strip()
            description = _PLAIN_LINK_RE.sub(r"\1", description).strip()
            if not description:
                description = None

        mirror_region = content[bold_end:desc_sep] if desc_sep >= 0 else content[bold_end:]
        mirrors: list[str] = []
        for pm in _PLAIN_LINK_RE.finditer(mirror_region):
            mu = pm.group(2).strip()
            if mu and mu != url and not mu.startswith("#"):
                mirrors.append(mu)

        category = f"{h1} / {h2}" if h2 else h1
        entries.append(FmhyEntry(name, url, description, category, is_starred, mirrors))
    return entries


class IFmhyCatalogueService(ABC):
    """Provides access to the FMHY content catalogue."""

    @abstractmethod
    async def sync(self, markdown: str) -> None: ...

    @abstractmethod
    def browse(self, category_filter: Optional[str] = None) -> list[FmhyEntry]: ...

    @abstractmethod
    def get_starred(self, category_filter: Optional[str] = None) -> list[FmhyEntry]: ...

    @abstractmethod
    def get_tracker_sources(self) -> list[TrackerSource]: ...

    @property
    @abstractmethod
    def entry_count(self) -> int: ...


class InMemoryFmhyCatalogueService(IFmhyCatalogueService):
    """In-memory IFmhyCatalogueService seeded optionally and updated via sync()."""

    def __init__(self, seed: Optional[list[FmhyEntry]] = None) -> None:
        self._entries: list[FmhyEntry] = seed or []
        self._last_synced_at: Optional[datetime] = None
        self.on_synced: Optional[Callable[[int, int, datetime], None]] = None

    @property
    def entry_count(self) -> int:
        return len(self._entries)

    @property
    def last_synced_at(self) -> Optional[datetime]:
        return self._last_synced_at

    async def sync(self, markdown: str) -> None:
        before = len(self._entries)
        parsed = parse_fmhy_markdown(markdown)
        now = datetime.now(timezone.utc)
        self._entries = parsed
        self._last_synced_at = now
        if self.on_synced is not None:
            self.on_synced(len(parsed), len(parsed) - before, now)

    def browse(self, category_filter: Optional[str] = None) -> list[FmhyEntry]:
        if not category_filter:
            return self._entries
        cf = category_filter.lower()
        return [e for e in self._entries if cf in e.category.lower()]

    def get_starred(self, category_filter: Optional[str] = None) -> list[FmhyEntry]:
        cf = category_filter.lower() if category_filter else None
        return [
            e
            for e in self._entries
            if e.is_starred and (cf is None or cf in e.category.lower())
        ]

    def get_tracker_sources(self) -> list[TrackerSource]:
        return BUILT_IN_TRACKER_SOURCES
