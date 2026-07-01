// SPDX-License-Identifier: MIT
//! 3-node mesh-integration proof for circuit-relay-v2: the engine relays A->B through R
//! over real MeshPacket frames (type CircuitRelayControl) with NO direct A-B link,
//! surfacing at B via the transport `set_on_data` callback — exactly how a host mesh
//! consumes it. Mirrors the C# CircuitRelayMeshIntegrationTests and the Go / Python / TS
//! mesh tests.

use std::collections::HashMap;
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use aethernet_protocol::circuitrelay::{MeshRelayLink, Options, Transport};
use aethernet_protocol::protocol::MeshPacket;

fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as i64
}

/// In-process mesh whose adjacency is A-R-B with NO direct A-B edge; routes each MeshPacket
/// one hop to the destination node's link on a spawned thread (so the engine's blocking
/// CONNECT/RESERVE waits never deadlock the sender). Stands in for the real radios.
struct MeshHub {
    edges: Mutex<HashMap<String, bool>>,
    links: Mutex<HashMap<String, Arc<MeshRelayLink>>>,
}

impl MeshHub {
    fn new() -> Arc<MeshHub> {
        Arc::new(MeshHub {
            edges: Mutex::new(HashMap::new()),
            links: Mutex::new(HashMap::new()),
        })
    }
    fn connect(&self, x: &str, y: &str) {
        let mut e = self.edges.lock().unwrap();
        e.insert(format!("{x}|{y}"), true);
        e.insert(format!("{y}|{x}"), true);
    }
    fn adjacent(&self, x: &str, y: &str) -> bool {
        *self.edges.lock().unwrap().get(&format!("{x}|{y}")).unwrap_or(&false)
    }
    fn register(&self, node: &str, link: Arc<MeshRelayLink>) {
        self.links.lock().unwrap().insert(node.to_string(), link);
    }
    fn deliver(&self, to: &str, pkt: MeshPacket) {
        let l = self.links.lock().unwrap().get(to).cloned();
        if let Some(l) = l {
            thread::spawn(move || l.handle_incoming_packet(&pkt));
        }
    }
}

fn send_from(hub: Arc<MeshHub>, node: String) -> Box<dyn Fn(MeshPacket) -> bool + Send + Sync> {
    Box::new(move |pkt: MeshPacket| {
        if !hub.adjacent(&node, &pkt.destination_uhid) {
            return false;
        }
        hub.deliver(&pkt.destination_uhid.clone(), pkt);
        true
    })
}

fn can_reach_from(hub: Arc<MeshHub>, node: String) -> Box<dyn Fn(&str) -> bool + Send + Sync> {
    Box::new(move |other: &str| hub.adjacent(&node, other))
}

#[test]
fn relay_works_as_mesh_transport() {
    let hub = MeshHub::new();
    hub.connect("A", "R");
    hub.connect("R", "B"); // deliberately NO A-B edge

    let a_link = Arc::new(MeshRelayLink::new(
        "A",
        send_from(Arc::clone(&hub), "A".into()),
        can_reach_from(Arc::clone(&hub), "A".into()),
    ));
    let r_link = Arc::new(MeshRelayLink::new(
        "R",
        send_from(Arc::clone(&hub), "R".into()),
        can_reach_from(Arc::clone(&hub), "R".into()),
    ));
    let b_link = Arc::new(MeshRelayLink::new(
        "B",
        send_from(Arc::clone(&hub), "B".into()),
        can_reach_from(Arc::clone(&hub), "B".into()),
    ));
    hub.register("A", Arc::clone(&a_link));
    hub.register("R", Arc::clone(&r_link));
    hub.register("B", Arc::clone(&b_link));

    let a = Transport::new("A", a_link, Options::default(), Box::new(now_ms));
    let r = Transport::new("R", r_link, Options::default(), Box::new(now_ms));
    let b = Transport::new("B", b_link, Options::default(), Box::new(now_ms));

    let (tx, rx) = mpsc::channel::<(String, Vec<u8>)>();
    b.set_on_data(Box::new(move |sender, data| {
        let _ = tx.send((sender, data));
    }));

    assert!(!a.is_connected("B"), "A should have no direct path to B");
    assert!(b.reserve("R"), "B failed to reserve on the relay");
    a.set_route("B", "R");

    let payload = vec![0xDE_u8, 0xAD, 0xBE, 0xEF];
    assert!(a.send("B", &payload), "A.send to B failed");

    let (sender, data) = rx
        .recv_timeout(Duration::from_secs(3))
        .expect("B never received the relayed message via the mesh link");
    assert_eq!(sender, "A");
    assert_eq!(data, payload);
    assert_eq!(r.active_bridge_count(), 1, "R should be bridging exactly one connection");
}
