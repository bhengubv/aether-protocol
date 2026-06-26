// SPDX-License-Identifier: MIT

// Free Media Heck Yeah (FMHY) content catalogue (Phase-2 extension), propagated
// over the Aether mesh so offline peers benefit from entries fetched by connected
// peers. Port of the C# reference (AetherNet.Fmhy): a markdown parser for the
// FMHY single-page dump plus an in-memory catalogue.

package aethernet.fmhy

import java.time.Instant

/** A single resource parsed from the FMHY directory. */
data class FmhyEntry(
    val name: String,
    val url: String,
    val description: String?,
    val category: String, // "H1" or "H1 / H2"
    val isStarred: Boolean,
    val mirrors: List<String> = emptyList(),
) {
    /** All URLs: primary + mirrors. */
    val allUrls: List<String>
        get() = if (mirrors.isEmpty()) listOf(url) else listOf(url) + mirrors
}

/** A known torrent tracker-list aggregator. */
data class TrackerSource(val name: String, val url: String, val description: String)

/** Well-known public tracker-list aggregators bundled with this release. */
val BUILT_IN_TRACKER_SOURCES: List<TrackerSource> = listOf(
    TrackerSource("ngosang/trackerslist", "https://ngosang.github.io/trackerslist/trackers_all.txt", "Community-maintained list of all known public BitTorrent trackers."),
    TrackerSource("XIU2/TrackersListCollection (all)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", "Comprehensive tracker collection maintained by XIU2, updated daily."),
    TrackerSource("XIU2/TrackersListCollection (best)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", "Curated best-performing tracker subset from the XIU2 collection."),
    TrackerSource("newtrackon (stable)", "https://newtrackon.com/api/stable", "Live-monitored stable tracker list from newtrackon.com."),
    TrackerSource("openwebtorrent", "https://openwebtorrent.com/", "Free WebTorrent-compatible tracker for browser-based torrenting."),
)

const val FMHY_API_URL = "https://api.fmhy.net/single-page"

private val BOLD_LINK_RE = Regex("""\*\*\[([^\]]+)\]\(([^)]+)\)\*\*""")
private val PLAIN_LINK_RE = Regex("""\[([^\]]+)\]\(([^)]+)\)""")
private val HEADING_RE = Regex("""^(#{1,2})\s+(.+)$""")
private val BULLET_RE = Regex("""^\s*[*\-]\s+(.+)$""")

/** Parse a raw FMHY markdown string into a flat list of entries in document order. */
fun parseFmhyMarkdown(markdown: String): List<FmhyEntry> {
    val entries = ArrayList<FmhyEntry>()
    var h1 = ""
    var h2 = ""

    for (rawLine in markdown.split("\n")) {
        val line = rawLine.trimEnd(' ', '\t', '\r')
        if (line.isEmpty()) continue

        val hm = HEADING_RE.find(line)
        if (hm != null) {
            val level = hm.groupValues[1].length
            val title = hm.groupValues[2].trim()
            if (level == 1) {
                h1 = title; h2 = ""
            } else {
                h2 = title
            }
            continue
        }

        val bm = BULLET_RE.find(line) ?: continue
        val content = bm.groupValues[1]
        val isStarred = content.contains('⭐') // ⭐

        val bold = BOLD_LINK_RE.find(content) ?: continue
        val name = bold.groupValues[1].trim()
        val url = bold.groupValues[2].trim()
        if (url.isEmpty() || url.startsWith("#")) continue
        val boldEnd = bold.range.last + 1

        var description: String? = null
        val rel = content.substring(boldEnd).indexOf(" - ")
        val descSep = if (rel >= 0) rel + boldEnd else -1
        if (descSep >= 0) {
            var d = content.substring(descSep + 3).trim()
            d = PLAIN_LINK_RE.replace(d) { it.groupValues[1] }.trim()
            description = d.ifEmpty { null }
        }

        val mirrorRegion = if (descSep >= 0) content.substring(boldEnd, descSep) else content.substring(boldEnd)
        val mirrors = ArrayList<String>()
        for (pm in PLAIN_LINK_RE.findAll(mirrorRegion)) {
            val mu = pm.groupValues[2].trim()
            if (mu.isNotEmpty() && mu != url && !mu.startsWith("#")) mirrors.add(mu)
        }

        val category = if (h2.isNotEmpty()) "$h1 / $h2" else h1
        entries.add(FmhyEntry(name, url, description, category, isStarred, mirrors))
    }
    return entries
}

/** Provides access to the FMHY content catalogue. */
interface IFmhyCatalogueService {
    suspend fun sync(markdown: String)
    fun browse(categoryFilter: String? = null): List<FmhyEntry>
    fun getStarred(categoryFilter: String? = null): List<FmhyEntry>
    fun getTrackerSources(): List<TrackerSource>
    val entryCount: Int
}

/** In-memory [IFmhyCatalogueService] seeded optionally and updated via [sync]. */
class InMemoryFmhyCatalogueService(seed: List<FmhyEntry> = emptyList()) : IFmhyCatalogueService {
    private var entries: List<FmhyEntry> = seed
    var lastSyncedAt: Instant? = null
        private set

    /** Fires when [sync] installs new entries: (total, added, syncedAt). */
    var onSynced: ((total: Int, added: Int, syncedAt: Instant) -> Unit)? = null

    override val entryCount: Int get() = entries.size

    override suspend fun sync(markdown: String) {
        val before = entries.size
        val parsed = parseFmhyMarkdown(markdown)
        val now = Instant.now()
        entries = parsed
        lastSyncedAt = now
        onSynced?.invoke(parsed.size, parsed.size - before, now)
    }

    override fun browse(categoryFilter: String?): List<FmhyEntry> {
        if (categoryFilter.isNullOrEmpty()) return entries
        val cf = categoryFilter.lowercase()
        return entries.filter { it.category.lowercase().contains(cf) }
    }

    override fun getStarred(categoryFilter: String?): List<FmhyEntry> {
        val cf = categoryFilter?.lowercase()
        return entries.filter { it.isStarred && (cf == null || it.category.lowercase().contains(cf)) }
    }

    override fun getTrackerSources(): List<TrackerSource> = BUILT_IN_TRACKER_SOURCES
}
