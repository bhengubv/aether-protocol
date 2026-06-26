// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-space breadcrumb noticeboard: drop
// (TTL clamp + emergency override + received callback), geohash-prefix scan,
// creator-only delete, and prune.

use std::cell::Cell;
use std::rc::Rc;

use aethernet_protocol::space::{BreadcrumbType, InMemorySpaceService};

#[test]
fn space_drop_scan_delete_prune() {
    let mut svc = InMemorySpaceService::new();

    let received = Rc::new(Cell::new(0));
    let r2 = received.clone();
    svc.on_breadcrumb_received = Some(Box::new(move |_b| r2.set(r2.get() + 1)));

    let a = svc.drop_crumb("k3vf9z", "hashA", "anchor1", BreadcrumbType::Notice, 24);
    assert_eq!(a.ttl_hours, 24);
    assert_eq!(received.get(), 1);

    // Emergency breadcrumbs get the fixed 720h TTL.
    let e = svc.drop_crumb("k3vf9z", "hashE", "anchor1", BreadcrumbType::Emergency, 1);
    assert_eq!(e.ttl_hours, 720);

    // Scan: prefix-proximity hit vs a far cell.
    assert_eq!(svc.scan("k3vf9z", 1).len(), 2);
    assert_eq!(svc.scan("xxxxxx", 1).len(), 0);

    // Creator-only delete.
    assert!(!svc.delete(&a, "wrong"));
    assert!(svc.delete(&a, "anchor1"));
    assert_eq!(svc.scan("k3vf9z", 1).len(), 1);

    // Nothing is past its TTL yet.
    assert_eq!(svc.prune_expired(), 0);
}
