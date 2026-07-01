# SPDX-License-Identifier: MIT

"""Binary circuit-relay-v2 frame serialization — the cross-language wire format
for native, no-libp2p any-node relaying, carried in ``MeshPacket.payload``.

Any AetherNet node can act as a relay: a client that cannot reach a target
directly reserves capacity on a relay it *can* reach, asks the relay to bridge
to the target, and then tunnels data through the bridge. This is the native
equivalent of libp2p circuit-relay-v2's HOP/STOP protocol.

Conventions mirror ``dtn/envelope.py`` exactly so the eight language SDKs stay
byte-identical (and are pinned by the ``fixtures/circuit-relay/`` corpus): every
frame begins with a single format-version byte (readers reject any other value);
all multi-byte integers are little-endian; the 16-byte connection id is the UUID
in RFC-4122 big-endian order (``UUID.bytes``); strings are uint16-LE
length-prefixed UTF-8; the payload is int32-LE length-prefixed raw bytes and is
always the last field.

Layout (fixed, every field always present)::

    version u8 | type u8 | status u8
    srcUhid u16+utf8 | dstUhid u16+utf8 | relayUhid u16+utf8
    connId 16B(BE) | reservationExpiresAtMs i64 | limitDurationSeconds i32 | limitDataBytes i64
    payload i32+bytes

Minimum size (all strings empty, no payload): 49 bytes.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from enum import IntEnum
from uuid import UUID

RELAY_FRAME_VERSION = 0x01
_MAX_PAYLOAD = 16 * 1024 * 1024
_NIL_UUID = UUID(int=0)


class MessageType(IntEnum):
    """The circuit-relay-v2 verb carried by a :class:`RelayFrame`."""

    Reserve = 1
    ReserveResponse = 2
    Connect = 3
    Stop = 4
    StopResponse = 5
    ConnectResponse = 6
    Data = 7


class Status(IntEnum):
    """Result code carried by a relay response frame."""

    Ok = 0
    ReservationRefused = 1
    NoReservation = 2
    ResourceLimitExceeded = 3
    PermissionDenied = 4
    ConnectionFailed = 5
    MalformedMessage = 6


@dataclass
class RelayFrame:
    """A single circuit-relay-v2 wire frame. One fixed layout carries every verb
    (type-discriminated), so the format is trivial to keep byte-identical across
    every language SDK. It rides in ``MeshPacket.payload`` the same way the DTN
    envelope does.
    """

    type: MessageType = MessageType.Reserve
    status: Status = Status.Ok
    source_uhid: str = ""
    destination_uhid: str = ""
    relay_uhid: str = ""
    # Correlation id for a bridge session. The nil UUID (all-zero) means "none".
    connection_id: UUID = field(default=_NIL_UUID)
    reservation_expires_at_ms: int = 0
    limit_duration_seconds: int = 0
    limit_data_bytes: int = 0
    payload: bytes = b""


def serialize(f: RelayFrame) -> bytes:
    out = bytearray()
    out.append(RELAY_FRAME_VERSION)
    out.append(int(f.type) & 0xFF)
    out.append(int(f.status) & 0xFF)
    _write_str(out, f.source_uhid)
    _write_str(out, f.destination_uhid)
    _write_str(out, f.relay_uhid)
    # 16-byte connection id, RFC-4122 big-endian; None => nil UUID.
    conn = f.connection_id if f.connection_id is not None else _NIL_UUID
    out += conn.bytes
    out += struct.pack("<q", f.reservation_expires_at_ms)
    out += struct.pack("<i", f.limit_duration_seconds)
    out += struct.pack("<q", f.limit_data_bytes)
    payload = bytes(f.payload or b"")
    if len(payload) > _MAX_PAYLOAD:
        raise ValueError(f"Relay: payload too large ({len(payload)} bytes)")
    out += struct.pack("<i", len(payload))
    out += payload
    return bytes(out)


def deserialize(data: bytes) -> RelayFrame:
    r = _Reader(data)
    r.expect_version()

    type_raw = r.u8()
    if type_raw == 0 or type_raw > MessageType.Data:
        raise ValueError(f"Relay: invalid message type {type_raw}")
    status_raw = r.u8()
    if status_raw > Status.MalformedMessage:
        raise ValueError(f"Relay: invalid status {status_raw}")

    source_uhid = r.string()
    destination_uhid = r.string()
    relay_uhid = r.string()
    connection_id = r.uuid()
    reservation_expires_at_ms = r.i64()
    limit_duration_seconds = r.i32()
    limit_data_bytes = r.i64()
    payload = r.bytes32()

    return RelayFrame(
        type=MessageType(type_raw),
        status=Status(status_raw),
        source_uhid=source_uhid,
        destination_uhid=destination_uhid,
        relay_uhid=relay_uhid,
        connection_id=connection_id,
        reservation_expires_at_ms=reservation_expires_at_ms,
        limit_duration_seconds=limit_duration_seconds,
        limit_data_bytes=limit_data_bytes,
        payload=payload,
    )


# ── Low-level helpers (identical idiom to dtn/envelope.py) ─────────────────────


def _write_str(out: bytearray, s: str) -> None:
    data = (s or "").encode("utf-8")
    if len(data) > 65535:
        raise ValueError(f"Relay: string too long ({len(data)} bytes)")
    out += struct.pack("<H", len(data))
    out += data


class _Reader:
    def __init__(self, data: bytes) -> None:
        self._d = data
        self._o = 0

    def expect_version(self) -> None:
        v = self.u8()
        if v != RELAY_FRAME_VERSION:
            raise ValueError(f"Relay: unsupported frame version {v:#04x}")

    def u8(self) -> int:
        v = self._d[self._o]
        self._o += 1
        return v

    def uuid(self) -> UUID:
        b = bytes(self._d[self._o : self._o + 16])
        if len(b) != 16:
            raise ValueError("Relay: truncated uuid")
        self._o += 16
        return UUID(bytes=b)

    def i32(self) -> int:
        v = struct.unpack_from("<i", self._d, self._o)[0]
        self._o += 4
        return v

    def i64(self) -> int:
        v = struct.unpack_from("<q", self._d, self._o)[0]
        self._o += 8
        return v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self._d, self._o)[0]
        self._o += 2
        return v

    def string(self) -> str:
        n = self.u16()
        s = self._d[self._o : self._o + n].decode("utf-8")
        self._o += n
        return s

    def bytes32(self) -> bytes:
        n = self.i32()
        if n < 0 or n > _MAX_PAYLOAD:
            raise ValueError(f"Relay: invalid payload length {n}")
        b = bytes(self._d[self._o : self._o + n])
        self._o += n
        return b
