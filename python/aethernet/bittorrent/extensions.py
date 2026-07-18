# SPDX-License-Identifier: MIT
"""BEP-10 extension protocol + BEP-9 ut_metadata + BEP-11 ut_pex."""
from __future__ import annotations

import hashlib

from . import bencode, dht

EXTENDED_MESSAGE_ID = 20
EXTENSION_HANDSHAKE_ID = 0

METADATA_REQUEST = 0
METADATA_DATA = 1
METADATA_REJECT = 2
METADATA_PIECE_SIZE = 16384


def wrap_extended(sub_id: int, body: bytes) -> bytes:
    return bytes([sub_id]) + body


def split_extended(payload: bytes):
    if not payload:
        raise ValueError("empty extended payload")
    return payload[0], bytes(payload[1:])


def build_extension_handshake(supported: dict, metadata_size: int = 0) -> bytes:
    m = {(k.encode("utf-8") if isinstance(k, str) else k): v for k, v in supported.items()}
    d = {b"m": m}
    if metadata_size > 0:
        d[b"metadata_size"] = metadata_size
    return wrap_extended(EXTENSION_HANDSHAKE_ID, bencode.encode(d))


def parse_extension_handshake(body: bytes) -> dict:
    d = bencode.decode(body)
    supported = {k.decode("utf-8"): v for k, v in d.get(b"m", {}).items()}
    return {"supported": supported, "metadata_size": d.get(b"metadata_size", 0)}


def build_metadata_request(piece: int) -> bytes:
    return bencode.encode({b"msg_type": METADATA_REQUEST, b"piece": piece})


def build_metadata_data(piece: int, total_size: int, data: bytes) -> bytes:
    return bencode.encode({b"msg_type": METADATA_DATA, b"piece": piece, b"total_size": total_size}) + data


def build_metadata_reject(piece: int) -> bytes:
    return bencode.encode({b"msg_type": METADATA_REJECT, b"piece": piece})


def parse_metadata(body: bytes) -> dict:
    val, consumed = bencode.decode_n(body, 0)
    return {
        "type": val.get(b"msg_type"),
        "piece": val.get(b"piece"),
        "total_size": val.get(b"total_size", 0),
        "data": bytes(body[consumed:]),
    }


class MetadataAssembler:
    def __init__(self, total_size: int):
        self.total_size = total_size
        self.pieces: dict[int, bytes] = {}

    def piece_count(self) -> int:
        return (self.total_size + METADATA_PIECE_SIZE - 1) // METADATA_PIECE_SIZE

    def add(self, piece: int, data: bytes) -> None:
        self.pieces[piece] = bytes(data)

    def is_complete(self) -> bool:
        return len(self.pieces) == self.piece_count()

    def try_finish(self, info_hash: bytes):
        if not self.is_complete():
            return None
        out = b"".join(self.pieces[i] for i in range(self.piece_count()))
        if len(out) != self.total_size or hashlib.sha1(out).digest() != info_hash:
            return None
        return out


def build_pex_added(added) -> bytes:
    return bencode.encode({b"added": dht.encode_compact_peers(added)})


def parse_pex_added(body: bytes):
    d = bencode.decode(body)
    if b"added" in d:
        return dht.decode_compact_peers(d[b"added"])
    return []
