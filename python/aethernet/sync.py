# SPDX-License-Identifier: MIT

"""Decentralised multi-device sync — no server.

Three cooperating pieces let a user's devices converge on the same state by
gossiping opaque records to one another, with no central coordinator:

* :class:`SyncRecord` / :func:`serialize_record` / :func:`deserialize_record` —
  the binary wire format for one state change (upsert / delete / read-marker) a
  device emits and gossips to the user's other devices. The payload is already
  end-to-end encrypted, so any relaying node learns nothing.
* :func:`winner` / :func:`merge` — deterministic last-write-wins reconciliation.
  Every device that sees the same set of records, in any order, over any path,
  picks the identical winner per item.
* :class:`DeviceLink` / :func:`device_link_*` — a device-membership record signed
  by the user's long-term Ed25519 identity key. Because Ed25519 signatures are
  deterministic, the serialized link is byte-identical across SDKs.

Wire conventions mirror the DTN envelope serializer: multi-byte integers are
little-endian; the 16-byte ``record_id`` is the UUID in RFC-4122 big-endian order
(``UUID.bytes``); strings are uint16-LE length-prefixed UTF-8; the payload is
int32-LE length-prefixed raw bytes. Every envelope opens with a single
format-version byte so the format can evolve without a flag-day. Verified
byte-for-byte against ``fixtures/sync/vectors.json``.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Dict, Iterable, Optional
from uuid import UUID

from aethernet.security.ed25519_service import Ed25519SigningService

SYNC_RECORD_VERSION = 0x01
DEVICE_LINK_VERSION = 0x01

_U16_MAX = 0xFFFF
# A SyncRecord's fixed framing: version + record_id + op + 2×i64 + 2×u16 str-len + i32 payload-len.
_SYNC_RECORD_MIN_LEN = 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4
# A DeviceLink's fixed framing: version + u16 id-len + 32 pubkey + i64 issued + 64 signature.
_DEVICE_LINK_MIN_LEN = 1 + 2 + 32 + 8 + 64


class SyncOp(IntEnum):
    """The kind of state change a :class:`SyncRecord` carries."""

    UPSERT = 0
    """Create or update the item."""
    DELETE = 1
    """Delete the item."""
    READ = 2
    """Mark the item read (read-state sync)."""


@dataclass(frozen=True)
class SyncRecord:
    """One state change to a synced item (a message, a read-marker, a deletion),
    emitted by one of a user's devices and gossiped to that user's other devices
    so they all converge on the same state — with no server.

    ``encrypted_payload`` is already end-to-end encrypted to the user's device
    set, so any node that relays the record learns nothing about its content.
    """

    record_id: UUID
    device_id: str
    op: SyncOp
    item_id: str
    logical_clock: int
    created_at_ms: int
    encrypted_payload: bytes = b""


# ── SyncRecord wire format ────────────────────────────────────────────────────


def serialize_record(record: SyncRecord) -> bytes:
    """Serialize a :class:`SyncRecord` to its canonical bytes.

    Layout: version(u8=1) · record_id(16, big-endian) · op(u8) ·
    logical_clock(i64 LE) · created_at_ms(i64 LE) · device_id(u16 len + utf8) ·
    item_id(u16 len + utf8) · encrypted_payload(i32 len + bytes).
    """
    if record is None:
        raise ValueError("record cannot be None")

    device = (record.device_id or "").encode("utf-8")
    item = (record.item_id or "").encode("utf-8")
    payload = bytes(record.encrypted_payload or b"")
    if len(device) > _U16_MAX:
        raise ValueError("device_id is too long")
    if len(item) > _U16_MAX:
        raise ValueError("item_id is too long")

    out = bytearray()
    out.append(SYNC_RECORD_VERSION)
    out += record.record_id.bytes  # RFC-4122 big-endian, matches C# Guid(bigEndian: true)
    out.append(int(record.op) & 0xFF)
    out += struct.pack("<q", record.logical_clock)
    out += struct.pack("<q", record.created_at_ms)
    _write_str(out, device)
    _write_str(out, item)
    out += struct.pack("<i", len(payload))
    out += payload
    return bytes(out)


def deserialize_record(data: bytes) -> SyncRecord:
    """Parse canonical bytes back into a :class:`SyncRecord`, validating framing."""
    if data is None:
        raise ValueError("data cannot be None")
    if len(data) < _SYNC_RECORD_MIN_LEN:
        raise ValueError("SyncRecord is too short")

    o = 0
    if data[o] != SYNC_RECORD_VERSION:
        raise ValueError("Unsupported SyncRecord format version")
    o += 1

    record_id = UUID(bytes=bytes(data[o : o + 16]))
    o += 16
    op_byte = data[o]
    o += 1
    if op_byte > int(SyncOp.READ):
        raise ValueError("Unknown SyncRecord op")
    op = SyncOp(op_byte)
    logical_clock = struct.unpack_from("<q", data, o)[0]
    o += 8
    created_at_ms = struct.unpack_from("<q", data, o)[0]
    o += 8
    device_id, o = _read_str(data, o)
    item_id, o = _read_str(data, o)

    if o + 4 > len(data):
        raise ValueError("SyncRecord payload length is truncated")
    payload_len = struct.unpack_from("<i", data, o)[0]
    o += 4
    if payload_len < 0 or o + payload_len > len(data):
        raise ValueError("SyncRecord payload length is invalid")
    payload = bytes(data[o : o + payload_len])

    return SyncRecord(
        record_id=record_id,
        device_id=device_id,
        op=op,
        item_id=item_id,
        logical_clock=logical_clock,
        created_at_ms=created_at_ms,
        encrypted_payload=payload,
    )


# ── Reconciliation (deterministic last-write-wins) ────────────────────────────


def _sort_key(r: SyncRecord):
    """Total order (later wins): created_at_ms, then logical_clock, then device_id
    (ordinal, i.e. UTF-8 code-unit order), then record_id bytes. The last two are
    arbitrary-but-stable tie-breakers so genuinely concurrent writes still resolve
    the same way on every device."""
    return (
        r.created_at_ms,
        r.logical_clock,
        (r.device_id or "").encode("utf-8"),
        r.record_id.bytes,
    )


def winner(records: Iterable[SyncRecord]) -> SyncRecord:
    """The winning record among ``records`` (all assumed to be for one item).

    Raises :class:`ValueError` if the sequence is empty.
    """
    if records is None:
        raise ValueError("records cannot be None")
    best: Optional[SyncRecord] = None
    best_key = None
    for r in records:
        k = _sort_key(r)
        if best is None or k > best_key:
            best = r
            best_key = k
    if best is None:
        raise ValueError("No records to reconcile")
    return best


def merge(records: Iterable[SyncRecord]) -> Dict[str, SyncRecord]:
    """Merge records into the winning record per :attr:`SyncRecord.item_id` — the
    converged view of a device's local state."""
    if records is None:
        raise ValueError("records cannot be None")
    result: Dict[str, SyncRecord] = {}
    best_keys: Dict[str, object] = {}
    for r in records:
        key = r.item_id or ""
        k = _sort_key(r)
        if key not in result or k > best_keys[key]:
            result[key] = r
            best_keys[key] = k
    return result


# ── DeviceLink ────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class DeviceLink:
    """A signed device-membership record. A user links a new device by having
    their long-term Ed25519 identity key sign the new device's own public key;
    every other device verifies that signature to admit the newcomer into the
    "self" device set — no central directory, no server."""

    device_id: str
    device_public_key: bytes
    issued_at_ms: int
    signature: bytes = field(default=b"")


def device_link_signed_body(
    device_id: str, device_public_key: bytes, issued_at_ms: int
) -> bytes:
    """The canonical signed body (everything but the signature): version ·
    device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE).
    Signer and verifier operate over exactly these bytes."""
    if device_id is None:
        raise ValueError("device_id cannot be None")
    if device_public_key is None:
        raise ValueError("device_public_key cannot be None")
    if len(device_public_key) != 32:
        raise ValueError("Device public key must be 32 bytes")

    device = device_id.encode("utf-8")
    if len(device) > _U16_MAX:
        raise ValueError("device_id is too long")

    out = bytearray()
    out.append(DEVICE_LINK_VERSION)
    _write_str(out, device)
    out += bytes(device_public_key)
    out += struct.pack("<q", issued_at_ms)
    return bytes(out)


def device_link_create(
    device_id: str,
    device_public_key: bytes,
    issued_at_ms: int,
    identity_private_key: bytes,
) -> DeviceLink:
    """Create a device-link signed by the user's 32-byte Ed25519 identity seed."""
    body = device_link_signed_body(device_id, device_public_key, issued_at_ms)
    signature = Ed25519SigningService.sign(identity_private_key, body)
    return DeviceLink(
        device_id=device_id,
        device_public_key=bytes(device_public_key),
        issued_at_ms=issued_at_ms,
        signature=signature,
    )


def device_link_verify(link: DeviceLink, identity_public_key: bytes) -> bool:
    """True if ``link`` was signed by the identity behind ``identity_public_key``
    — i.e. this device belongs to that user."""
    if link is None or identity_public_key is None:
        return False
    if link.signature is None or len(link.signature) != 64:
        return False
    if link.device_public_key is None or len(link.device_public_key) != 32:
        return False

    body = device_link_signed_body(
        link.device_id, link.device_public_key, link.issued_at_ms
    )
    return Ed25519SigningService.verify(identity_public_key, body, link.signature)


def device_link_serialize(link: DeviceLink) -> bytes:
    """Serialize a link as its signed body followed by the 64-byte signature."""
    if link is None:
        raise ValueError("link cannot be None")
    if link.signature is None or len(link.signature) != 64:
        raise ValueError("Signature must be 64 bytes")

    body = device_link_signed_body(
        link.device_id, link.device_public_key, link.issued_at_ms
    )
    return body + bytes(link.signature)


def device_link_deserialize(data: bytes) -> DeviceLink:
    """Parse a serialized link, validating framing."""
    if data is None:
        raise ValueError("data cannot be None")
    if len(data) < _DEVICE_LINK_MIN_LEN:
        raise ValueError("DeviceLink is too short")

    o = 0
    if data[o] != DEVICE_LINK_VERSION:
        raise ValueError("Unsupported DeviceLink format version")
    o += 1

    id_len = struct.unpack_from("<H", data, o)[0]
    o += 2
    if o + id_len + 32 + 8 + 64 > len(data):
        raise ValueError("DeviceLink is truncated")
    device_id = data[o : o + id_len].decode("utf-8")
    o += id_len
    device_public_key = bytes(data[o : o + 32])
    o += 32
    issued_at_ms = struct.unpack_from("<q", data, o)[0]
    o += 8
    signature = bytes(data[o : o + 64])

    return DeviceLink(
        device_id=device_id,
        device_public_key=device_public_key,
        issued_at_ms=issued_at_ms,
        signature=signature,
    )


# ── Low-level helpers ─────────────────────────────────────────────────────────


def _write_str(out: bytearray, utf8: bytes) -> None:
    out += struct.pack("<H", len(utf8))
    out += utf8


def _read_str(data: bytes, o: int) -> tuple[str, int]:
    if o + 2 > len(data):
        raise ValueError("SyncRecord string length is truncated")
    n = struct.unpack_from("<H", data, o)[0]
    o += 2
    if o + n > len(data):
        raise ValueError("SyncRecord string is truncated")
    s = data[o : o + n].decode("utf-8")
    o += n
    return s, o
