# SPDX-License-Identifier: MIT
"""
ChunkBitmap wire-format codec for the Aether Chunk Shuffle / SAPI protocol.

Wire format:
  • JSON, snake_case property names.
  • Bitset: LSB-first within each byte — bit i is set in byte (i//8), at
    position (i%8).  Length = ceil(chunk_count / 8).
  • Bitset transmitted as standard Base64 (with padding).
  • Field order in canonical JSON: root_hash, chunk_count, have_bitset,
    generation.
"""

import base64
import json
import math
from typing import Iterable


class BitsetCodec:
    @staticmethod
    def encode(chunk_count: int, have_indices: Iterable[int]) -> bytes:
        """Encode indices into an LSB-first compact bitset.

        Returns bytes of length ceil(chunk_count / 8). Raises ValueError if
        any index is out of [0, chunk_count).
        """
        if chunk_count <= 0:
            return b""
        buf = bytearray((chunk_count + 7) // 8)
        for i in have_indices:
            if i < 0 or i >= chunk_count:
                raise ValueError(f"Index {i} out of range [0, {chunk_count})")
            buf[i >> 3] |= (1 << (i & 7))
        return bytes(buf)

    @staticmethod
    def decode(bitset: bytes, chunk_count: int) -> list[int]:
        """Decode a compact bitset into sorted chunk indices."""
        result = []
        limit = min(chunk_count, len(bitset) * 8)
        for i in range(limit):
            if bitset[i >> 3] & (1 << (i & 7)):
                result.append(i)
        return result


def marshal_json_chunk_bitmap(
    root_hash: str,
    chunk_count: int,
    have_bitset: bytes,
    generation: int,
) -> str:
    """Produce canonical wire JSON with fixed field order."""
    have_b64 = base64.b64encode(have_bitset).decode("ascii")
    # Use json.dumps for values but assemble field order manually
    parts = [
        f'"root_hash":{json.dumps(root_hash)}',
        f'"chunk_count":{chunk_count}',
        f'"have_bitset":{json.dumps(have_b64)}',
        f'"generation":{generation}',
    ]
    return "{" + ",".join(parts) + "}"
