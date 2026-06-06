# SPDX-License-Identifier: MIT

"""Unit tests for the extensibility no-op providers:
NoopIncentiveProvider, NoopBackendClient, NoopFeatureFlagProvider.
"""

from __future__ import annotations

import asyncio
import unittest

from aethernet.extensibility import (
    NoopIncentiveProvider,
    NoopBackendClient,
    NoopFeatureFlagProvider,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.models import DtnBundle, SosAlert


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _make_packet(from_uhid: str = "alice") -> MeshPacket:
    return MeshPacket(
        type=PacketType.Data,
        source_uhid=from_uhid,
        destination_uhid="bob",
        payload=b"hello",
    )


def _make_dtn_bundle() -> DtnBundle:
    # DtnBundle's only required fields are sender_uhid, recipient_uhid, encrypted_payload.
    return DtnBundle(
        sender_uhid="alice",
        recipient_uhid="bob",
        encrypted_payload=b"\x01\x02\x03",
    )


def _make_sos_alert() -> SosAlert:
    # SosAlert's only required field is sender_uhid; everything else has a default.
    return SosAlert(
        sender_uhid="alice",
        message="help",
        latitude=-26.2,
        longitude=28.04,
    )


class TestNoopIncentiveProvider(unittest.TestCase):

    def test_instantiates_without_arguments(self):
        provider = NoopIncentiveProvider()
        self.assertIsNotNone(provider)

    def test_record_relay_returns_none(self):
        provider = NoopIncentiveProvider()
        result = _run(provider.record_relay("alice", _make_packet("alice")))
        self.assertIsNone(result)

    def test_record_relay_does_not_raise(self):
        provider = NoopIncentiveProvider()
        for i in range(5):
            _run(provider.record_relay(f"node-{i}", _make_packet(f"node-{i}")))

    def test_should_prioritize_returns_false(self):
        provider = NoopIncentiveProvider()
        result = _run(provider.should_prioritize(_make_packet("alice")))
        self.assertFalse(result)

    def test_should_prioritize_false_for_multiple_packets(self):
        provider = NoopIncentiveProvider()
        for uhid in ["alice", "bob", "carol", "dave"]:
            result = _run(provider.should_prioritize(_make_packet(uhid)))
            self.assertFalse(result, f"expected False for uhid={uhid}")


class TestNoopBackendClient(unittest.TestCase):

    def test_instantiates_without_arguments(self):
        client = NoopBackendClient()
        self.assertIsNotNone(client)

    def test_relay_message_returns_false(self):
        client = NoopBackendClient()
        result = _run(client.relay_message("alice", "bob", b"\x01\x02\x03", 0))
        self.assertFalse(result)

    def test_relay_message_false_for_empty_content(self):
        client = NoopBackendClient()
        result = _run(client.relay_message("a", "b", b"", 1))
        self.assertFalse(result)

    def test_relay_message_false_regardless_of_priority(self):
        client = NoopBackendClient()
        for pri in [0, 1, 5, 100]:
            result = _run(client.relay_message("a", "b", b"\x01", pri))
            self.assertFalse(result, f"expected False for priority={pri}")

    def test_sync_dtn_bundle_returns_false(self):
        client = NoopBackendClient()
        result = _run(client.sync_dtn_bundle(_make_dtn_bundle()))
        self.assertFalse(result)

    def test_sync_dtn_bundle_multiple_calls_all_false(self):
        client = NoopBackendClient()
        for _ in range(5):
            result = _run(client.sync_dtn_bundle(_make_dtn_bundle()))
            self.assertFalse(result)

    def test_sync_sos_returns_false(self):
        client = NoopBackendClient()
        result = _run(client.sync_sos(_make_sos_alert()))
        self.assertFalse(result)

    def test_sync_sos_false_for_multiple_alerts(self):
        client = NoopBackendClient()
        for _ in range(5):
            result = _run(client.sync_sos(_make_sos_alert()))
            self.assertFalse(result)


class TestNoopFeatureFlagProvider(unittest.TestCase):

    def test_instantiates_without_arguments(self):
        provider = NoopFeatureFlagProvider()
        self.assertIsNotNone(provider)

    def test_is_enabled_returns_true(self):
        """The no-op intentionally enables all features so the protocol operates normally."""
        provider = NoopFeatureFlagProvider()
        result = _run(provider.is_enabled("any-feature"))
        self.assertTrue(result)

    def test_is_enabled_true_for_all_known_flags(self):
        provider = NoopFeatureFlagProvider()
        flags = [
            "rlnc", "dtn", "voice", "video", "watch-together",
            "group-voice", "sos", "", "FEATURE_UNDER_DEVELOPMENT",
        ]
        for flag in flags:
            result = _run(provider.is_enabled(flag))
            self.assertTrue(result, f"expected True for flag={flag!r}")


if __name__ == "__main__":
    unittest.main()
