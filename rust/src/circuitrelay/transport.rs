// SPDX-License-Identifier: MIT

//! Native circuit-relay-v2 **engine** — the decentralised, no-libp2p equivalent of
//! libp2p's circuit-relay-v2. Any AetherNet node can act as a relay: a node that cannot
//! reach a peer directly routes through a third node reachable to both. Built on top of
//! the [`RelayFrame`] wire codec in [`super`]; a faithful port of the C#
//! `AetherNet.Transport.CircuitRelay.CircuitRelayTransportService` and the Go
//! `go/circuitrelay.Transport`.
//!
//! Three roles, all in this one engine (a node can be any/all at once):
//! - **Target** — [`Transport::reserve`] reserves capacity on a relay so peers behind NAT
//!   can be reached via that relay.
//! - **Client** — [`Transport::send`] to a peer for which a relay route is known
//!   ([`Transport::set_route`]) performs the CONNECT handshake then tunnels DATA.
//! - **Relay** — grants reservations, bridges CONNECT→STOP, and forwards DATA between the
//!   two legs under a data/duration budget.
//!
//! One hop of a frame is carried by an injected [`RelayLink`]. Shared engine state lives
//! behind a single [`Mutex`]; the CONNECT/RESERVE response waits use
//! [`std::sync::mpsc`] channels with [`Receiver::recv_timeout`]. The clock is injectable
//! (epoch-ms closure) for deterministic reservation-expiry tests.

use std::collections::HashMap;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use uuid::Uuid;

use super::{deserialize, serialize, MessageType, RelayFrame, Status};

/// The one-hop link a [`Transport`] uses to exchange raw relay frames with
/// *directly-reachable* nodes — the seam between circuit-relay-v2 (transport-agnostic)
/// and whatever real transport carries a frame one hop (BLE, Wi-Fi Direct, WebRTC, the
/// HTTP relay, or an in-process link in tests). Mirrors the C# `IRelayLink` and Go
/// `RelayLink`.
///
/// Interior mutability is expected: `set_on_frame` mutates through a shared reference, so
/// implementors guard the handler behind a `Mutex<Option<...>>`.
pub trait RelayLink: Send + Sync {
    /// Sends a raw relay frame to a node reachable in one hop. Returns `true` if the frame
    /// was handed to that node's link.
    fn send_frame(&self, node: &str, frame: &[u8]) -> bool;

    /// Whether this node currently has a direct one-hop link to `node`.
    fn can_reach(&self, node: &str) -> bool;

    /// Registers the handler invoked when a raw frame arrives from a directly-reachable
    /// node (sender node UHID, frame bytes).
    fn set_on_frame(&self, handler: Box<dyn Fn(String, Vec<u8>) + Send + Sync>);
}

/// Tuning + policy for a [`Transport`] (mirrors C# `CircuitRelayOptions` / Go `Options`).
#[derive(Debug, Clone)]
pub struct Options {
    /// How long a granted reservation remains valid.
    pub reservation_ttl: Duration,
    /// Maximum concurrent reservations this node will hold as a relay.
    pub max_reservations: usize,
    /// Maximum concurrent bridges this node will service as a relay.
    pub max_bridges: usize,
    /// Per-bridge data budget in bytes granted by this relay. 0 = unlimited.
    pub bridge_data_limit_bytes: i64,
    /// Per-bridge duration budget in seconds granted by this relay. 0 = unlimited.
    pub bridge_duration_limit_seconds: i32,
    /// How long a client waits for a CONNECT to be confirmed before giving up.
    pub connect_timeout: Duration,
    /// How long a client waits for a RESERVE to be confirmed before giving up.
    pub reserve_timeout: Duration,
    /// Whether this node grants reservations and bridges traffic for others.
    pub act_as_relay: bool,
}

impl Default for Options {
    /// The same defaults as the C# / Go references.
    fn default() -> Self {
        Options {
            reservation_ttl: Duration::from_secs(30 * 60),
            max_reservations: 128,
            max_bridges: 128,
            bridge_data_limit_bytes: 0,
            bridge_duration_limit_seconds: 0,
            connect_timeout: Duration::from_secs(10),
            reserve_timeout: Duration::from_secs(10),
            act_as_relay: true,
        }
    }
}

/// A bridge this node is relaying (relay role).
struct RelayBridge {
    a: String,
    b: String,
    data_budget: i64,
    /// Deadline as epoch ms; `0` => no duration limit.
    deadline_ms: i64,
    data_used: i64,
    open: bool,
}

/// An established bridge from this node's endpoint view: which connection, via which relay.
#[derive(Clone)]
struct ActiveBridge {
    conn_id: Uuid,
    relay: String,
}

/// All shared engine state, guarded by one [`Mutex`] (a struct of maps behind one lock).
#[derive(Default)]
struct State {
    // Relay role.
    reservations: HashMap<String, i64>, // client UHID -> expiry epoch ms
    bridges: HashMap<Uuid, RelayBridge>,
    // Client / target role.
    routes: HashMap<String, String>,        // dest -> relay
    peer_bridges: HashMap<String, ActiveBridge>, // peer -> bridge
    pending_connects: HashMap<Uuid, Sender<Status>>,
    pending_reservations: HashMap<String, Sender<Status>>,
}

/// The inner, shared engine — held behind an `Arc` so the link's frame handler (which
/// runs on another thread) can reach back into it.
struct Inner {
    local_uhid: String,
    link: Arc<dyn RelayLink>,
    opts: Options,
    now: Box<dyn Fn() -> i64 + Send + Sync>,
    state: Mutex<State>,
    on_data: Mutex<Option<Box<dyn Fn(String, Vec<u8>) + Send + Sync>>>,
}

/// The native circuit-relay-v2 engine. Clone-cheap handle over a shared [`Inner`].
///
/// Wire it onto a link with [`Transport::new`]; the link's inbound handler is registered
/// during construction and dispatches frames back into the engine.
#[derive(Clone)]
pub struct Transport {
    inner: Arc<Inner>,
}

impl Transport {
    /// Wires a `Transport` onto a `link`. `now` returns epoch **milliseconds** and is
    /// injectable for deterministic reservation-expiry tests; pass a wall-clock closure in
    /// production.
    pub fn new(
        local_uhid: impl Into<String>,
        link: Arc<dyn RelayLink>,
        opts: Options,
        now: Box<dyn Fn() -> i64 + Send + Sync>,
    ) -> Self {
        let inner = Arc::new(Inner {
            local_uhid: local_uhid.into(),
            link: Arc::clone(&link),
            opts,
            now,
            state: Mutex::new(State::default()),
            on_data: Mutex::new(None),
        });

        // Register the inbound frame dispatcher. The link delivers frames on another
        // thread (the in-process test link spawns one), so re-entrancy is not a concern.
        let weak = Arc::downgrade(&inner);
        link.set_on_frame(Box::new(move |from, frame| {
            if let Some(inner) = weak.upgrade() {
                Transport::on_frame(&inner, &from, &frame);
            }
        }));

        Transport { inner }
    }

    // ── Public target / client API ──────────────────────────────────────────

    /// Registers the callback invoked when tunnelled data is delivered to this node as an
    /// endpoint (sender UHID, payload).
    pub fn set_on_data(&self, cb: Box<dyn Fn(String, Vec<u8>) + Send + Sync>) {
        *self.inner.on_data.lock().unwrap() = Some(cb);
    }

    /// Records that `dest` is reachable via `relay` (in production, from the directory /
    /// reservation gossip; tests set it directly).
    pub fn set_route(&self, dest: impl Into<String>, relay: impl Into<String>) {
        self.inner
            .state
            .lock()
            .unwrap()
            .routes
            .insert(dest.into(), relay.into());
    }

    /// Number of bridges this node is currently servicing as a relay (diagnostics/tests).
    pub fn active_bridge_count(&self) -> usize {
        self.inner.state.lock().unwrap().bridges.len()
    }

    /// Number of reservations this node is currently holding as a relay (diagnostics/tests).
    pub fn active_reservation_count(&self) -> usize {
        self.inner.state.lock().unwrap().reservations.len()
    }

    /// Whether a relay bridge to `peer` has been established (this node as an endpoint).
    pub fn is_connected(&self, peer: &str) -> bool {
        self.inner
            .state
            .lock()
            .unwrap()
            .peer_bridges
            .contains_key(peer)
    }

    /// Reserves capacity on `relay` so peers can reach this node through it. Returns `true`
    /// once the relay confirms the reservation.
    pub fn reserve(&self, relay: &str) -> bool {
        let inner = &self.inner;
        if !inner.link.can_reach(relay) {
            return false;
        }

        let (tx, rx) = mpsc::channel::<Status>();
        inner
            .state
            .lock()
            .unwrap()
            .pending_reservations
            .insert(relay.to_string(), tx);

        let result = (|| {
            let frame = RelayFrame {
                message_type: MessageType::Reserve,
                status: Status::Ok,
                source_uhid: inner.local_uhid.clone(),
                destination_uhid: String::new(),
                relay_uhid: relay.to_string(),
                connection_id: Uuid::nil(),
                reservation_expires_at_ms: 0,
                limit_duration_seconds: 0,
                limit_data_bytes: 0,
                payload: Vec::new(),
            };
            let bytes = match serialize(&frame) {
                Ok(b) => b,
                Err(_) => return false,
            };
            inner.link.send_frame(relay, &bytes);
            Transport::await_status(&rx, inner.opts.reserve_timeout) == Status::Ok
        })();

        inner
            .state
            .lock()
            .unwrap()
            .pending_reservations
            .remove(relay);
        result
    }

    /// Delivers `data` to `peer`, establishing a relay bridge first if needed. Returns
    /// `true` if the frame was handed to the relay's link.
    pub fn send(&self, peer: &str, data: &[u8]) -> bool {
        let inner = &self.inner;

        // Existing bridge? tunnel straight over it.
        let existing = inner.state.lock().unwrap().peer_bridges.get(peer).cloned();
        if let Some(ab) = existing {
            return self.send_data(&ab, peer, data);
        }

        // No bridge yet — establish one through the known relay for this peer.
        let relay = inner.state.lock().unwrap().routes.get(peer).cloned();
        let relay = match relay {
            Some(r) if inner.link.can_reach(&r) => r,
            _ => return false,
        };

        if self.connect(peer, &relay) != Status::Ok {
            return false;
        }

        let ab = inner.state.lock().unwrap().peer_bridges.get(peer).cloned();
        match ab {
            Some(ab) => self.send_data(&ab, peer, data),
            None => false,
        }
    }

    // ── Client handshake ────────────────────────────────────────────────────

    fn connect(&self, dest: &str, relay: &str) -> Status {
        let inner = &self.inner;
        let conn_id = Uuid::new_v4();
        let (tx, rx) = mpsc::channel::<Status>();
        inner
            .state
            .lock()
            .unwrap()
            .pending_connects
            .insert(conn_id, tx);

        let result = (|| {
            let frame = RelayFrame {
                message_type: MessageType::Connect,
                status: Status::Ok,
                source_uhid: inner.local_uhid.clone(),
                destination_uhid: dest.to_string(),
                relay_uhid: relay.to_string(),
                connection_id: conn_id,
                reservation_expires_at_ms: 0,
                limit_duration_seconds: 0,
                limit_data_bytes: 0,
                payload: Vec::new(),
            };
            let bytes = match serialize(&frame) {
                Ok(b) => b,
                Err(_) => return Status::ConnectionFailed,
            };
            if !inner.link.send_frame(relay, &bytes) {
                return Status::ConnectionFailed;
            }
            Transport::await_status(&rx, inner.opts.connect_timeout)
        })();

        inner.state.lock().unwrap().pending_connects.remove(&conn_id);
        result
    }

    fn send_data(&self, bridge: &ActiveBridge, peer: &str, data: &[u8]) -> bool {
        let inner = &self.inner;
        let frame = RelayFrame {
            message_type: MessageType::Data,
            status: Status::Ok,
            source_uhid: inner.local_uhid.clone(),
            destination_uhid: peer.to_string(),
            relay_uhid: bridge.relay.clone(),
            connection_id: bridge.conn_id,
            reservation_expires_at_ms: 0,
            limit_duration_seconds: 0,
            limit_data_bytes: 0,
            payload: data.to_vec(),
        };
        match serialize(&frame) {
            Ok(bytes) => inner.link.send_frame(&bridge.relay, &bytes),
            Err(_) => false,
        }
    }

    /// Blocks up to `timeout` for a response status; on timeout (or a dropped sender)
    /// returns [`Status::ConnectionFailed`], matching the C#/Go behaviour.
    fn await_status(rx: &Receiver<Status>, timeout: Duration) -> Status {
        rx.recv_timeout(timeout).unwrap_or(Status::ConnectionFailed)
    }

    // ── Inbound frame dispatch ──────────────────────────────────────────────

    fn on_frame(inner: &Arc<Inner>, from: &str, frame: &[u8]) {
        let f = match deserialize(frame) {
            Ok(f) => f,
            Err(_) => return, // drop malformed
        };
        match f.message_type {
            MessageType::Reserve => Transport::handle_reserve(inner, from, &f),
            MessageType::ReserveResponse => Transport::handle_reserve_response(inner, from, &f),
            MessageType::Connect => Transport::handle_connect(inner, from, &f),
            MessageType::Stop => Transport::handle_stop(inner, from, &f),
            MessageType::StopResponse => Transport::handle_stop_response(inner, from, &f),
            MessageType::ConnectResponse => Transport::handle_connect_response(inner, from, &f),
            MessageType::Data => Transport::handle_data(inner, from, &f),
        }
    }

    // Relay: grant/refuse a reservation.
    fn handle_reserve(inner: &Arc<Inner>, from: &str, f: &RelayFrame) {
        let expiry_ms = {
            let mut st = inner.state.lock().unwrap();
            if !inner.opts.act_as_relay || st.reservations.len() >= inner.opts.max_reservations {
                drop(st);
                Transport::send_frame(
                    inner,
                    from,
                    &reply_frame(
                        MessageType::ReserveResponse,
                        f.source_uhid.clone(),
                        String::new(),
                        inner.local_uhid.clone(),
                        Uuid::nil(),
                        Status::ReservationRefused,
                    ),
                );
                return;
            }
            let expiry = (inner.now)() + inner.opts.reservation_ttl.as_millis() as i64;
            st.reservations.insert(f.source_uhid.clone(), expiry);
            expiry
        };

        let mut reply = reply_frame(
            MessageType::ReserveResponse,
            f.source_uhid.clone(),
            String::new(),
            inner.local_uhid.clone(),
            Uuid::nil(),
            Status::Ok,
        );
        reply.reservation_expires_at_ms = expiry_ms;
        Transport::send_frame(inner, from, &reply);
    }

    // Client: reservation confirmed/denied.
    fn handle_reserve_response(inner: &Arc<Inner>, from: &str, f: &RelayFrame) {
        let tx = inner
            .state
            .lock()
            .unwrap()
            .pending_reservations
            .get(from)
            .cloned();
        if let Some(tx) = tx {
            let _ = tx.send(f.status);
        }
    }

    // Relay: A wants B. Validate B's reservation + reachability, open a STOP to B.
    fn handle_connect(inner: &Arc<Inner>, _from: &str, f: &RelayFrame) {
        let a = f.source_uhid.clone();
        let b = f.destination_uhid.clone();
        let conn_id = f.connection_id;

        if !inner.opts.act_as_relay {
            Transport::reply_connect(inner, &a, f, Status::ConnectionFailed);
            return;
        }

        let mut st = inner.state.lock().unwrap();

        match st.reservations.get(&b).copied() {
            Some(exp) if (inner.now)() < exp => {}
            _ => {
                st.reservations.remove(&b);
                drop(st);
                Transport::reply_connect(inner, &a, f, Status::NoReservation);
                return;
            }
        }

        if !inner.link.can_reach(&b) {
            drop(st);
            Transport::reply_connect(inner, &a, f, Status::ConnectionFailed);
            return;
        }

        if st.bridges.len() >= inner.opts.max_bridges {
            drop(st);
            Transport::reply_connect(inner, &a, f, Status::ResourceLimitExceeded);
            return;
        }

        let deadline_ms = if inner.opts.bridge_duration_limit_seconds > 0 {
            (inner.now)() + inner.opts.bridge_duration_limit_seconds as i64 * 1000
        } else {
            0
        };
        st.bridges.insert(
            conn_id,
            RelayBridge {
                a: a.clone(),
                b: b.clone(),
                data_budget: inner.opts.bridge_data_limit_bytes,
                deadline_ms,
                data_used: 0,
                open: false,
            },
        );
        drop(st);

        let mut stop = reply_frame(
            MessageType::Stop,
            a,
            b.clone(),
            inner.local_uhid.clone(),
            conn_id,
            Status::Ok,
        );
        stop.limit_data_bytes = inner.opts.bridge_data_limit_bytes;
        stop.limit_duration_seconds = inner.opts.bridge_duration_limit_seconds;
        Transport::send_frame(inner, &b, &stop);
    }

    // Target: relay says A wants us. Accept and record a return route to A.
    fn handle_stop(inner: &Arc<Inner>, from: &str, f: &RelayFrame) {
        inner.state.lock().unwrap().peer_bridges.insert(
            f.source_uhid.clone(),
            ActiveBridge {
                conn_id: f.connection_id,
                relay: from.to_string(),
            },
        );
        let reply = reply_frame(
            MessageType::StopResponse,
            f.source_uhid.clone(),
            inner.local_uhid.clone(),
            from.to_string(),
            f.connection_id,
            Status::Ok,
        );
        Transport::send_frame(inner, from, &reply);
    }

    // Relay: target accepted/refused. Finalise the bridge and answer the client.
    fn handle_stop_response(inner: &Arc<Inner>, _from: &str, f: &RelayFrame) {
        let conn_id = f.connection_id;
        let mut st = inner.state.lock().unwrap();
        let bridge = match st.bridges.get_mut(&conn_id) {
            Some(b) => b,
            None => return,
        };

        if f.status != Status::Ok {
            let a = bridge.a.clone();
            st.bridges.remove(&conn_id);
            drop(st);
            Transport::reply_connect(inner, &a, f, Status::ConnectionFailed);
            return;
        }

        bridge.open = true;
        let a = bridge.a.clone();
        let b = bridge.b.clone();
        let budget = bridge.data_budget;
        drop(st);

        let mut ok = reply_frame(
            MessageType::ConnectResponse,
            a.clone(),
            b,
            inner.local_uhid.clone(),
            conn_id,
            Status::Ok,
        );
        ok.limit_data_bytes = budget;
        Transport::send_frame(inner, &a, &ok);
    }

    // Client: bridge established/refused.
    fn handle_connect_response(inner: &Arc<Inner>, from: &str, f: &RelayFrame) {
        let mut st = inner.state.lock().unwrap();
        if f.status == Status::Ok {
            st.peer_bridges.insert(
                f.destination_uhid.clone(),
                ActiveBridge {
                    conn_id: f.connection_id,
                    relay: from.to_string(),
                },
            );
        }
        let tx = st.pending_connects.get(&f.connection_id).cloned();
        drop(st);
        if let Some(tx) = tx {
            let _ = tx.send(f.status);
        }
    }

    // Data: endpoint delivery, or relay forward (under budget).
    fn handle_data(inner: &Arc<Inner>, from: &str, f: &RelayFrame) {
        if f.destination_uhid == inner.local_uhid {
            let cb = inner.on_data.lock().unwrap();
            if let Some(cb) = cb.as_ref() {
                cb(f.source_uhid.clone(), f.payload.clone());
            }
            return;
        }

        let conn_id = f.connection_id;
        {
            let mut st = inner.state.lock().unwrap();
            let bridge = match st.bridges.get_mut(&conn_id) {
                Some(b) if b.open && (from == b.a || from == b.b) => b,
                _ => return, // unknown / not-yet-open bridge, or frame not from a party — drop
            };

            if bridge.deadline_ms != 0 && (inner.now)() >= bridge.deadline_ms {
                st.bridges.remove(&conn_id);
                return;
            }

            bridge.data_used += f.payload.len() as i64;
            let over = bridge.data_budget > 0 && bridge.data_used > bridge.data_budget;
            if over {
                st.bridges.remove(&conn_id);
                return;
            }
        }

        // Forward the frame unchanged to the other endpoint (= its dst).
        if let Ok(bytes) = serialize(f) {
            inner.link.send_frame(&f.destination_uhid, &bytes);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    fn send_frame(inner: &Arc<Inner>, to: &str, f: &RelayFrame) {
        if let Ok(bytes) = serialize(f) {
            inner.link.send_frame(to, &bytes);
        }
    }

    fn reply_connect(inner: &Arc<Inner>, client: &str, connect: &RelayFrame, status: Status) {
        let reply = reply_frame(
            MessageType::ConnectResponse,
            connect.source_uhid.clone(),
            connect.destination_uhid.clone(),
            inner.local_uhid.clone(),
            connect.connection_id,
            status,
        );
        Transport::send_frame(inner, client, &reply);
    }
}

/// Builds a frame with the common header fields set and the numeric limits zeroed. Callers
/// override `reservation_expires_at_ms` / `limit_*` afterwards where relevant.
fn reply_frame(
    message_type: MessageType,
    source_uhid: String,
    destination_uhid: String,
    relay_uhid: String,
    connection_id: Uuid,
    status: Status,
) -> RelayFrame {
    RelayFrame {
        message_type,
        status,
        source_uhid,
        destination_uhid,
        relay_uhid,
        connection_id,
        reservation_expires_at_ms: 0,
        limit_duration_seconds: 0,
        limit_data_bytes: 0,
        payload: Vec::new(),
    }
}
