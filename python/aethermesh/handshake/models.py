# SPDX-License-Identifier: MIT

"""Handshake data models — Hello payload, negotiated peer capabilities,
and the IncompatiblePeer event payload.

The JSON shape MUST match the C# reference exactly (snake_case keys) — any
drift breaks cross-language interop.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import List, Optional, Set


@dataclass
class HelloPayload:
    """Wire payload carried inside a Hello or HelloAck packet's payload.

    JSON shape (snake_case to match the rest of the Aether wire format and
    the C# `HelloPayload` class):

        {
            "min_version": 1,
            "max_version": 2,
            "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
            "implementation": "aether-python/1.0.0"
        }

    Fields:
        min_version: lowest protocol version the announcer can speak (uint8).
        max_version: highest protocol version the announcer can speak (uint8).
        capabilities: capability tags advertised by the announcer.
        implementation: free-form implementation banner (diagnostic only,
            not used for compatibility decisions).
    """

    min_version: int = 0
    max_version: int = 0
    capabilities: List[str] = field(default_factory=list)
    implementation: str = ""

    def to_json_bytes(self) -> bytes:
        """Serialize to UTF-8 JSON bytes with snake_case keys, matching the
        C# `HelloPayloadJson.Options` (PropertyNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition.WhenWritingNull).

        We always emit all four fields — none of them is nullable in our
        model, so WhenWritingNull never fires.
        """
        obj = {
            "min_version": int(self.min_version),
            "max_version": int(self.max_version),
            "capabilities": list(self.capabilities),
            "implementation": self.implementation,
        }
        # separators tightens the output; no space matches System.Text.Json default.
        return json.dumps(obj, separators=(",", ":")).encode("utf-8")

    @classmethod
    def from_json_bytes(cls, data: bytes) -> "HelloPayload":
        """Parse a UTF-8 JSON-encoded HelloPayload. Raises ValueError on
        malformed input.

        Tolerant of missing fields (defaults applied) and extra fields
        (ignored), matching the C# JSON reader.
        """
        if data is None or len(data) == 0:
            raise ValueError("HelloPayload JSON body is empty")
        try:
            obj = json.loads(data.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as ex:
            raise ValueError(f"HelloPayload JSON parse failed: {ex}") from ex
        if not isinstance(obj, dict):
            raise ValueError("HelloPayload JSON root is not an object")

        min_v = int(obj.get("min_version", 0))
        max_v = int(obj.get("max_version", 0))
        # Clamp into uint8 range to match C#'s `byte` field. Out-of-range
        # values are treated as malformed.
        if not (0 <= min_v <= 255 and 0 <= max_v <= 255):
            raise ValueError(
                f"HelloPayload version out of byte range: min={min_v}, max={max_v}"
            )

        caps_raw = obj.get("capabilities", []) or []
        if not isinstance(caps_raw, list):
            raise ValueError("HelloPayload.capabilities is not a list")
        caps: List[str] = [str(c) for c in caps_raw if c is not None]

        impl = obj.get("implementation", "") or ""
        if not isinstance(impl, str):
            impl = str(impl)

        return cls(
            min_version=min_v,
            max_version=max_v,
            capabilities=caps,
            implementation=impl,
        )


@dataclass(frozen=True)
class PeerCapabilities:
    """Negotiated protocol-version + capability set for a remote peer.

    Locked in once the Hello/HelloAck exchange completes (or after the
    backward-compat fallback for peers that never replied).

    Fields:
        peer_uhid: UHID of the peer this record describes.
        negotiated_version: highest mutually-supported protocol version.
            Defaults to 1 for peers that never replied with a HelloAck.
        capabilities: intersection of capability tags both sides claim to
            support. Empty for peers that never replied.
        implementation_version: free-form implementation banner the peer
            announced. Empty for peers that never replied.
        negotiated_at: UTC timestamp when negotiation completed.
    """

    peer_uhid: str
    negotiated_version: int
    capabilities: frozenset
    implementation_version: str
    negotiated_at: datetime


@dataclass(frozen=True)
class IncompatiblePeerEvent:
    """Payload for the IncompatiblePeer event (raised when a peer's
    advertised version range does not overlap with ours).

    Mirrors the C# `IncompatiblePeerEventArgs` class.
    """

    peer_uhid: str
    their_min_version: int
    their_max_version: int
    our_min_version: int
    our_max_version: int
    reason: str
