"""Capability handshake (Hello / HelloAck) for protocol-version negotiation.

Mirrors the C# `Aether.Handshake` namespace. Peers exchange a Hello/HelloAck
pair on first contact; each side announces the protocol-version range it can
speak and the capability tags it supports. The receiver replies with the
highest mutually-supported version + the intersection of capability tags.

Wire format (UTF-8 JSON, snake_case to match the rest of the Aether wire
format):

    {
        "min_version": 1,
        "max_version": 2,
        "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
        "implementation": "aether-python/1.0.0"
    }

Security: this payload is NEITHER encrypted NOR authenticated by design — it
runs before any Signal session exists. Peer identity is verified later via
Ed25519 packet signatures on data packets. Treat the announced capabilities
as a hint, not a security claim.
"""

from aether.handshake.models import (
    HelloPayload,
    PeerCapabilities,
    IncompatiblePeerEvent,
)
from aether.handshake.service import HandshakeService

__all__ = [
    "HelloPayload",
    "PeerCapabilities",
    "IncompatiblePeerEvent",
    "HandshakeService",
]
