# SPDX-License-Identifier: MIT

"""Aether Mesh Networking Protocol.

A decentralized mesh networking protocol designed for environments with
intermittent or absent internet connectivity.
"""

from __future__ import annotations

__version__ = "2.0.0"
__author__ = "The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V."
__license__ = "MIT"

# Eager re-exports — pure-Python, no third-party deps. Safe to import unconditionally.
from aethernet.models import AetherNetNode, PeerInfo, RouteEntry
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

# Voice services
from aethernet.voice.service import VoiceCallService, VoiceCallState, VoiceCallSession
from aethernet.voice.group_service import GroupVoiceCallService, GroupVoiceCallSession

# Streaming services
from aethernet.streaming.service import StreamingService, StreamSession, StreamState
from aethernet.streaming.video_service import VideoCallService, VideoCallSession, VideoCallState
from aethernet.streaming.watch_together import WatchTogetherService

# Security primitives use pynacl, which is an optional dep for hosts that don't
# need crypto (e.g. wire-format-only verifiers). Keep imports lazy so importing
# `aether` does not require pynacl to be installed.
from aethernet.gossip import (
    ReputationGossipService,
    ReputationUpdatePayload,
    REPUTATION_UPDATE_TYPE,
)


def __getattr__(name: str):
    if name in ("Ed25519SigningService",):
        from aethernet.security.ed25519_service import Ed25519SigningService
        return Ed25519SigningService
    if name in ("SignalProtocolService",):
        from aethernet.security.signal_protocol import SignalProtocolService
        return SignalProtocolService
    # Incentive layer — generic value-agnostic relay tips (TipPacket 24).
    if name in ("TipPacketPayload", "MeshTipService",
                "MeshTipSettlementProvider", "NoopMeshTipSettlementProvider"):
        import aethernet.incentive as _incentive
        return getattr(_incentive, name)
    # Vault layer — systematic Cauchy-Reed-Solomon erasure codec.
    if name in ("ReedSolomonCodec",):
        from aethernet.vault.reed_solomon import ReedSolomonCodec
        return ReedSolomonCodec
    # Market layer — Proof-of-Vicinity tokens (PoVTokenExchange 43).
    if name in ("PoVToken", "PoVTransportType", "PoVScore", "PoVTokenExchangeService"):
        import aethernet.market as _market
        return getattr(_market, name)
    raise AttributeError(f"module 'aether' has no attribute {name!r}")

__all__ = [
    "AetherNetNode",
    "PeerInfo",
    "RouteEntry",
    "MeshPacket",
    "PacketType",
    "Ed25519SigningService",
    "SignalProtocolService",
    # Voice
    "VoiceCallService",
    "VoiceCallState",
    "VoiceCallSession",
    "GroupVoiceCallService",
    "GroupVoiceCallSession",
    # Streaming
    "StreamingService",
    "StreamSession",
    "StreamState",
    "VideoCallService",
    "VideoCallSession",
    "VideoCallState",
    "WatchTogetherService",
    # Reputation gossip
    "ReputationGossipService",
    "ReputationUpdatePayload",
    "REPUTATION_UPDATE_TYPE",
    # Incentive (relay tips)
    "TipPacketPayload",
    "MeshTipService",
    "MeshTipSettlementProvider",
    "NoopMeshTipSettlementProvider",
    # Vault (erasure coding)
    "ReedSolomonCodec",
    # Market (Proof-of-Vicinity)
    "PoVToken",
    "PoVTransportType",
    "PoVScore",
    "PoVTokenExchangeService",
]
