# SPDX-License-Identifier: MIT
"""Behavioural tests for the in-memory aether-forge package cache.

Covers cache (with the new-entry announcement + idempotent first-write-wins),
query hit/miss, the fetch download-count increment, and aggregate stats.
"""
from __future__ import annotations

import asyncio
import unittest

from aethernet.forge import InMemoryForgeService


class ForgeServiceTests(unittest.TestCase):
    def test_cache_query_fetch_stats(self) -> None:
        async def run() -> None:
            svc = InMemoryForgeService()
            fired = []
            svc.on_new_entry_announced = lambda e: fired.append(e)

            e = await svc.cache("npm:react@18.2.0", "hash1", 1000)
            self.assertEqual(e.download_count, 0)
            self.assertEqual(len(fired), 1)

            # Idempotent re-cache: first write wins, no second announcement.
            e2 = await svc.cache("npm:react@18.2.0", "hash2", 9999)
            self.assertEqual(e2.content_hash, "hash1")
            self.assertEqual(len(fired), 1)

            # Query hit + miss.
            self.assertEqual((await svc.query("npm:react@18.2.0")).content_hash, "hash1")
            self.assertIsNone(await svc.query("missing"))

            # Fetch increments the download counter; miss returns None.
            f1 = await svc.fetch("npm:react@18.2.0")
            self.assertEqual(f1.download_count, 1)
            await svc.fetch("npm:react@18.2.0")
            self.assertIsNone(await svc.fetch("missing"))

            # Stats: bytes-saved = downloads * size; one entry catalogued.
            st = await svc.get_stats()
            self.assertEqual(st.catalogue_size, 1)
            self.assertEqual(st.total_bytes_saved, 2000)  # 2 downloads * 1000 bytes

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
