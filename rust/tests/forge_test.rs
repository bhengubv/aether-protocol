// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-forge package cache: cache (with the
// new-entry announcement + idempotent first-write-wins), query hit/miss, the fetch
// download-count increment, and aggregate stats.

use std::cell::Cell;
use std::rc::Rc;

use aethernet_protocol::forge::InMemoryForgeService;

#[test]
fn forge_cache_query_fetch_stats() {
    let mut svc = InMemoryForgeService::new();

    let fired = Rc::new(Cell::new(0));
    let fired2 = fired.clone();
    svc.on_new_entry_announced = Some(Box::new(move |_e| fired2.set(fired2.get() + 1)));

    let e = svc.cache("npm:react@18.2.0", "hash1", 1000);
    assert_eq!(e.download_count, 0);
    assert_eq!(fired.get(), 1);

    // Idempotent re-cache: first write wins, no second announcement.
    let e2 = svc.cache("npm:react@18.2.0", "hash2", 9999);
    assert_eq!(e2.content_hash, "hash1");
    assert_eq!(fired.get(), 1);

    // Query hit + miss.
    assert_eq!(svc.query("npm:react@18.2.0").unwrap().content_hash, "hash1");
    assert!(svc.query("missing").is_none());

    // Fetch increments the download counter; miss returns None.
    assert_eq!(svc.fetch("npm:react@18.2.0").unwrap().download_count, 1);
    svc.fetch("npm:react@18.2.0");
    assert!(svc.fetch("missing").is_none());

    // Stats: bytes-saved = downloads * size; one entry catalogued.
    let st = svc.get_stats();
    assert_eq!(st.catalogue_size, 1);
    assert_eq!(st.total_bytes_saved, 2000); // 2 downloads * 1000 bytes
}
