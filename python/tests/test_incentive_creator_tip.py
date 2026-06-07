# SPDX-License-Identifier: MIT

"""Unit tests for IncentiveProvider.record_creator_tip (v1.2.0, Issue #61)."""

from __future__ import annotations

import asyncio
import unittest
from decimal import Decimal

from aethernet.extensibility import IncentiveProvider, NoopIncentiveProvider
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


class CapturingIncentiveProvider(IncentiveProvider):
    """Test double: captures every record_creator_tip and record_relay call."""

    def __init__(self) -> None:
        self.tips: list[tuple[str, Decimal, str]] = []
        self.relays: list[tuple[str, MeshPacket]] = []

    async def record_creator_tip(
        self,
        creator_uhid: str,
        amount: Decimal,
        content_hash: str,
    ) -> None:
        self.tips.append((creator_uhid, amount, content_hash))

    async def record_relay(self, local_uhid: str, packet: MeshPacket) -> None:
        self.relays.append((local_uhid, packet))


class IncentiveProviderCreatorTipTests(unittest.TestCase):

    def test_default_record_creator_tip_is_noop_and_returns_none(self):
        """The default no-op impl must not raise and must return None."""
        provider = NoopIncentiveProvider()
        result = _run(provider.record_creator_tip("creator-uhid", Decimal("5.00"), "deadbeef"))
        self.assertIsNone(result)

    def test_default_record_creator_tip_handles_multiple_calls(self):
        provider = NoopIncentiveProvider()
        for i in range(5):
            _run(provider.record_creator_tip(
                f"creator-{i}",
                Decimal("1.23"),
                f"hash-{i}",
            ))
        # No exception = pass

    def test_custom_record_creator_tip_receives_arguments_verbatim(self):
        provider = CapturingIncentiveProvider()
        _run(provider.record_creator_tip("creator-zulu", Decimal("12.50"), "rootHash-abc"))

        self.assertEqual(1, len(provider.tips))
        creator, amount, content_hash = provider.tips[0]
        self.assertEqual("creator-zulu", creator)
        self.assertEqual(Decimal("12.50"), amount)
        self.assertEqual("rootHash-abc", content_hash)

    def test_record_creator_tip_and_record_relay_are_independent_paths(self):
        provider = CapturingIncentiveProvider()

        _run(provider.record_creator_tip("author", Decimal("1.00"), "h1"))
        _run(provider.record_relay("node-uhid", MeshPacket(type=PacketType.Data)))

        # Both recorded separately - relay path doesn't pollute the tip stream and vice versa.
        self.assertEqual(1, len(provider.tips))
        self.assertEqual(1, len(provider.relays))


if __name__ == "__main__":
    unittest.main()
