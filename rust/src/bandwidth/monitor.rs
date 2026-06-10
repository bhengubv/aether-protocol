// SPDX-License-Identifier: MIT

//! Node activity monitor — UI-facing layer of the ABMF.
//!
//! Ports `src/AetherNet.Transport/Bandwidth/NodeActivityMonitor.cs`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use super::estimator::BandwidthEstimator;
use super::models::{NodeActivitySnapshot, NodeActivityState, TransportActivitySnapshot};

// ── Traffic accumulator ───────────────────────────────────────────────────────

struct TransportTraffic {
    ingress_bytes: AtomicI64,
    egress_bytes: AtomicI64,
    last_egress_ms: AtomicI64,
}

impl TransportTraffic {
    fn new() -> Self {
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or(Duration::ZERO)
            .as_millis() as i64;
        Self {
            ingress_bytes: AtomicI64::new(0),
            egress_bytes: AtomicI64::new(0),
            last_egress_ms: AtomicI64::new(now_ms),
        }
    }
}

// ── Shared state ──────────────────────────────────────────────────────────────

struct Shared {
    transports: HashMap<String, (Arc<Mutex<BandwidthEstimator>>, Arc<TransportTraffic>)>,
    // Maps peer_uhid → last-seen Unix ms. A peer is "active" if it had ingress or
    // egress within idle_threshold_ms. Populated only by the peer-aware
    // record_ingress_from_peer / record_egress_to_peer methods; the transport-only
    // methods do not contribute (the caller did not supply a peer). Stale entries
    // are pruned each tick so the map stays bounded by the count of recently-active
    // peers, not the lifetime peer set.
    last_seen_peer_ms: HashMap<String, i64>,
    sample_interval_ms: u64,
    idle_threshold_ms: i64,
    current: NodeActivitySnapshot,
    subscribers: Vec<Box<dyn Fn(NodeActivitySnapshot) + Send + 'static>>,
    running: bool,
}

// ── Monitor ───────────────────────────────────────────────────────────────────

/// Runs a background thread sampling ingress/egress rates and publishing
/// `NodeActivitySnapshot` objects at a configurable interval (default 500 ms).
pub struct NodeActivityMonitor {
    shared: Arc<Mutex<Shared>>,
}

impl NodeActivityMonitor {
    pub fn new() -> Self {
        let initial = offline_snapshot();
        Self {
            shared: Arc::new(Mutex::new(Shared {
                transports: HashMap::new(),
                last_seen_peer_ms: HashMap::new(),
                sample_interval_ms: 500,
                idle_threshold_ms: 5_000,
                current: initial,
                subscribers: Vec::new(),
                running: false,
            })),
        }
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    pub fn set_sample_interval_ms(&self, ms: u64) {
        self.shared.lock().unwrap().sample_interval_ms = ms.clamp(100, 60_000);
    }

    pub fn set_idle_threshold_seconds(&self, secs: i64) {
        self.shared.lock().unwrap().idle_threshold_ms = secs.clamp(1, 300) * 1000;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// Register a transport estimator so its activity is included in snapshots.
    pub fn register(&self, name: &str, estimator: Arc<Mutex<BandwidthEstimator>>) {
        let traffic = Arc::new(TransportTraffic::new());
        self.shared
            .lock()
            .unwrap()
            .transports
            .insert(name.to_string(), (estimator, traffic));
    }

    /// Record inbound bytes on a transport.
    pub fn record_ingress(&self, transport: &str, bytes: i32) {
        let g = self.shared.lock().unwrap();
        if let Some((_, traffic)) = g.transports.get(transport) {
            traffic.ingress_bytes.fetch_add(bytes as i64, Ordering::Relaxed);
        }
    }

    /// Record outbound bytes on a transport.
    pub fn record_egress(&self, transport: &str, bytes: i32) {
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or(Duration::ZERO)
            .as_millis() as i64;
        let g = self.shared.lock().unwrap();
        if let Some((_, traffic)) = g.transports.get(transport) {
            traffic.egress_bytes.fetch_add(bytes as i64, Ordering::Relaxed);
            traffic.last_egress_ms.store(now_ms, Ordering::Relaxed);
        }
    }

    /// Record inbound bytes on a transport from a specific peer.
    /// Tracks the peer for the `NodeActivitySnapshot::active_peers` count.
    pub fn record_ingress_from_peer(&self, transport: &str, peer_uhid: &str, bytes: i32) {
        self.record_ingress(transport, bytes);
        if !peer_uhid.is_empty() {
            let now_ms = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or(Duration::ZERO)
                .as_millis() as i64;
            self.shared
                .lock()
                .unwrap()
                .last_seen_peer_ms
                .insert(peer_uhid.to_string(), now_ms);
        }
    }

    /// Record outbound bytes on a transport to a specific peer.
    /// Tracks the peer for the `NodeActivitySnapshot::active_peers` count.
    pub fn record_egress_to_peer(&self, transport: &str, peer_uhid: &str, bytes: i32) {
        self.record_egress(transport, bytes);
        if !peer_uhid.is_empty() {
            let now_ms = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or(Duration::ZERO)
                .as_millis() as i64;
            self.shared
                .lock()
                .unwrap()
                .last_seen_peer_ms
                .insert(peer_uhid.to_string(), now_ms);
        }
    }

    // ── Subscription ─────────────────────────────────────────────────────────

    /// Subscribe a callback fired on every snapshot (heartbeat).
    pub fn subscribe(&self, cb: Box<dyn Fn(NodeActivitySnapshot) + Send + 'static>) {
        self.shared.lock().unwrap().subscribers.push(cb);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// Start the background sampling loop.
    pub fn start(&self) {
        {
            let mut g = self.shared.lock().unwrap();
            if g.running {
                return;
            }
            g.running = true;
        }
        let shared = Arc::clone(&self.shared);
        thread::Builder::new()
            .name("abmf-monitor".to_string())
            .spawn(move || run_loop(shared))
            .expect("failed to spawn monitor thread");
    }

    /// Stop the background sampling loop.
    pub fn stop(&self) {
        self.shared.lock().unwrap().running = false;
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    /// The most recent snapshot. Thread-safe.
    pub fn current(&self) -> NodeActivitySnapshot {
        self.shared.lock().unwrap().current.clone()
    }
}

impl Default for NodeActivityMonitor {
    fn default() -> Self {
        Self::new()
    }
}

// ── Background loop ───────────────────────────────────────────────────────────

fn run_loop(shared: Arc<Mutex<Shared>>) {
    let mut last_tick_ms = now_ms();

    loop {
        let interval_ms = {
            let g = shared.lock().unwrap();
            if !g.running {
                break;
            }
            g.sample_interval_ms
        };

        thread::sleep(Duration::from_millis(interval_ms));

        let now = now_ms();
        let elapsed_sec = ((now - last_tick_ms) / 1000.0).max(0.001);
        last_tick_ms = now;

        let snapshot = {
            let mut g = shared.lock().unwrap();
            if !g.running {
                break;
            }
            build_snapshot(&mut g, elapsed_sec, now)
        };

        let (prev_state, prev_total, prev_active_transports) = {
            let g = shared.lock().unwrap();
            (
                g.current.state,
                g.current.total_bps(),
                g.current.active_transports,
            )
        };

        // Publish snapshot: update current, then call subscribers WITHOUT holding the lock.
        // We take a snapshot of subscriber count, store the current snapshot, release the lock,
        // then dispatch callbacks individually (re-acquiring the lock per call to safely index in).
        let sub_count = {
            let mut g = shared.lock().unwrap();
            g.current = snapshot.clone();
            g.subscribers.len()
        };

        for idx in 0..sub_count {
            let cb_snapshot = snapshot.clone();
            // Re-acquire briefly to call the callback.  Callbacks must not call back into
            // the monitor or they will deadlock on this same mutex.
            let g = shared.lock().unwrap();
            if let Some(cb) = g.subscribers.get(idx) {
                cb(cb_snapshot);
            }
        }

        // Detect change for potential future SnapshotChanged event.
        let _changed = snapshot.state != prev_state
            || (snapshot.total_bps() - prev_total).abs() > 1_000
            || snapshot.active_transports != prev_active_transports;
    }
}

fn build_snapshot(g: &mut Shared, elapsed_sec: f64, now_ms: f64) -> NodeActivitySnapshot {
    let mut transport_snapshots: Vec<TransportActivitySnapshot> =
        Vec::with_capacity(g.transports.len());
    let mut total_ingress: i64 = 0;
    let mut total_egress: i64 = 0;
    let mut active_transports: i32 = 0;

    // Count distinct peers active within the idle window; prune stale entries
    // so the map stays bounded by recently-active peers.
    let now_i64 = now_ms as i64;
    let idle_threshold_ms = g.idle_threshold_ms;
    g.last_seen_peer_ms
        .retain(|_, last_seen| now_i64 - *last_seen < idle_threshold_ms);
    let active_peers = g.last_seen_peer_ms.len() as i32;

    for (name, (estimator, traffic)) in &g.transports {
        let ingress_delta = traffic.ingress_bytes.swap(0, Ordering::Relaxed);
        let egress_delta = traffic.egress_bytes.swap(0, Ordering::Relaxed);
        let ingress_bps = (ingress_delta as f64 * 8.0 / elapsed_sec) as i64;
        let egress_bps = (egress_delta as f64 * 8.0 / elapsed_sec) as i64;

        let sample = estimator.lock().unwrap().current_sample();
        let util_fraction = if sample.btl_bw_bps > 0 {
            (egress_bps as f64 / sample.btl_bw_bps as f64).clamp(0.0, 1.0)
        } else {
            0.0
        };

        let last_egress = traffic.last_egress_ms.load(Ordering::Relaxed);
        let is_recent = (now_ms as i64 - last_egress) < g.idle_threshold_ms;
        let state = compute_transport_state(egress_bps, ingress_bps, &sample, is_recent);

        if !matches!(state, NodeActivityState::Offline | NodeActivityState::Idle) {
            active_transports += 1;
        }

        total_ingress += ingress_bps;
        total_egress += egress_bps;

        transport_snapshots.push(TransportActivitySnapshot {
            transport_name: name.clone(),
            is_available: true,
            ingress_bps,
            egress_bps,
            srtt: sample.srtt,
            btl_bw_bps: sample.btl_bw_bps,
            utilization_fraction: util_fraction,
            state,
            confidence: sample.confidence,
        });
    }

    let node_state = compute_node_state(&transport_snapshots);
    let primary = transport_snapshots
        .iter()
        .max_by_key(|t| t.egress_bps)
        .filter(|t| t.egress_bps > 0)
        .map(|t| t.transport_name.clone());

    NodeActivitySnapshot {
        state: node_state,
        ingress_bps: total_ingress,
        egress_bps: total_egress,
        active_peers,
        active_transports,
        transports: transport_snapshots,
        primary_transport_name: primary,
        timestamp: SystemTime::now(),
    }
}

fn compute_transport_state(
    egress_bps: i64,
    ingress_bps: i64,
    sample: &super::models::BandwidthSample,
    is_recent: bool,
) -> NodeActivityState {
    if !is_recent && egress_bps == 0 && ingress_bps == 0 {
        return NodeActivityState::Idle;
    }
    if egress_bps == 0 && ingress_bps == 0 {
        return NodeActivityState::Idle;
    }
    if sample.loss_rate > 0.05 {
        return NodeActivityState::Degraded;
    }
    let util = if sample.btl_bw_bps > 0 {
        egress_bps as f64 / sample.btl_bw_bps as f64
    } else {
        0.0
    };
    if util >= 0.5 {
        NodeActivityState::Busy
    } else {
        NodeActivityState::Active
    }
}

fn compute_node_state(transports: &[TransportActivitySnapshot]) -> NodeActivityState {
    if transports.is_empty() {
        return NodeActivityState::Offline;
    }
    if transports.iter().any(|t| t.state == NodeActivityState::Degraded) {
        return NodeActivityState::Degraded;
    }
    if transports.iter().any(|t| t.state == NodeActivityState::Busy) {
        return NodeActivityState::Busy;
    }
    if transports.iter().any(|t| t.state == NodeActivityState::Active) {
        return NodeActivityState::Active;
    }
    if transports.iter().all(|t| t.state == NodeActivityState::Offline) {
        return NodeActivityState::Offline;
    }
    NodeActivityState::Idle
}

fn offline_snapshot() -> NodeActivitySnapshot {
    NodeActivitySnapshot {
        state: NodeActivityState::Offline,
        ingress_bps: 0,
        egress_bps: 0,
        active_peers: 0,
        active_transports: 0,
        transports: Vec::new(),
        primary_transport_name: None,
        timestamp: SystemTime::now(),
    }
}

fn now_ms() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .as_secs_f64()
        * 1000.0
}
