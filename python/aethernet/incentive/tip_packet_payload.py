# SPDX-License-Identifier: MIT
#
# Generic "value-earned" relay-tip envelope carried inside a PacketType.TipPacket (24). Python port of
# AetherNet.Incentive.TipPacketPayload, byte-identical to the C# reference and every other language
# implementation.
#
# This model is deliberately value-agnostic. ``amount`` is a bare number with NO units, NO policy, and
# NO settlement semantics attached at the protocol layer. The protocol carries the signal that one node
# wishes to credit another for some kind of relayed traffic; what (if anything) that signal is worth is
# entirely the host's business. A bare node accepts and relays the packet but settles nothing — only a
# host that has wired a MeshTipSettlementProvider override decides how to interpret the value.
#
# The payload is self-signed by the tipper: ``signature`` is an Ed25519 signature over the canonical
# byte layout produced by ``build_canonical_data``. The signature binds the tipper, recipient, amount,
# traffic type, reference, and timestamp together so an intermediate relay cannot tamper with any field
# without invalidating it.

from __future__ import annotations

import json
import struct
from dataclasses import dataclass
from typing import Optional
from uuid import UUID

# 16 zero bytes — the canonical encoding of a null reference_id (Guid.Empty in .NET).
_EMPTY_GUID_BYTES = b"\x00" * 16


@dataclass
class TipPacketPayload:
    """The JSON body (snake_case) carried inside a TipPacket(24).

    ``amount`` is the INVARIANT decimal string (the .NET ``decimal.ToString(InvariantCulture)``
    round-trip form, e.g. ``"12.50"``, ``"0.0001"``, ``"123456.789"``) — NOT a float. Keeping it a
    string is what makes the signed bytes stable across locales and decimal scales without baking in any
    unit or fixed-point assumption, and is required for byte-identity with the C# canonical data.
    """

    # UHID of the node offering the tip (the signer of this payload).
    tipper_uhid: str = ""

    # UHID of the node the tip is addressed to.
    recipient_uhid: str = ""

    # Generic value being credited, as the invariant decimal string. The protocol imposes NO unit,
    # NO minimum, NO maximum, and NO policy.
    amount: str = "0"

    # Free-form tag describing the kind of relayed traffic this tip is for, e.g. "message-relay" or
    # "gateway-share". Opaque to the protocol.
    traffic_type: str = ""

    # Optional correlation id linking this tip to some host-defined unit of work. ``None`` when the tip
    # stands alone (serialised as 16 zero bytes in the canonical data).
    reference_id: Optional[UUID] = None

    # When the tipper created this payload, in Unix milliseconds.
    timestamp_unix_ms: int = 0

    # Ed25519 signature over ``build_canonical_data``, produced by the tipper's identity key. ``None``
    # until the payload has been signed.
    signature: Optional[bytes] = None

    def build_canonical_data(self) -> bytes:
        """Build the canonical byte array that is signed/verified for this payload. The ``signature``
        field itself is excluded from the canonical data.

        Layout (little-endian lengths, matching PacketSigningService._construct_signable_data
        conventions)::

            TipperLen(4 LE i32)    || Tipper(UTF-8)
            RecipientLen(4 LE i32) || Recipient(UTF-8)
            AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
            TrafficLen(4 LE i32)   || TrafficType(UTF-8)
            ReferenceId(16, all-zero GUID when None, .NET mixed-endian byte order)
            TimestampUnixMs(8 LE i64)
        """
        tipper_bytes = self.tipper_uhid.encode("utf-8")
        recipient_bytes = self.recipient_uhid.encode("utf-8")
        amount_bytes = self.amount.encode("utf-8")
        traffic_bytes = self.traffic_type.encode("utf-8")

        parts: list[bytes] = []
        parts.append(_write_length_prefixed(tipper_bytes))
        parts.append(_write_length_prefixed(recipient_bytes))
        parts.append(_write_length_prefixed(amount_bytes))
        parts.append(_write_length_prefixed(traffic_bytes))

        # ReferenceId — 16 bytes, all-zero when None, .NET GUID byte order otherwise. Python's
        # UUID.bytes_le yields exactly the mixed-endian layout System.Guid.TryWriteBytes produces
        # (Data1: 4 bytes LE, Data2: 2 bytes LE, Data3: 2 bytes LE, Data4: 8 bytes as-is).
        if self.reference_id is None:
            parts.append(_EMPTY_GUID_BYTES)
        else:
            parts.append(self.reference_id.bytes_le)

        # Timestamp — Unix milliseconds, little-endian int64.
        parts.append(struct.pack("<q", self.timestamp_unix_ms))

        return b"".join(parts)

    def to_json(self) -> bytes:
        """Serialise the payload to its snake_case UTF-8 JSON wire form.

        Mirrors the C# serializer: ``reference_id`` is the canonical 8-4-4-4-12 string (or omitted when
        ``None``), ``signature`` is hex (or omitted when unsigned), ``timestamp`` is the i64 Unix-ms
        value. ``amount`` is the invariant decimal string verbatim.
        """
        obj: dict = {
            "tipper_uhid": self.tipper_uhid,
            "recipient_uhid": self.recipient_uhid,
            "amount": self.amount,
            "traffic_type": self.traffic_type,
            "timestamp": self.timestamp_unix_ms,
        }
        if self.reference_id is not None:
            obj["reference_id"] = str(self.reference_id)
        if self.signature is not None:
            obj["signature"] = self.signature.hex()
        return json.dumps(obj).encode("utf-8")

    @classmethod
    def from_json(cls, data: bytes) -> "TipPacketPayload":
        """Deserialise a snake_case UTF-8 JSON tip payload."""
        obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
        ref = obj.get("reference_id")
        sig = obj.get("signature")
        return cls(
            tipper_uhid=obj.get("tipper_uhid", ""),
            recipient_uhid=obj.get("recipient_uhid", ""),
            amount=obj.get("amount", "0"),
            traffic_type=obj.get("traffic_type", ""),
            reference_id=UUID(ref) if ref else None,
            timestamp_unix_ms=int(obj.get("timestamp", 0)),
            signature=bytes.fromhex(sig) if sig else None,
        )


def _write_length_prefixed(value: bytes) -> bytes:
    """Return a 4-byte LE int32 length prefix followed by ``value``."""
    return struct.pack("<i", len(value)) + value
