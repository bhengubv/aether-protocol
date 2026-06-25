# SPDX-License-Identifier: MIT

"""Binary DTN-envelope serialization — the cross-language wire format for the
three DTN packet bodies (bundle / custody-ack / delivery-receipt) carried in
``MeshPacket.payload``.

Conventions mirror the packet serializer: all multi-byte integers little-endian;
the 16-byte bundle id is the UUID in RFC-4122 big-endian order (``UUID.bytes``);
strings are uint16-LE length-prefixed UTF-8; the encrypted payload is int32-LE
length-prefixed raw bytes. Every envelope begins with a single format-version
byte so the format can evolve without a flag-day — a reader rejects any unknown
version.

Cleartext routing fields are laid out first and the opaque encrypted payload
last, so a later version can encrypt sender/recipient with no field-shuffle.
"""

from __future__ import annotations

import struct
from datetime import datetime, timezone
from uuid import UUID

from aethernet.models import BundlePriority, BundleStatus, DtnBundle

DTN_ENVELOPE_VERSION = 0x01
_MAX_PAYLOAD = 16 * 1024 * 1024


# ── Bundle ────────────────────────────────────────────────────────────────────

def serialize_bundle(b: DtnBundle) -> bytes:
    out = bytearray()
    out.append(DTN_ENVELOPE_VERSION)
    out += b.id.bytes
    out.append(int(b.priority) & 0xFF)
    out.append(int(b.status) & 0xFF)
    out += struct.pack("<iii", b.copy_count, b.max_copies, b.hop_count)
    out += struct.pack("<qq", _to_unix_ms(b.created_at), _to_unix_ms(b.expires_at))
    _write_str(out, b.sender_uhid)
    _write_str(out, b.recipient_uhid)
    _write_str(out, b.sender_geohash or "")
    _write_str(out, b.recipient_last_geohash or "")
    payload = bytes(b.encrypted_payload)
    if len(payload) > _MAX_PAYLOAD:
        raise ValueError(f"DTN: payload too large ({len(payload)} bytes)")
    out += struct.pack("<i", len(payload))
    out += payload
    return bytes(out)


def deserialize_bundle(data: bytes) -> DtnBundle:
    r = _Reader(data)
    r.expect_version()
    bundle_id = r.uuid()
    priority_raw = r.u8()
    status_raw = r.u8()
    try:
        priority = BundlePriority(priority_raw)
        status = BundleStatus(status_raw)
    except ValueError as exc:
        raise ValueError(f"DTN: invalid enum (priority={priority_raw}, status={status_raw})") from exc
    copy_count, max_copies, hop_count = r.i32(), r.i32(), r.i32()
    created_at = _from_unix_ms(r.i64())
    expires_at = _from_unix_ms(r.i64())
    sender_uhid = r.string()
    recipient_uhid = r.string()
    sender_geohash = r.string()
    recipient_last_geohash = r.string()
    encrypted_payload = r.bytes32()
    return DtnBundle(
        id=bundle_id,
        sender_uhid=sender_uhid,
        recipient_uhid=recipient_uhid,
        encrypted_payload=encrypted_payload,
        priority=priority,
        status=status,
        copy_count=copy_count,
        max_copies=max_copies,
        sender_geohash=sender_geohash,
        recipient_last_geohash=recipient_last_geohash,
        hop_count=hop_count,
        created_at=created_at,
        expires_at=expires_at,
    )


# ── Custody-ack ───────────────────────────────────────────────────────────────

def serialize_custody_ack(bundle_id: UUID, accepted: bool) -> bytes:
    out = bytearray()
    out.append(DTN_ENVELOPE_VERSION)
    out += bundle_id.bytes
    out.append(0x01 if accepted else 0x00)
    return bytes(out)


def deserialize_custody_ack(data: bytes) -> tuple[UUID, bool]:
    r = _Reader(data)
    r.expect_version()
    bundle_id = r.uuid()
    accepted = r.u8() != 0
    return bundle_id, accepted


# ── Delivery-receipt ──────────────────────────────────────────────────────────

def serialize_delivery_receipt(
    bundle_id: UUID,
    recipient_uhid: str,
    total_hops: int,
    total_custody_transfers: int,
    delivered_at_ms: int,
) -> bytes:
    out = bytearray()
    out.append(DTN_ENVELOPE_VERSION)
    out += bundle_id.bytes
    _write_str(out, recipient_uhid)
    out += struct.pack("<ii", total_hops, total_custody_transfers)
    out += struct.pack("<q", delivered_at_ms)
    return bytes(out)


def deserialize_delivery_receipt(data: bytes) -> tuple[UUID, str, int, int, int]:
    r = _Reader(data)
    r.expect_version()
    bundle_id = r.uuid()
    recipient_uhid = r.string()
    total_hops = r.i32()
    total_custody_transfers = r.i32()
    delivered_at_ms = r.i64()
    return bundle_id, recipient_uhid, total_hops, total_custody_transfers, delivered_at_ms


# ── Low-level helpers ─────────────────────────────────────────────────────────

def _to_unix_ms(dt: datetime) -> int:
    # Treat naive datetimes as UTC (the model populates datetime.utcnow()).
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return int(dt.timestamp() * 1000)


def _from_unix_ms(ms: int) -> datetime:
    # Naive UTC datetime, matching the model's convention.
    return datetime.utcfromtimestamp(ms / 1000)


def _write_str(out: bytearray, s: str) -> None:
    data = s.encode("utf-8")
    if len(data) > 65535:
        raise ValueError(f"DTN: string too long ({len(data)} bytes)")
    out += struct.pack("<H", len(data))
    out += data


class _Reader:
    def __init__(self, data: bytes) -> None:
        self._d = data
        self._o = 0

    def expect_version(self) -> None:
        v = self.u8()
        if v != DTN_ENVELOPE_VERSION:
            raise ValueError(f"DTN: unsupported envelope version {v:#04x}")

    def u8(self) -> int:
        v = self._d[self._o]
        self._o += 1
        return v

    def uuid(self) -> UUID:
        b = bytes(self._d[self._o : self._o + 16])
        if len(b) != 16:
            raise ValueError("DTN: truncated uuid")
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
            raise ValueError(f"DTN: invalid payload length {n}")
        b = bytes(self._d[self._o : self._o + n])
        self._o += n
        return b
