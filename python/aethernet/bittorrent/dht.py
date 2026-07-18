# SPDX-License-Identifier: MIT
"""DHT (BEP-5): XOR distance + compact node (26B) / peer (6B) info."""
from __future__ import annotations

import struct


def xor_distance(a: bytes, b: bytes) -> bytes:
    return bytes(x ^ y for x, y in zip(a, b))


def leading_zeros(node_id: bytes) -> int:
    for i, by in enumerate(node_id):
        if by != 0:
            return i * 8 + (8 - by.bit_length())
    return len(node_id) * 8


def _ip_bytes(ip: str) -> bytes:
    return bytes(int(x) for x in ip.split("."))


def _ip_str(b: bytes) -> str:
    return ".".join(str(x) for x in b)


def encode_compact_nodes(nodes) -> bytes:
    """nodes: iterable of (id_bytes, ip_str, port)."""
    out = bytearray()
    for nid, ip, port in nodes:
        out += bytes(nid) + _ip_bytes(ip) + struct.pack(">H", port)
    return bytes(out)


def decode_compact_nodes(data: bytes):
    if len(data) % 26 != 0:
        raise ValueError("compact nodes length is not a multiple of 26")
    out = []
    for i in range(0, len(data), 26):
        out.append((bytes(data[i:i + 20]), _ip_str(data[i + 20:i + 24]),
                    struct.unpack(">H", data[i + 24:i + 26])[0]))
    return out


def encode_compact_peers(peers) -> bytes:
    """peers: iterable of (ip_str, port)."""
    out = bytearray()
    for ip, port in peers:
        out += _ip_bytes(ip) + struct.pack(">H", port)
    return bytes(out)


def decode_compact_peers(data: bytes):
    if len(data) % 6 != 0:
        raise ValueError("compact peers length is not a multiple of 6")
    out = []
    for i in range(0, len(data), 6):
        out.append((_ip_str(data[i:i + 4]), struct.unpack(">H", data[i + 4:i + 6])[0]))
    return out
