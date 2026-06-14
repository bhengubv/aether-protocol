# SPDX-License-Identifier: MIT
#
# Proof-of-Vicinity token model and canonical signable-body codec. Python port of
# AetherNet.Market.Models.PoVToken / PoVTransportType / PoVScore and AetherNet.Market.PoVTokenCodec.
#
# The canonical body that BOTH the witness and the subject sign with their real Ed25519 identity keys
# must stay byte-identical across every language implementation so a token signed by one node verifies
# on any other:
#
#   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
#
# timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).

from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Optional

# Number of .NET DateTime ticks (100ns) per second.
_TICKS_PER_SECOND = 10_000_000

# The .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z) — i.e. ticks between
# 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and Python datetime.
_UNIX_EPOCH_TICKS = 621_355_968_000_000_000


class PoVTransportType(IntEnum):
    """The transport used for a co-presence Proof-of-Vicinity exchange. Only short-range transports are
    valid (prevents remote minting)."""

    # Bluetooth Low Energy (short range — prevents remote forgery).
    Ble = 0
    # Near-Field Communication (requires physical proximity).
    Nfc = 1
    # Huawei NearLink (short range, similar to BLE).
    NearLink = 2

    def is_short_range(self) -> bool:
        """Whether the transport is a valid short-range PoV channel."""
        return self in (PoVTransportType.Ble, PoVTransportType.Nfc, PoVTransportType.NearLink)

    @property
    def wire_name(self) -> str:
        """The lowercase wire name of the transport (``ble`` / ``nfc`` / ``nearlink``)."""
        return {
            PoVTransportType.Ble: "ble",
            PoVTransportType.Nfc: "nfc",
            PoVTransportType.NearLink: "nearlink",
        }[self]


def build_signable_token_data(
    subject_uhid: str, timestamp_ticks: int, transport: PoVTransportType
) -> bytes:
    """Build the canonical signable bytes for a PoV token body. The same layout is signed by the witness
    (on issue) and counter-signed by the subject (on accept)::

        SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
    """
    subject_bytes = subject_uhid.encode("utf-8")
    return (
        struct.pack("<i", len(subject_bytes))
        + subject_bytes
        + struct.pack("<q", timestamp_ticks)
        + bytes([int(transport) & 0xFF])
    )


@dataclass
class PoVToken:
    """A Proof-of-Vicinity token issued by one node (the witness) to another (the subject) during a
    physical co-presence event.

    Both parties must countersign — this prevents unilateral forgery. The token is transmitted over a
    short-range transport (BLE/NFC/NearLink only) to prevent remote minting. The JSON wire form is
    snake_case, matching the C# serializer.
    """

    # UHID of the node issuing the voucher.
    witness_uhid: str = ""

    # UHID of the node being vouched for.
    subject_uhid: str = ""

    # The co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01). Stored as ticks (not a
    # Python datetime) so the signed canonical body is byte-identical to C#.
    timestamp_ticks: int = 0

    # The transport channel used (must be short-range).
    transport_used: PoVTransportType = PoVTransportType.Ble

    # Ed25519 signature by the witness over the canonical body.
    witness_signature: Optional[bytes] = None

    # Ed25519 countersignature by the subject — required for token validity.
    subject_signature: Optional[bytes] = None

    def signable_data(self) -> bytes:
        """The canonical signable bytes for this token."""
        return build_signable_token_data(
            self.subject_uhid, self.timestamp_ticks, self.transport_used
        )

    def to_json(self) -> bytes:
        """Serialise the token to its snake_case UTF-8 JSON wire form. Signatures are hex (or omitted
        when absent)."""
        obj: dict = {
            "witness_uhid": self.witness_uhid,
            "subject_uhid": self.subject_uhid,
            "timestamp_ticks": self.timestamp_ticks,
            "transport_used": int(self.transport_used),
        }
        if self.witness_signature is not None:
            obj["witness_signature"] = self.witness_signature.hex()
        if self.subject_signature is not None:
            obj["subject_signature"] = self.subject_signature.hex()
        return json.dumps(obj).encode("utf-8")

    @classmethod
    def from_json(cls, data: bytes) -> "PoVToken":
        """Deserialise a snake_case UTF-8 JSON PoV token."""
        obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
        wsig = obj.get("witness_signature")
        ssig = obj.get("subject_signature")
        return cls(
            witness_uhid=obj.get("witness_uhid", ""),
            subject_uhid=obj.get("subject_uhid", ""),
            timestamp_ticks=int(obj.get("timestamp_ticks", 0)),
            transport_used=PoVTransportType(int(obj.get("transport_used", 0))),
            witness_signature=bytes.fromhex(wsig) if wsig else None,
            subject_signature=bytes.fromhex(ssig) if ssig else None,
        )


def ticks_to_datetime(ticks: int) -> datetime:
    """Convert a .NET DateTime.Ticks value to a timezone-aware UTC ``datetime``. Provided for hosts that
    want a Python datetime; the canonical body always uses the raw ticks."""
    unix_ticks = ticks - _UNIX_EPOCH_TICKS
    micros = unix_ticks // 10  # 100ns ticks -> microseconds
    return datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=micros)


def datetime_to_ticks(dt: datetime) -> int:
    """Convert a ``datetime`` to a .NET DateTime.Ticks value. Naive datetimes are assumed to be UTC."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    delta = dt.astimezone(timezone.utc) - datetime(1970, 1, 1, tzinfo=timezone.utc)
    # delta.microseconds resolution is 1us = 10 ticks; sub-microsecond .NET precision is not
    # representable in a Python datetime, so this round-trips at microsecond (10-tick) resolution.
    total_micros = (delta.days * 86_400 + delta.seconds) * 1_000_000 + delta.microseconds
    return total_micros * 10 + _UNIX_EPOCH_TICKS


@dataclass
class PoVScore:
    """The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil routing/identity signal
    that attaches NO value semantics."""

    # UHID of the scored node.
    uhid: str = ""
    # Number of distinct witnesses who have issued PoV tokens to this node.
    unique_witnesses: int = 0
    # Weighted score (0.0–1.0).
    weighted_score: float = 0.0
    # Time of the most recent score update.
    last_updated: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
