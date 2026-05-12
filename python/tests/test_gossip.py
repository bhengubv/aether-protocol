# SPDX-License-Identifier: MIT

"""Unit tests for ReputationGossipService (Item 27).

Run with: python -m pytest tests/test_gossip.py -v
"""

from __future__ import annotations

import json
import math
import time
import unittest
from dataclasses import asdict
from typing import List, Tuple

from aether.gossip import (
    FRESHNESS_WINDOW_MS,
    REPUTATION_UPDATE_TYPE,
    ReputationGossipService,
    ReputationUpdatePayload,
)
from aether.reputation import NodeReputationService


# ---------------------------------------------------------------------------
# Fake / stub helpers
# ---------------------------------------------------------------------------

class FakeSender:
    """Simple MeshSender stub that records every broadcast call."""

    def __init__(self, uhid: str, peer_count: int = 3) -> None:
        self._uhid = uhid
        self._peer_count = peer_count
        self.broadcasts: List[dict] = []

    @property
    def local_uhid(self) -> str:
        return self._uhid

    def broadcast(self, packet: dict) -> int:
        self.broadcasts.append(packet)
        return self._peer_count


class FakeSigner:
    """PacketSigner stub.

    By default verify always returns True.  Set `verify_ok = False` to
    simulate a bad signature.
    """

    def __init__(self, verify_ok: bool = True) -> None:
        self.verify_ok = verify_ok
        self.signed_packets: List[dict] = []

    def sign_packet(self, packet: dict) -> dict:
        signed = dict(packet)
        signed["_signed"] = True
        self.signed_packets.append(signed)
        return signed

    def verify_packet(self, packet: dict, sender_public_key: bytes) -> bool:
        return self.verify_ok


def _now_ms() -> int:
    return int(time.time() * 1000)


def _make_fresh_packet(
    reporter_uhid: str,
    target_uhid: str,
    score_delta: float,
    reason: str = "test",
    ts_offset_ms: int = 0,
) -> dict:
    """Build a raw (unsigned) reputation packet with a fresh timestamp."""
    now_ms = _now_ms() + ts_offset_ms
    payload = ReputationUpdatePayload(
        reporter_uhid=reporter_uhid,
        target_uhid=target_uhid,
        score_delta=score_delta,
        timestamp_ms=now_ms,
        reason=reason,
    )
    return {
        "type": REPUTATION_UPDATE_TYPE,
        "source_uhid": reporter_uhid,
        "destination_uhid": "*",
        "ttl": 3,
        "payload": json.dumps(asdict(payload)),
        "timestamp_ms": now_ms,
    }


DUMMY_KEY = b"\x00" * 32

LOCAL = "local-node"
REPORTER = "reporter-node"
TARGET = "target-node"


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

class TestBroadcastReputationUpdate(unittest.TestCase):
    """Tests for ReputationGossipService.broadcast_reputation_update."""

    def _make_svc(self) -> Tuple[ReputationGossipService, FakeSender, FakeSigner]:
        sender = FakeSender(LOCAL)
        signer = FakeSigner()
        rep = NodeReputationService()
        svc = ReputationGossipService(sender, signer, rep)
        return svc, sender, signer

    def test_broadcast_sends_one_packet(self):
        """broadcast_reputation_update broadcasts exactly one packet."""
        svc, sender, _ = self._make_svc()
        svc.broadcast_reputation_update(TARGET, -0.3, "bad behaviour")
        self.assertEqual(len(sender.broadcasts), 1)

    def test_broadcast_payload_fields(self):
        """Broadcasted packet carries correct reporter, target, and reason."""
        svc, sender, _ = self._make_svc()
        svc.broadcast_reputation_update(TARGET, -0.3, "custody refusal")

        packet = sender.broadcasts[0]
        payload = ReputationUpdatePayload(**json.loads(packet["payload"]))

        self.assertEqual(payload.reporter_uhid, LOCAL)
        self.assertEqual(payload.target_uhid, TARGET)
        self.assertEqual(payload.reason, "custody refusal")
        self.assertAlmostEqual(payload.score_delta, -0.3, places=9)

    def test_broadcast_clamps_delta_above_1(self):
        """score_delta > 1.0 is clamped to 1.0 before broadcast."""
        svc, sender, _ = self._make_svc()
        svc.broadcast_reputation_update(TARGET, 5.0, "exaggerated praise")

        packet = sender.broadcasts[0]
        payload = ReputationUpdatePayload(**json.loads(packet["payload"]))
        self.assertAlmostEqual(payload.score_delta, 1.0, places=9)

    def test_broadcast_clamps_delta_below_minus_1(self):
        """score_delta < -1.0 is clamped to -1.0 before broadcast."""
        svc, sender, _ = self._make_svc()
        svc.broadcast_reputation_update(TARGET, -99.0, "catastrophic")

        packet = sender.broadcasts[0]
        payload = ReputationUpdatePayload(**json.loads(packet["payload"]))
        self.assertAlmostEqual(payload.score_delta, -1.0, places=9)


class TestHandleGossipPacket(unittest.TestCase):
    """Tests for ReputationGossipService.handle_gossip_packet."""

    def _make_svc(
        self, verify_ok: bool = True
    ) -> Tuple[ReputationGossipService, NodeReputationService]:
        sender = FakeSender(LOCAL)
        signer = FakeSigner(verify_ok=verify_ok)
        rep = NodeReputationService()
        svc = ReputationGossipService(sender, signer, rep)
        return svc, rep

    def test_handle_invalid_signature(self):
        """Packets that fail signature verification are rejected (returns False)."""
        svc, rep = self._make_svc(verify_ok=False)
        packet = _make_fresh_packet(REPORTER, TARGET, -0.2)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertFalse(result)
        # Score must not have changed
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), 1.0, places=9)

    def test_handle_wrong_type(self):
        """Packets with type != 52 are rejected without touching reputation."""
        svc, rep = self._make_svc()
        packet = _make_fresh_packet(REPORTER, TARGET, -0.2)
        packet["type"] = 99  # wrong type
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertFalse(result)
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), 1.0, places=9)

    def test_handle_stale_timestamp(self):
        """Packets older than 5 minutes are rejected (returns False)."""
        svc, rep = self._make_svc()
        stale_offset = -(FRESHNESS_WINDOW_MS + 1_000)  # 5 min + 1 s in the past
        packet = _make_fresh_packet(REPORTER, TARGET, -0.2, ts_offset_ms=stale_offset)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertFalse(result)
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), 1.0, places=9)

    def test_handle_missing_reporter_field(self):
        """Packets with an empty reporter_uhid are rejected."""
        svc, rep = self._make_svc()
        packet = _make_fresh_packet("", TARGET, -0.2)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertFalse(result)
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), 1.0, places=9)

    def test_handle_own_gossip(self):
        """Echo suppression: packets where reporter == local_uhid are rejected."""
        svc, rep = self._make_svc()
        # reporter_uhid == LOCAL (same as the service's own UHID)
        packet = _make_fresh_packet(LOCAL, TARGET, -0.2)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertFalse(result)
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), 1.0, places=9)

    def test_handle_unknown_reporter_full_delta(self):
        """Unknown reporter defaults to R=1.0, so effective_delta == score_delta."""
        svc, rep = self._make_svc()
        packet = _make_fresh_packet(REPORTER, TARGET, -0.4)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertTrue(result)
        # Unknown reporter → R=1.0 → effective_delta = -0.4 × 1.0 = -0.4
        # Target starts at 1.0 → 1.0 + (−0.4) = 0.6
        expected = 1.0 + (-0.4 * 1.0)
        self.assertAlmostEqual(rep.get_reputation_score(TARGET), expected, places=9)

    def test_handle_degraded_reporter_weighted_delta(self):
        """Degraded reporter (R≈0.50) causes effective_delta = claimed × 0.50.

        Reporter degraded by 10 × record_rreq_flood_attempt (each −0.05):
          1.0 − 10 × 0.05 = 0.50.
        Claimed delta = −0.6 → effective_delta = −0.6 × 0.50 = −0.30.
        Target starts at 1.0 → expected = 1.0 − 0.30 = 0.70.
        """
        svc, rep = self._make_svc()

        # Degrade the reporter to R=0.50
        for _ in range(10):
            rep.record_rreq_flood_attempt(REPORTER)

        reporter_r = rep.get_reputation_score(REPORTER)
        self.assertAlmostEqual(reporter_r, 0.50, places=9)

        claimed_delta = -0.6
        packet = _make_fresh_packet(REPORTER, TARGET, claimed_delta)
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertTrue(result)

        expected_score = 1.0 + (claimed_delta * reporter_r)
        self.assertAlmostEqual(
            rep.get_reputation_score(TARGET), expected_score, places=9
        )

    def test_handle_positive_delta_improves_target(self):
        """A positive reputation report from a trusted reporter improves the target."""
        svc, rep = self._make_svc()

        # First, degrade the target somewhat
        rep.record_signature_failure(TARGET)  # 1.0 → 0.80
        before = rep.get_reputation_score(TARGET)
        self.assertAlmostEqual(before, 0.80, places=9)

        # Good report from an unknown (trusted by default) reporter
        packet = _make_fresh_packet(REPORTER, TARGET, +0.10, reason="good relay")
        result = svc.handle_gossip_packet(packet, DUMMY_KEY)
        self.assertTrue(result)

        after = rep.get_reputation_score(TARGET)
        # effective_delta = +0.10 × 1.0 = +0.10; 0.80 + 0.10 = 0.90
        self.assertAlmostEqual(after, 0.90, places=9)


if __name__ == "__main__":
    unittest.main()
