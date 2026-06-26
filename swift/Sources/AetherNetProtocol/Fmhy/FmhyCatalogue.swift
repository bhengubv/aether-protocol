// SPDX-License-Identifier: MIT

// Free Media Heck Yeah (FMHY) content catalogue (Phase-2 extension), propagated
// over the Aether mesh so offline peers benefit from entries fetched by connected
// peers. Port of the C# reference (AetherNet.Fmhy): a markdown parser for the
// FMHY single-page dump plus an in-memory catalogue.

import Foundation

/// A single resource parsed from the FMHY directory.
public struct FmhyEntry {
    public var name: String
    public var url: String
    public var description: String?
    public var category: String // "H1" or "H1 / H2"
    public var isStarred: Bool
    public var mirrors: [String]

    public init(name: String, url: String, description: String?, category: String, isStarred: Bool, mirrors: [String]) {
        self.name = name
        self.url = url
        self.description = description
        self.category = category
        self.isStarred = isStarred
        self.mirrors = mirrors
    }

    /// All URLs: primary followed by any mirrors.
    public var allUrls: [String] { mirrors.isEmpty ? [url] : [url] + mirrors }
}

/// A known torrent tracker-list aggregator.
public struct TrackerSource {
    public let name: String
    public let url: String
    public let description: String
    public init(name: String, url: String, description: String) {
        self.name = name
        self.url = url
        self.description = description
    }
}

/// Well-known public tracker-list aggregators bundled with this release.
public let builtInTrackerSources: [TrackerSource] = [
    TrackerSource(name: "ngosang/trackerslist", url: "https://ngosang.github.io/trackerslist/trackers_all.txt", description: "Community-maintained list of all known public BitTorrent trackers."),
    TrackerSource(name: "XIU2/TrackersListCollection (all)", url: "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", description: "Comprehensive tracker collection maintained by XIU2, updated daily."),
    TrackerSource(name: "XIU2/TrackersListCollection (best)", url: "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", description: "Curated best-performing tracker subset from the XIU2 collection."),
    TrackerSource(name: "newtrackon (stable)", url: "https://newtrackon.com/api/stable", description: "Live-monitored stable tracker list from newtrackon.com."),
    TrackerSource(name: "openwebtorrent", url: "https://openwebtorrent.com/", description: "Free WebTorrent-compatible tracker for browser-based torrenting."),
]

public let fmhyApiUrl = "https://api.fmhy.net/single-page"

private let boldLinkRe = try! NSRegularExpression(pattern: "\\*\\*\\[([^\\]]+)\\]\\(([^)]+)\\)\\*\\*")
private let plainLinkRe = try! NSRegularExpression(pattern: "\\[([^\\]]+)\\]\\(([^)]+)\\)")
private let headingRe = try! NSRegularExpression(pattern: "^(#{1,2})\\s+(.+)$")
private let bulletRe = try! NSRegularExpression(pattern: "^\\s*[*\\-]\\s+(.+)$")

private func firstMatch(_ re: NSRegularExpression, _ s: String) -> NSTextCheckingResult? {
    re.firstMatch(in: s, range: NSRange(s.startIndex..., in: s))
}

private func group(_ m: NSTextCheckingResult, _ i: Int, _ s: String) -> String {
    guard let r = Range(m.range(at: i), in: s) else { return "" }
    return String(s[r])
}

/// Parse a raw FMHY markdown string into a flat list of entries in document order.
public func parseFmhyMarkdown(_ markdown: String) -> [FmhyEntry] {
    var entries: [FmhyEntry] = []
    var h1 = ""
    var h2 = ""

    for rawLine in markdown.split(separator: "\n", omittingEmptySubsequences: false) {
        let line = String(rawLine).replacingOccurrences(of: "[ \\t\\r]+$", with: "", options: .regularExpression)
        if line.isEmpty { continue }

        if let hm = firstMatch(headingRe, line) {
            let level = group(hm, 1, line).count
            let title = group(hm, 2, line).trimmingCharacters(in: .whitespaces)
            if level == 1 { h1 = title; h2 = "" } else { h2 = title }
            continue
        }

        guard let bm = firstMatch(bulletRe, line) else { continue }
        let content = group(bm, 1, line)
        let isStarred = content.contains("\u{2B50}") // ⭐

        guard let bold = firstMatch(boldLinkRe, content) else { continue }
        let name = group(bold, 1, content).trimmingCharacters(in: .whitespaces)
        let url = group(bold, 2, content).trimmingCharacters(in: .whitespaces)
        if url.isEmpty || url.hasPrefix("#") { continue }

        // Byte/character index after the bold match.
        guard let boldRange = Range(bold.range, in: content) else { continue }
        let afterBold = content[boldRange.upperBound...]

        var description: String? = nil
        var mirrorRegion = String(afterBold)
        if let sep = afterBold.range(of: " - ") {
            let descText = String(afterBold[sep.upperBound...]).trimmingCharacters(in: .whitespaces)
            let stripped = plainLinkRe.stringByReplacingMatches(
                in: descText, range: NSRange(descText.startIndex..., in: descText), withTemplate: "$1"
            ).trimmingCharacters(in: .whitespaces)
            if !stripped.isEmpty { description = stripped }
            mirrorRegion = String(afterBold[afterBold.startIndex..<sep.lowerBound])
        }

        var mirrors: [String] = []
        let mr = mirrorRegion
        for m in plainLinkRe.matches(in: mr, range: NSRange(mr.startIndex..., in: mr)) {
            let mu = group(m, 2, mr).trimmingCharacters(in: .whitespaces)
            if !mu.isEmpty && mu != url && !mu.hasPrefix("#") { mirrors.append(mu) }
        }

        let category = h2.isEmpty ? h1 : "\(h1) / \(h2)"
        entries.append(FmhyEntry(name: name, url: url, description: description, category: category, isStarred: isStarred, mirrors: mirrors))
    }
    return entries
}

/// Provides access to the FMHY content catalogue.
public protocol FmhyCatalogueServiceProtocol {
    func sync(markdown: String) async
    func browse(categoryFilter: String?) -> [FmhyEntry]
    func getStarred(categoryFilter: String?) -> [FmhyEntry]
    func getTrackerSources() -> [TrackerSource]
    var entryCount: Int { get }
}

/// In-memory FMHY catalogue, seeded optionally and updated via sync().
public final class InMemoryFmhyCatalogueService: FmhyCatalogueServiceProtocol {
    private var entries: [FmhyEntry]
    public private(set) var lastSyncedAt: Date?

    /// Fires when sync installs new entries: (total, added, syncedAt).
    public var onSynced: ((Int, Int, Date) -> Void)?

    public init(seed: [FmhyEntry] = []) {
        self.entries = seed
    }

    public var entryCount: Int { entries.count }

    public func sync(markdown: String) async {
        let before = entries.count
        let parsed = parseFmhyMarkdown(markdown)
        let now = Date()
        entries = parsed
        lastSyncedAt = now
        onSynced?(parsed.count, parsed.count - before, now)
    }

    public func browse(categoryFilter: String? = nil) -> [FmhyEntry] {
        guard let cf = categoryFilter, !cf.isEmpty else { return entries }
        let lc = cf.lowercased()
        return entries.filter { $0.category.lowercased().contains(lc) }
    }

    public func getStarred(categoryFilter: String? = nil) -> [FmhyEntry] {
        let lc = categoryFilter?.isEmpty == false ? categoryFilter!.lowercased() : nil
        return entries.filter { $0.isStarred && (lc == nil || $0.category.lowercased().contains(lc!)) }
    }

    public func getTrackerSources() -> [TrackerSource] { builtInTrackerSources }
}
