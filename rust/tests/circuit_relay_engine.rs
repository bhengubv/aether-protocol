// SPDX-License-Identifier: MIT
//! Behavioural proof of the native circuit-relay-v2 **engine**: a three-node topology
//! where A and B can each reach relay R but NOT each other directly. A message from A must
//! traverse the relay bridge to reach B — server off, no libp2p. Mirrors the Go
//! `go/circuitrelay/transport_test.go` (and the C# `CircuitRelayBridgeTests`).

use std::collections::HashMap;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use aethernet_protocol::circuitrelay::{Options, RelayLink, Transport};

// ── in-process one-hop mesh ──────────────────────────────────────────────────

/// Shared graph of undirected edges + a link per node. Frames only cross an existing edge,
/// and are delivered on a spawned thread (like a real transport) so the engine's blocking
/// CONNECT/RESERVE waits never deadlock the sender.
struct Mesh {
    edges: Mutex<HashMap<String, bool>>,
    links: Mutex<HashMap<String, Arc<ProcLink>>>,
}

impl Mesh {
    fn new() -> Arc<Mesh> {
        Arc::new(Mesh {
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
        *self
            .edges
            .lock()
            .unwrap()
            .get(&format!("{x}|{y}"))
            .unwrap_or(&false)
    }

    fn link(self: &Arc<Self>, node: &str) -> Arc<ProcLink> {
        let mut links = self.links.lock().unwrap();
        links
            .entry(node.to_string())
            .or_insert_with(|| {
                Arc::new(ProcLink {
                    mesh: Arc::clone(self),
                    node: node.to_string(),
                    handler: Mutex::new(None),
                })
            })
            .clone()
    }

    fn deliver(&self, from: &str, to: &str, frame: Vec<u8>) {
        if !self.adjacent(from, to) {
            return;
        }
        let l = self.links.lock().unwrap().get(to).cloned();
        if let Some(l) = l {
            let from = from.to_string();
            thread::spawn(move || {
                // Invoke the handler while holding its lock; the engine's handler does not
                // re-enter *this* link (a relay forwards on a different link's send path),
                // so there is no re-entrant deadlock on this mutex.
                if let Some(handler) = l.handler.lock().unwrap().as_ref() {
                    handler(from, frame);
                }
            });
        }
    }
}

struct ProcLink {
    mesh: Arc<Mesh>,
    node: String,
    handler: Mutex<Option<Box<dyn Fn(String, Vec<u8>) + Send + Sync>>>,
}

impl RelayLink for ProcLink {
    fn send_frame(&self, node: &str, frame: &[u8]) -> bool {
        if !self.mesh.adjacent(&self.node, node) {
            return false;
        }
        self.mesh.deliver(&self.node, node, frame.to_vec());
        true
    }

    fn can_reach(&self, node: &str) -> bool {
        self.mesh.adjacent(&self.node, node)
    }

    fn set_on_frame(&self, handler: Box<dyn Fn(String, Vec<u8>) + Send + Sync>) {
        *self.handler.lock().unwrap() = Some(handler);
    }
}

// ── controllable clock (epoch ms) ────────────────────────────────────────────

#[derive(Clone)]
struct TestClock {
    t: Arc<Mutex<i64>>,
}

impl TestClock {
    /// Starts at 2026-01-01T00:00:00Z in epoch ms.
    fn new() -> Self {
        TestClock {
            t: Arc::new(Mutex::new(1_767_225_600_000)),
        }
    }
    fn now(&self) -> i64 {
        *self.t.lock().unwrap()
    }
    fn advance(&self, d: Duration) {
        *self.t.lock().unwrap() += d.as_millis() as i64;
    }
    fn now_fn(&self) -> Box<dyn Fn() -> i64 + Send + Sync> {
        let t = Arc::clone(&self.t);
        Box::new(move || *t.lock().unwrap())
    }
}

/// Wall-clock (epoch ms) closure for nodes that don't need a controllable clock.
fn wall_clock() -> Box<dyn Fn() -> i64 + Send + Sync> {
    Box::new(|| {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64
    })
}

// ── receipt plumbing ─────────────────────────────────────────────────────────

#[derive(Debug, PartialEq, Eq)]
struct Recv {
    sender: String,
    data: String,
}

fn on_data_to(tx: Sender<Recv>) -> Box<dyn Fn(String, Vec<u8>) + Send + Sync> {
    Box::new(move |s, d| {
        let _ = tx.send(Recv {
            sender: s,
            data: String::from_utf8_lossy(&d).into_owned(),
        });
    })
}

fn wait_recv(rx: &Receiver<Recv>, what: &str) -> Recv {
    rx.recv_timeout(Duration::from_secs(3))
        .unwrap_or_else(|_| panic!("timeout waiting for {what}"))
}

/// Wires A ── R ── B with NO A-B edge. `relay_opts` / `relay_now` configure R. Returns the
/// three transports and the B / A receipt receivers.
fn build_line(
    relay_opts: Options,
    relay_now: Box<dyn Fn() -> i64 + Send + Sync>,
) -> (Transport, Transport, Transport, Receiver<Recv>, Receiver<Recv>) {
    let mesh = Mesh::new();
    mesh.connect("A", "R");
    mesh.connect("R", "B");

    let a = Transport::new("A", mesh.link("A"), Options::default(), wall_clock());
    let r = Transport::new("R", mesh.link("R"), relay_opts, relay_now);
    let b = Transport::new("B", mesh.link("B"), Options::default(), wall_clock());

    let (b_tx, b_rx) = mpsc::channel::<Recv>();
    let (a_tx, a_rx) = mpsc::channel::<Recv>();
    b.set_on_data(on_data_to(b_tx));
    a.set_on_data(on_data_to(a_tx));

    (a, r, b, b_rx, a_rx)
}

// ── (a) A→R→B relay, B receives; relay has one active bridge ─────────────────

#[test]
fn engine_message_traverses_relay_no_direct_link() {
    let (a, r, b, b_rx, _a_rx) = build_line(Options::default(), wall_clock());

    assert!(!a.is_connected("B"), "A should not be directly connected to B");
    assert!(b.reserve("R"), "B.reserve(R) failed");
    a.set_route("B", "R");

    assert!(a.send("B", b"deadbeef"), "A.send returned false");

    let got = wait_recv(&b_rx, "B receiving relayed message");
    assert_eq!(
        got,
        Recv {
            sender: "A".into(),
            data: "deadbeef".into()
        }
    );
    assert_eq!(r.active_bridge_count(), 1, "relay bridge count should be 1");
}

// ── (b) bridge is bidirectional ──────────────────────────────────────────────

#[test]
fn engine_bridge_is_bidirectional() {
    let (a, _r, b, b_rx, a_rx) = build_line(Options::default(), wall_clock());
    assert!(b.reserve("R"), "reserve failed");
    a.set_route("B", "R");
    assert!(a.send("B", b"hi"), "A.send failed");
    wait_recv(&b_rx, "B receiving");

    assert!(b.send("A", b"reply"), "B.send(A) failed");
    let got = wait_recv(&a_rx, "A receiving B's reply");
    assert_eq!(
        got,
        Recv {
            sender: "B".into(),
            data: "reply".into()
        }
    );
}

// ── (c) connect refused without a reservation ────────────────────────────────

#[test]
fn engine_connect_refused_without_reservation() {
    let (a, r, _b, b_rx, _a_rx) = build_line(Options::default(), wall_clock());
    a.set_route("B", "R"); // route known, but B never reserved

    assert!(!a.send("B", b"x"), "A.send should fail without a reservation");

    // B must not receive anything.
    assert!(
        b_rx.recv_timeout(Duration::from_millis(200)).is_err(),
        "B should not have received anything"
    );
    assert_eq!(r.active_bridge_count(), 0, "relay bridge count should be 0");
}

// ── (d) send fails with no route ─────────────────────────────────────────────

#[test]
fn engine_send_fails_without_route() {
    let (a, _r, b, _b_rx, _a_rx) = build_line(Options::default(), wall_clock());
    assert!(b.reserve("R"), "reserve failed");
    // no set_route
    assert!(!a.send("B", b"x"), "A.send should fail with no relay route known");
}

// ── (e) data budget 10: first 5B delivered, second 8B (cum 13) dropped + torn down ─

#[test]
fn engine_relay_enforces_data_budget() {
    let mut opts = Options::default();
    opts.bridge_data_limit_bytes = 10;
    let (a, r, b, b_rx, _a_rx) = build_line(opts, wall_clock());
    assert!(b.reserve("R"), "reserve failed");
    a.set_route("B", "R");

    assert!(a.send("B", &[1, 2, 3, 4, 5]), "first send failed"); // 5 bytes, within 10
    wait_recv(&b_rx, "first (in-budget) message");

    a.send("B", &[6, 7, 8, 9, 10, 11, 12, 13]); // 8 more -> 13 > 10 -> torn down
    assert!(
        b_rx.recv_timeout(Duration::from_millis(300)).is_err(),
        "over-budget message should not arrive"
    );
    assert_eq!(
        r.active_bridge_count(),
        0,
        "bridge should be torn down on budget breach"
    );
}

// ── (f) reservation expiry via injectable clock refuses connect ──────────────

#[test]
fn engine_reservation_expiry_refuses_connect() {
    let clk = TestClock::new();
    let mut opts = Options::default();
    opts.reservation_ttl = Duration::from_secs(30 * 60);
    let (a, _r, b, b_rx, _a_rx) = build_line(opts, clk.now_fn());

    assert!(b.reserve("R"), "reserve failed");
    a.set_route("B", "R");

    clk.advance(Duration::from_secs(31 * 60)); // past the reservation TTL on R's clock
    let _ = clk.now(); // (silence unused on some paths)

    assert!(!a.send("B", b"x"), "A.send should fail after reservation expiry");
    assert!(
        b_rx.recv_timeout(Duration::from_millis(200)).is_err(),
        "B should not receive after expiry"
    );
}
