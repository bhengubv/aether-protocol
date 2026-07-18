# SPDX-License-Identifier: MIT
"""Rarest-first picker + SHA-1-verified piece store."""
from __future__ import annotations

import hashlib

from .wire import Bitfield


class RarestFirstPicker:
    def __init__(self, piece_count: int):
        self.count = piece_count
        self.have = [False] * piece_count
        self.inflight = [False] * piece_count
        self.avail = [0] * piece_count
        self.peer_has: dict[str, list[bool]] = {}

    def set_have(self, i: int) -> None:
        if 0 <= i < self.count:
            self.have[i] = True
            self.inflight[i] = False

    def add_peer(self, peer: str) -> None:
        self.peer_has.setdefault(peer, [False] * self.count)

    def peer_has_piece(self, peer: str, i: int) -> None:
        self.add_peer(peer)
        if 0 <= i < self.count and not self.peer_has[peer][i]:
            self.peer_has[peer][i] = True
            self.avail[i] += 1

    def pick_for(self, peer: str) -> int:
        has = self.peer_has.get(peer)
        if has is None:
            return -1
        best, best_avail = -1, 0
        for i in range(self.count):
            if self.have[i] or self.inflight[i] or not has[i]:
                continue
            if best == -1 or self.avail[i] < best_avail:
                best, best_avail = i, self.avail[i]
        if best != -1:
            self.inflight[best] = True
        return best

    def release(self, i: int) -> None:
        if 0 <= i < self.count:
            self.inflight[i] = False

    def is_complete(self) -> bool:
        return self.count > 0 and all(self.have)


class PieceStore:
    def __init__(self, piece_length: int, total_length: int, piece_hashes: list[bytes]):
        self.piece_length = piece_length
        self.total_length = total_length
        self.piece_hashes = piece_hashes
        self.pieces: dict[int, bytes] = {}

    def piece_count(self) -> int:
        return len(self.piece_hashes)

    def length_of_piece(self, i: int) -> int:
        if not (0 <= i < len(self.piece_hashes)):
            return 0
        if i == len(self.piece_hashes) - 1:
            return self.total_length - i * self.piece_length
        return self.piece_length

    def has(self, i: int) -> bool:
        return i in self.pieces

    def try_complete(self, i: int, data: bytes) -> bool:
        if not (0 <= i < len(self.piece_hashes)):
            return False
        if len(data) != self.length_of_piece(i):
            return False
        if hashlib.sha1(data).digest() != self.piece_hashes[i]:
            return False
        self.pieces[i] = bytes(data)
        return True

    def read_block(self, i: int, begin: int, length: int):
        p = self.pieces.get(i)
        if p is None or begin < 0 or begin + length > len(p):
            return None
        return p[begin:begin + length]

    def build_bitfield(self) -> Bitfield:
        bf = Bitfield(len(self.piece_hashes))
        for i in range(len(self.piece_hashes)):
            if self.has(i):
                bf.set(i)
        return bf

    def is_complete(self) -> bool:
        return len(self.pieces) == len(self.piece_hashes)

    def assemble(self):
        if not self.is_complete():
            return None
        return b"".join(self.pieces[i] for i in range(len(self.piece_hashes)))


def piece_store_from_content(data: bytes, piece_length: int) -> PieceStore:
    piece_count = (len(data) + piece_length - 1) // piece_length
    hashes = []
    store = PieceStore(piece_length, len(data), [])
    for i in range(piece_count):
        start = i * piece_length
        end = min(start + piece_length, len(data))
        hashes.append(hashlib.sha1(data[start:end]).digest())
        store.pieces[i] = bytes(data[start:end])
    store.piece_hashes = hashes
    return store
