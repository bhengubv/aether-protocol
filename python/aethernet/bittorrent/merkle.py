# SPDX-License-Identifier: MIT
"""BitTorrent v2 (BEP-52) SHA-256 merkle hashing + v2 info-hash."""
from __future__ import annotations

import hashlib

BLOCK_SIZE = 16384


def merkle_root(data: bytes, block_size: int = BLOCK_SIZE) -> bytes:
    if block_size <= 0:
        raise ValueError("block size must be positive")
    leaves = [hashlib.sha256(data[i:i + block_size]).digest() for i in range(0, len(data), block_size)]
    if not leaves:
        return b"\x00" * 32
    return _root_of(leaves)


def _root_of(leaf_hashes: list[bytes]) -> bytes:
    level = list(leaf_hashes)
    width = 1
    while width < len(level):
        width <<= 1
    zero = b"\x00" * 32
    while len(level) < width:
        level.append(zero)
    while len(level) > 1:
        level = [hashlib.sha256(level[i] + level[i + 1]).digest() for i in range(0, len(level), 2)]
    return level[0]


def v2_info_hash(info_dict_bytes: bytes) -> bytes:
    return hashlib.sha256(info_dict_bytes).digest()


def v2_info_hash_truncated(info_dict_bytes: bytes) -> bytes:
    return v2_info_hash(info_dict_bytes)[:20]
