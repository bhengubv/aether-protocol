# SPDX-License-Identifier: MIT
"""BEP-3 peer-wire: handshake, messages (exact big-endian framing), MSB-first bitfield."""
from __future__ import annotations

import struct

PROTOCOL_STRING = b"BitTorrent protocol"

CHOKE = 0
UNCHOKE = 1
INTERESTED = 2
NOT_INTERESTED = 3
HAVE = 4
BITFIELD = 5
REQUEST = 6
PIECE = 7
CANCEL = 8
PORT = 9
EXTENDED = 20


def default_reserved() -> bytes:
    r = bytearray(8)
    r[5] |= 0x10  # extension protocol
    r[7] |= 0x01  # DHT
    return bytes(r)


class Handshake:
    def __init__(self, info_hash: bytes, peer_id: bytes, reserved: bytes | None = None):
        self.reserved = reserved if reserved is not None else default_reserved()
        self.info_hash = info_hash
        self.peer_id = peer_id

    def to_bytes(self) -> bytes:
        return bytes([19]) + PROTOCOL_STRING + self.reserved + self.info_hash + self.peer_id

    @staticmethod
    def parse(data: bytes) -> "Handshake":
        if len(data) < 68:
            raise ValueError(f"handshake is {len(data)} bytes, need 68")
        if data[0] != 19 or data[1:20] != PROTOCOL_STRING:
            raise ValueError("handshake prefix mismatch")
        return Handshake(bytes(data[28:48]), bytes(data[48:68]), bytes(data[20:28]))

    def supports_extended(self) -> bool:
        return bool(self.reserved[5] & 0x10)

    def supports_dht(self) -> bool:
        return bool(self.reserved[7] & 0x01)


class PeerMessage:
    def __init__(self, msg_id: int | None, payload: bytes = b""):
        self.id = msg_id  # None => keep-alive
        self.payload = payload

    def to_bytes(self) -> bytes:
        if self.id is None:
            return b"\x00\x00\x00\x00"
        return struct.pack(">I", 1 + len(self.payload)) + bytes([self.id]) + self.payload


def keep_alive() -> PeerMessage:
    return PeerMessage(None)


def choke() -> PeerMessage:
    return PeerMessage(CHOKE)


def unchoke() -> PeerMessage:
    return PeerMessage(UNCHOKE)


def interested() -> PeerMessage:
    return PeerMessage(INTERESTED)


def not_interested() -> PeerMessage:
    return PeerMessage(NOT_INTERESTED)


def have(piece_index: int) -> PeerMessage:
    return PeerMessage(HAVE, struct.pack(">I", piece_index))


def bitfield_message(bits: bytes) -> PeerMessage:
    return PeerMessage(BITFIELD, bits)


def request(index: int, begin: int, length: int) -> PeerMessage:
    return PeerMessage(REQUEST, struct.pack(">III", index, begin, length))


def cancel(index: int, begin: int, length: int) -> PeerMessage:
    return PeerMessage(CANCEL, struct.pack(">III", index, begin, length))


def piece(index: int, begin: int, block: bytes) -> PeerMessage:
    return PeerMessage(PIECE, struct.pack(">II", index, begin) + block)


def port(value: int) -> PeerMessage:
    return PeerMessage(PORT, struct.pack(">H", value))


def extended(sub_id: int, body: bytes) -> PeerMessage:
    return PeerMessage(EXTENDED, bytes([sub_id]) + body)


def parse_frame(data: bytes):
    """Parse a length-prefixed frame; return (PeerMessage, consumed)."""
    if len(data) < 4:
        raise ValueError("frame shorter than 4-byte length prefix")
    (length,) = struct.unpack(">I", data[0:4])
    if 4 + length > len(data):
        raise ValueError("frame length exceeds available data")
    body = data[4:4 + length]
    if length == 0:
        return PeerMessage(None), 4
    return PeerMessage(body[0], bytes(body[1:])), 4 + length


class Bitfield:
    def __init__(self, piece_count: int, bits: bytes | None = None):
        self.count = piece_count
        need = (piece_count + 7) // 8
        if bits is None:
            self.bits = bytearray(need)
        else:
            self.bits = bytearray(need)
            self.bits[:len(bits)] = bits[:need]

    @staticmethod
    def from_bytes(data: bytes, piece_count: int) -> "Bitfield":
        return Bitfield(piece_count, data)

    def get(self, i: int) -> bool:
        if not (0 <= i < self.count):
            return False
        return bool(self.bits[i >> 3] & (0x80 >> (i & 7)))

    def set(self, i: int) -> None:
        if 0 <= i < self.count:
            self.bits[i >> 3] |= 0x80 >> (i & 7)

    def pop_count(self) -> int:
        return sum(1 for i in range(self.count) if self.get(i))

    def has_all(self) -> bool:
        return self.pop_count() == self.count

    def to_bytes(self) -> bytes:
        return bytes(self.bits)
