# SPDX-License-Identifier: MIT

"""Reputation gossip service — propagates NodeReputationService signals across the mesh.

Packet type 52 carries a signed ReputationUpdatePayload. Inbound updates are
weighted by the reporter's own reputation score so that degraded reporters
have reduced influence on the target's score.
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, asdict
from typing import TYPE_CHECKING, Protocol

if TYPE_CHECKING:
    from aether.reputation import NodeReputationService

REPUTATION_UPDATE_TYPE: int = 52
FRESHNESS_WINDOW_MS: int = 5 * 60 * 1000  # 5 minutes


@dataclass
class ReputationUpdatePayload:
    """Wire representation of a single reputation update event."""

    reporter_uhid: str
    target_uhid: str
    score_delta: float
    timestamp_ms: int
    reason: str


class MeshSender(Protocol):
    """Minimal interface the gossip service needs to send packets onto the mesh."""

    @property
    def local_uhid(self) -> str: ...  # noqa: E704

    def broadcast(self, packet: dict) -> int: ...  # noqa: E704


class PacketSigner(Protocol):
    """Minimal interface for packet signing and verification."""

    def sign_packet(self, packet: dict) -> dict: ...  # noqa: E704

    def verify_packet(self, packet: dict, sender_public_key: bytes) -> bool: ...  # noqa: E704


class ReputationGossipService:
    """Broadcasts and processes signed reputation-update gossip packets.

    Args:
        sender:     Mesh transport used to broadcast outgoing packets.
        signing:    Signing/verification service (e.g. Ed25519).
        reputation: Local reputation store that receives accepted updates.
    """

    def __init__(
        self,
        sender: MeshSender,
        signing: PacketSigner,
        reputation: "NodeReputationService",
    ) -> None:
        self._sender = sender
        self._signing = signing
        self._reputation = reputation

    # ── Public API ───────────────────────────────────────────────────────────

    def broadcast_reputation_update(
        self, target_uhid: str, score_delta: float, reason: str
    ) -> int:
        """Build and broadcast a signed ReputationUpdate packet.

        Args:
            target_uhid: UHID of the node whose reputation is being reported.
            score_delta: Raw delta in [-1, 1]; clamped before serialisation.
            reason:      Human-readable description of the observed event.

        Returns:
            Number of peers the packet was delivered to (as reported by the
            underlying transport).
        """
        local_uhid = self._sender.local_uhid
        now_ms = _now_ms()

        clamped_delta = max(-1.0, min(1.0, score_delta))

        payload = ReputationUpdatePayload(
            reporter_uhid=local_uhid,
            target_uhid=target_uhid,
            score_delta=clamped_delta,
            timestamp_ms=now_ms,
            reason=reason,
        )

        json_str = json.dumps(asdict(payload))

        packet: dict = {
            "type": REPUTATION_UPDATE_TYPE,
            "source_uhid": local_uhid,
            "destination_uhid": "*",
            "ttl": 3,
            "payload": json_str,
            "timestamp_ms": now_ms,
        }

        signed = self._signing.sign_packet(packet)
        return self._sender.broadcast(signed)

    def handle_gossip_packet(self, packet: dict, sender_public_key: bytes) -> bool:
        """Process an inbound ReputationUpdate packet.

        Steps:
          1. Reject if packet type != 52.
          2. Reject if signature verification fails.
          3. Reject if the payload timestamp is outside the freshness window.
          4. Reject if reporter_uhid or target_uhid is empty.
          5. Reject if reporter_uhid == local_uhid (own-echo suppression).
          6. Weight the delta by the reporter's reputation score and apply.

        Args:
            packet:            The received packet dict.
            sender_public_key: Public key used to verify the packet signature.

        Returns:
            True if the update was accepted and applied; False otherwise.
        """
        # 1. Type guard
        if packet.get("type") != REPUTATION_UPDATE_TYPE:
            return False

        # 2. Signature verification
        if not self._signing.verify_packet(packet, sender_public_key):
            return False

        # 3. Parse payload
        try:
            raw = json.loads(packet["payload"])
            payload = ReputationUpdatePayload(**raw)
        except (KeyError, TypeError, ValueError):
            return False

        # 4. Freshness check
        now_ms = _now_ms()
        if abs(now_ms - payload.timestamp_ms) > FRESHNESS_WINDOW_MS:
            return False

        # 5. Validate non-empty UHIDs
        if not payload.reporter_uhid or not payload.target_uhid:
            return False

        # 6. Own-echo suppression
        if payload.reporter_uhid == self._sender.local_uhid:
            return False

        # 7. Clamp the claimed delta
        clamped_delta = max(-1.0, min(1.0, payload.score_delta))

        # 8. Weight by reporter reputation (unknown reporters default to 1.0)
        reporter_reputation = self._reputation.get_reputation_score(payload.reporter_uhid)

        # 9. Compute effective delta
        effective_delta = clamped_delta * reporter_reputation

        # 10. Apply to the target
        self._reputation.apply_weighted_delta(payload.target_uhid, effective_delta)

        return True


# ── Helpers ──────────────────────────────────────────────────────────────────

def _now_ms() -> int:
    """Return the current wall-clock time in milliseconds."""
    return int(time.time() * 1000)
