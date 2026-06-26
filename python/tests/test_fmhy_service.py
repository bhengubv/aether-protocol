# SPDX-License-Identifier: MIT
"""Behavioural tests for the FMHY catalogue: the markdown parser (headings ->
category, bold link -> entry, star -> starred) and the in-memory catalogue
(sync + entry_count + category browse + get_starred).
"""
from __future__ import annotations

import asyncio
import unittest

from aethernet.fmhy import InMemoryFmhyCatalogueService, parse_fmhy_markdown

MD = """# Video
## Streaming
* **[FreeFlix](https://freeflix.example)** - Free movies and shows
* ⭐ **[BestStream](https://best.example)** - The top pick

# Audio
* **[TunePort](https://tune.example)** - Music streaming
"""


class FmhyServiceTests(unittest.TestCase):
    def test_parse_and_catalogue(self) -> None:
        async def run() -> None:
            parsed = parse_fmhy_markdown(MD)
            self.assertEqual(len(parsed), 3)
            self.assertEqual(parsed[0].category, "Video / Streaming")
            self.assertEqual(parsed[0].name, "FreeFlix")
            self.assertTrue(parsed[1].is_starred)
            self.assertEqual(parsed[1].name, "BestStream")
            self.assertEqual(parsed[2].category, "Audio")

            svc = InMemoryFmhyCatalogueService()
            self.assertEqual(svc.entry_count, 0)
            fired = []
            svc.on_synced = lambda total, added, at: fired.append(1)
            await svc.sync(MD)
            self.assertEqual(svc.entry_count, 3)
            self.assertEqual(len(fired), 1)

            self.assertEqual(len(svc.browse()), 3)
            self.assertEqual(len(svc.browse("video")), 2)
            self.assertEqual(len(svc.browse("audio")), 1)
            self.assertEqual(len(svc.browse("nonexistent")), 0)

            starred = svc.get_starred()
            self.assertEqual(len(starred), 1)
            self.assertEqual(starred[0].name, "BestStream")

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
