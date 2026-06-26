# SPDX-License-Identifier: MIT
"""Behavioural tests for the in-memory aether-vault service.

Covers the erasure-coded store/recover round-trip, any-K-of-N recovery, the
unrecoverable-below-K case, and the empty-blob edge case.
"""
from __future__ import annotations

import asyncio
import unittest

from aethernet.vault import VAULT_K, VAULT_M, InMemoryVaultService


class VaultServiceTests(unittest.TestCase):
    def test_store_recover_round_trip_and_health(self) -> None:
        async def run() -> None:
            svc = InMemoryVaultService()
            data = bytes((i * 7) % 256 for i in range(3333))
            m = await svc.store(data, "doc.bin")
            self.assertEqual(len(m.shard_hashes), VAULT_K + VAULT_M)
            self.assertEqual(m.size_bytes, 3333)
            self.assertEqual(len(m.content_hash), 64)

            self.assertEqual(await svc.recover(m), data)

            h = svc.check_health(m)
            self.assertEqual(h.reachable_shards, VAULT_K + VAULT_M)
            self.assertTrue(h.is_recoverable)
            self.assertGreater(h.redundancy_score, 0.99)

        asyncio.run(run())

    def test_recovers_from_any_k_shards_then_unrecoverable(self) -> None:
        async def run() -> None:
            svc = InMemoryVaultService()
            data = bytes(range(1, 13))
            m = await svc.store(data, "x")

            # Drop M shards: K survive -> still recoverable.
            for h in m.shard_hashes[:VAULT_M]:
                svc._shards.pop(h, None)  # noqa: SLF001 - white-box loss simulation
            health = svc.check_health(m)
            self.assertEqual(health.reachable_shards, VAULT_K)
            self.assertTrue(health.is_recoverable)
            self.assertEqual(await svc.recover(m), data)

            # Drop one more -> only K-1 remain -> unrecoverable.
            svc._shards.pop(m.shard_hashes[VAULT_M], None)  # noqa: SLF001
            self.assertFalse(svc.check_health(m).is_recoverable)
            with self.assertRaises(ValueError):
                await svc.recover(m)

        asyncio.run(run())

    def test_empty_blob_round_trip(self) -> None:
        async def run() -> None:
            svc = InMemoryVaultService()
            m = await svc.store(b"", "empty")
            self.assertEqual(m.size_bytes, 0)
            self.assertEqual(await svc.recover(m), b"")

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
