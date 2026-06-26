# SPDX-License-Identifier: MIT
"""Behavioural tests for the in-memory aether-space breadcrumb noticeboard.

Covers drop (TTL clamp + emergency override + received callback), the
geohash-prefix scan, creator-only delete, and prune.
"""
from __future__ import annotations

import asyncio
import unittest

from aethernet.space import BreadcrumbType, InMemorySpaceService


class SpaceServiceTests(unittest.TestCase):
    def test_drop_scan_delete_prune(self) -> None:
        async def run() -> None:
            svc = InMemorySpaceService()
            received = []
            svc.on_breadcrumb_received = lambda b: received.append(b)

            a = await svc.drop("k3vf9z", "hashA", "anchor1", BreadcrumbType.NOTICE, 24)
            self.assertEqual(a.ttl_hours, 24)
            self.assertEqual(len(received), 1)

            # Emergency breadcrumbs get the fixed 720h TTL.
            e = await svc.drop("k3vf9z", "hashE", "anchor1", BreadcrumbType.EMERGENCY, 1)
            self.assertEqual(e.ttl_hours, 720)

            # Scan: prefix-proximity hit vs a far cell.
            self.assertEqual(len(await svc.scan("k3vf9z", 1)), 2)
            self.assertEqual(len(await svc.scan("xxxxxx", 1)), 0)

            # Creator-only delete.
            self.assertFalse(await svc.delete(a, "wrong"))
            self.assertTrue(await svc.delete(a, "anchor1"))
            self.assertEqual(len(await svc.scan("k3vf9z", 1)), 1)

            # Nothing is past its TTL yet.
            self.assertEqual(svc.prune_expired(), 0)

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
