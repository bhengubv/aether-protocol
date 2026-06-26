// SPDX-License-Identifier: MIT
//
// Behavioural test for the FMHY catalogue: the markdown parser (headings ->
// category, bold link -> entry, star -> starred) and the in-memory catalogue
// (sync + entryCount + category browse + getStarred).

import XCTest
@testable import AetherNetProtocol

final class FmhyCatalogueTests: XCTestCase {

    private let md = """
    # Video
    ## Streaming
    * **[FreeFlix](https://freeflix.example)** - Free movies and shows
    * ⭐ **[BestStream](https://best.example)** - The top pick

    # Audio
    * **[TunePort](https://tune.example)** - Music streaming
    """

    func testParseAndCatalogue() async {
        let parsed = parseFmhyMarkdown(md)
        XCTAssertEqual(parsed.count, 3)
        XCTAssertEqual(parsed[0].category, "Video / Streaming")
        XCTAssertEqual(parsed[0].name, "FreeFlix")
        XCTAssertTrue(parsed[1].isStarred)
        XCTAssertEqual(parsed[1].name, "BestStream")
        XCTAssertEqual(parsed[2].category, "Audio")

        let svc = InMemoryFmhyCatalogueService()
        XCTAssertEqual(svc.entryCount, 0)
        var synced = 0
        svc.onSynced = { _, _, _ in synced += 1 }
        await svc.sync(markdown: md)
        XCTAssertEqual(svc.entryCount, 3)
        XCTAssertEqual(synced, 1)

        XCTAssertEqual(svc.browse().count, 3)
        XCTAssertEqual(svc.browse(categoryFilter: "video").count, 2)
        XCTAssertEqual(svc.browse(categoryFilter: "audio").count, 1)
        XCTAssertEqual(svc.browse(categoryFilter: "nonexistent").count, 0)

        let starred = svc.getStarred()
        XCTAssertEqual(starred.count, 1)
        XCTAssertEqual(starred[0].name, "BestStream")
    }
}
