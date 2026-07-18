# SPDX-License-Identifier: MIT
"""µTP packet (BEP-29, version 1) — byte-exact 20-byte header."""
from __future__ import annotations

import struct

DATA = 0
FIN = 1
STATE = 2
RESET = 3
SYN = 4

VERSION = 1
HEADER_SIZE = 20


class UtpPacket:
    def __init__(self, type: int, conn_id: int = 0, timestamp: int = 0, timestamp_diff: int = 0,
                 window: int = 0, seq: int = 0, ack: int = 0, payload: bytes = b""):
        self.type = type
        self.conn_id = conn_id
        self.timestamp = timestamp
        self.timestamp_diff = timestamp_diff
        self.window = window
        self.seq = seq
        self.ack = ack
        self.payload = payload

    def to_bytes(self) -> bytes:
        h = bytearray(HEADER_SIZE)
        h[0] = (self.type << 4) | VERSION
        h[1] = 0  # no extensions
        struct.pack_into(">H", h, 2, self.conn_id)
        struct.pack_into(">I", h, 4, self.timestamp)
        struct.pack_into(">I", h, 8, self.timestamp_diff)
        struct.pack_into(">I", h, 12, self.window)
        struct.pack_into(">H", h, 16, self.seq)
        struct.pack_into(">H", h, 18, self.ack)
        return bytes(h) + self.payload

    @staticmethod
    def parse(data: bytes) -> "UtpPacket":
        if len(data) < HEADER_SIZE:
            raise ValueError(f"µTP packet is {len(data)} bytes, shorter than {HEADER_SIZE}")
        version = data[0] & 0x0F
        if version != VERSION:
            raise ValueError(f"unsupported µTP version {version}")
        packet_type = data[0] >> 4
        offset = HEADER_SIZE
        next_ext = data[1]
        while next_ext != 0:
            if offset + 2 > len(data):
                raise ValueError("truncated µTP extension header")
            this_next = data[offset]
            ext_len = data[offset + 1]
            offset += 2 + ext_len
            if offset > len(data):
                raise ValueError("truncated µTP extension data")
            next_ext = this_next
        return UtpPacket(
            packet_type,
            struct.unpack(">H", data[2:4])[0],
            struct.unpack(">I", data[4:8])[0],
            struct.unpack(">I", data[8:12])[0],
            struct.unpack(">I", data[12:16])[0],
            struct.unpack(">H", data[16:18])[0],
            struct.unpack(">H", data[18:20])[0],
            bytes(data[offset:]),
        )
