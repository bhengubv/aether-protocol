// SPDX-License-Identifier: MIT
//
// Behavioural test for the FMHY catalogue: the markdown parser (headings ->
// category, bold link -> entry, star -> starred) and the in-memory catalogue
// (sync + entry_count + category browse + get_starred).

use aethernet_protocol::fmhy::{parse_fmhy_markdown, InMemoryFmhyCatalogueService};

const MD: &str = "# Video\n## Streaming\n* **[FreeFlix](https://freeflix.example)** - Free movies and shows\n* ⭐ **[BestStream](https://best.example)** - The top pick\n\n# Audio\n* **[TunePort](https://tune.example)** - Music streaming\n";

#[test]
fn fmhy_parse_and_catalogue() {
    let parsed = parse_fmhy_markdown(MD);
    assert_eq!(parsed.len(), 3);
    assert_eq!(parsed[0].category, "Video / Streaming");
    assert_eq!(parsed[0].name, "FreeFlix");
    assert!(parsed[1].is_starred);
    assert_eq!(parsed[1].name, "BestStream");
    assert_eq!(parsed[2].category, "Audio");

    let mut svc = InMemoryFmhyCatalogueService::new(Vec::new());
    assert_eq!(svc.entry_count(), 0);
    svc.sync(MD);
    assert_eq!(svc.entry_count(), 3);

    assert_eq!(svc.browse(None).len(), 3);
    assert_eq!(svc.browse(Some("video")).len(), 2);
    assert_eq!(svc.browse(Some("audio")).len(), 1);
    assert_eq!(svc.browse(Some("nonexistent")).len(), 0);

    let starred = svc.get_starred(None);
    assert_eq!(starred.len(), 1);
    assert_eq!(starred[0].name, "BestStream");
}
