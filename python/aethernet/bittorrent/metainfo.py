# SPDX-License-Identifier: MIT
"""Torrent metainfo, info-hash (SHA-1 of the raw info dict), magnet, and builder."""
from __future__ import annotations

import base64
import hashlib
from urllib.parse import parse_qs, urlsplit

from . import bencode


class TorrentError(ValueError):
    pass


class TorrentMetainfo:
    def __init__(self, root, info, info_hash_v1, name, piece_length, piece_hashes,
                 files, total_length, announce_urls, is_single_file):
        self.root = root
        self.info = info
        self.info_hash_v1 = info_hash_v1
        self.name = name
        self.piece_length = piece_length
        self.piece_hashes = piece_hashes
        self.files = files
        self.total_length = total_length
        self.announce_urls = announce_urls
        self.is_single_file = is_single_file

    @property
    def info_hash_v1_hex(self) -> str:
        return self.info_hash_v1.hex()


def build_single_file_torrent(name: str, data: bytes, piece_length: int, announce: str = "") -> bytes:
    if not name:
        raise TorrentError("name is required")
    if piece_length <= 0:
        raise TorrentError("piece length must be positive")
    piece_count = (len(data) + piece_length - 1) // piece_length
    pieces = bytearray()
    for i in range(piece_count):
        start = i * piece_length
        end = min(start + piece_length, len(data))
        pieces += hashlib.sha1(data[start:end]).digest()
    info = {
        b"length": len(data),
        b"name": name.encode("utf-8"),
        b"piece length": piece_length,
        b"pieces": bytes(pieces),
    }
    root: dict = {}
    if announce and announce.strip():
        root[b"announce"] = announce.encode("utf-8")
    root[b"info"] = info
    return bencode.encode(root)


def parse_torrent(data: bytes) -> TorrentMetainfo:
    root = bencode.decode(data)
    if not isinstance(root, dict):
        raise TorrentError("metainfo is not a dictionary")
    info = root.get(b"info")
    if not isinstance(info, dict):
        raise TorrentError("metainfo has no 'info' dictionary")

    info_hash = hashlib.sha1(_extract_info_span(data)).digest()

    name = _text(info.get(b"name"), "info has no 'name'")
    piece_length = _int(info.get(b"piece length"), "info has no 'piece length'")
    if piece_length <= 0:
        raise TorrentError("'piece length' must be positive")

    pieces = info.get(b"pieces")
    if not isinstance(pieces, (bytes, bytearray)):
        raise TorrentError("info has no 'pieces'")
    if len(pieces) % 20 != 0:
        raise TorrentError("'pieces' length is not a multiple of 20")
    piece_hashes = [bytes(pieces[i:i + 20]) for i in range(0, len(pieces), 20)]

    files = []
    total = 0
    if b"files" in info:
        is_single = False
        for f in info[b"files"]:
            length = _int(f.get(b"length"), "file entry has no 'length'")
            parts = [p.decode("utf-8") for p in f.get(b"path", [])]
            if not parts:
                raise TorrentError("file entry has an empty 'path'")
            files.append((parts, length))
            total += length
    else:
        is_single = True
        length = _int(info.get(b"length"), "single-file info has neither 'length' nor 'files'")
        files.append(([name], length))
        total = length

    announce = []
    seen = set()

    def add(u: str):
        if u and u not in seen:
            seen.add(u)
            announce.append(u)

    if b"announce" in root:
        add(root[b"announce"].decode("utf-8"))
    for tier in root.get(b"announce-list", []):
        for t in tier:
            add(t.decode("utf-8"))

    return TorrentMetainfo(root, info, info_hash, name, piece_length, piece_hashes,
                           files, total, announce, is_single)


def _extract_info_span(data: bytes) -> bytes:
    if not data or data[0] != ord("d"):
        raise TorrentError("metainfo is not a bencoded dictionary")
    pos = 1
    while pos < len(data) and data[pos] != ord("e"):
        key, pos = bencode._decode_str(data, pos)
        val_start = pos
        _, pos = bencode.decode_n(data, pos)
        if key == b"info":
            return data[val_start:pos]
    raise TorrentError("metainfo has no 'info' key")


def _text(v, err: str) -> str:
    if not isinstance(v, (bytes, bytearray)):
        raise TorrentError(err)
    return bytes(v).decode("utf-8")


def _int(v, err: str) -> int:
    if not isinstance(v, int):
        raise TorrentError(err)
    return v


class MagnetLink:
    def __init__(self, info_hash: bytes, display_name: str, trackers: list[str]):
        self.info_hash = info_hash
        self.display_name = display_name
        self.trackers = trackers

    @property
    def info_hash_hex(self) -> str:
        return self.info_hash.hex()


def parse_magnet(uri: str) -> MagnetLink:
    if not uri.startswith("magnet:?"):
        raise TorrentError("not a magnet URI")
    q = parse_qs(uri[len("magnet:?"):])
    info_hash = None
    for xt in q.get("xt", []):
        if xt.startswith("urn:btih:"):
            info_hash = _decode_info_hash(xt[len("urn:btih:"):])
            break
    if info_hash is None:
        raise TorrentError("magnet has no xt=urn:btih: topic")
    dn = q.get("dn", [""])[0]
    return MagnetLink(info_hash, dn, q.get("tr", []))


def _decode_info_hash(s: str) -> bytes:
    if len(s) == 40:
        return bytes.fromhex(s)
    if len(s) == 32:
        return base64.b32decode(s.upper())
    raise TorrentError(f"info-hash must be 40 hex or 32 base32 chars, got {len(s)}")
