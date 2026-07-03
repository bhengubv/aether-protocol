// SPDX-License-Identifier: MIT
//! Gap-2 acceptance test (Rust): the circuit relay must be picked *automatically* by
//! [`TransportManager`] as the last-resort fallback — NOT called directly. A and B each run a
//! manager whose only transport is the relay; `a_mgr.send_async` routes B's payload through the
//! manager's selection (additional transports, ascending power cost, relay = cost 90) and B
//! receives it, tagged with the relay transport's name — proving selection, not hand-wiring.
//!
//! Mirrors the C# `CircuitRelayMeshIntegrationTests.Relay_Is_Auto_Selected_By_TransportManager_As_Fallback`.

use std::collections::HashMap;
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use aethernet_protocol::circuitrelay::{MeshCircuitRelay, MeshRelayLink, Options};
use aethernet_protocol::protocol::MeshPacket;
use aethernet_protocol::transport::{TransportManager, TransportService};

fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_millis() as i64
}

/// In-process mesh whose adjacency is A-R-B with NO direct A-B edge; routes each MeshPacket one
/// hop to the destination node's link on a spawned thread (so the engine's blocking
/// CONNECT/RESERVE waits never deadlock the sender). Stands in for the real radios that in
/// production are the `send_one_hop` delegate.
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

#[tokio::test]
async fn relay_is_auto_selected_by_transport_manager_as_fallback() {
    let hub = MeshHub::new();
    hub.connect("A", "R");
    hub.connect("R", "B"); // no A-B edge

    let (a_t, a_l) = MeshCircuitRelay::create(
        "A",
        send_from(Arc::clone(&hub), "A".into()),
        can_reach_from(Arc::clone(&hub), "A".into()),
        Options::default(),
        Box::new(now_ms),
    );
    let (r_t, r_l) = MeshCircuitRelay::create(
        "R",
        send_from(Arc::clone(&hub), "R".into()),
        can_reach_from(Arc::clone(&hub), "R".into()),
        Options::default(),
        Box::new(now_ms),
    );
    let (b_t, b_l) = MeshCircuitRelay::create(
        "B",
        send_from(Arc::clone(&hub), "B".into()),
        can_reach_from(Arc::clone(&hub), "B".into()),
        Options::default(),
        Box::new(now_ms),
    );
    hub.register("A", a_l);
    hub.register("R", r_l);
    hub.register("B", b_l);

    // A and B each run a TransportManager whose ONLY transport is the relay (no BLE/Wi-Fi/NearLink).
    let a_mgr = TransportManager::new(vec![Arc::clone(&a_t) as Arc<dyn TransportService>]);
    let b_mgr = TransportManager::new(vec![Arc::clone(&b_t) as Arc<dyn TransportService>]);

    // B surfaces the relayed message through the manager's receive path, tagged with the
    // selecting transport's name.
    let (tx, rx) = mpsc::channel::<(String, Vec<u8>, String)>();
    b_mgr.set_data_received(Arc::new(move |sender: &str, data: &[u8], via: &str| {
        let _ = tx.send((sender.to_string(), data.to_vec(), via.to_string()));
    }));

    assert!(b_t.reserve("R"), "B failed to reserve on the relay");
    a_t.set_route("B", "R"); // A learns B is reachable via R

    let payload = vec![0x11_u8, 0x22, 0x33, 0x44];
    // Send via the MANAGER, which must SELECT the relay (its only additional transport, cost 90).
    assert!(
        a_mgr.send_async("B", &payload).await.expect("send_async errored"),
        "a_mgr.send_async to B failed — relay was not selected"
    );

    let (sender, data, via) = rx
        .recv_timeout(Duration::from_secs(3))
        .expect("B never received the relayed message via TransportManager selection");
    assert_eq!(sender, "A");
    assert_eq!(data, payload);
    assert_eq!(via, "Circuit Relay (v2)", "the manager must have chosen the relay transport, by name");
    assert_eq!(r_t.active_bridge_count(), 1, "R should be bridging exactly one connection");
}
