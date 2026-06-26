// SPDX-License-Identifier: MIT
//
// Behavioural test for the FMHY catalogue: the markdown parser (headings ->
// category, bold link -> entry, star -> starred) and the in-memory catalogue
// (sync + entryCount + category browse + getStarred).

package aethernet.fmhy

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class FmhyCatalogueTest {

    private val md = """
        # Video
        ## Streaming
        * **[FreeFlix](https://freeflix.example)** - Free movies and shows
        * ⭐ **[BestStream](https://best.example)** - The top pick

        # Audio
        * **[TunePort](https://tune.example)** - Music streaming
    """.trimIndent()

    @Test
    fun parseAndCatalogue() = runBlocking {
        val parsed = parseFmhyMarkdown(md)
        assertEquals(3, parsed.size)
        assertEquals("Video / Streaming", parsed[0].category)
        assertEquals("FreeFlix", parsed[0].name)
        assertTrue(parsed[1].isStarred)
        assertEquals("BestStream", parsed[1].name)
        assertEquals("Audio", parsed[2].category)

        val svc = InMemoryFmhyCatalogueService()
        assertEquals(0, svc.entryCount)
        var synced = 0
        svc.onSynced = { _, _, _ -> synced++ }
        svc.sync(md)
        assertEquals(3, svc.entryCount)
        assertEquals(1, synced)

        assertEquals(3, svc.browse().size)
        assertEquals(2, svc.browse("video").size)
        assertEquals(1, svc.browse("audio").size)
        assertEquals(0, svc.browse("nonexistent").size)

        val starred = svc.getStarred()
        assertEquals(1, starred.size)
        assertEquals("BestStream", starred[0].name)
    }
}
