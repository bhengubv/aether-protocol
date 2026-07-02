// SPDX-License-Identifier: MIT

//! Direct peer-to-peer transport for AetherNet over a WebRTC data channel
//! (pure-Rust [`webrtc`](https://crates.io/crates/webrtc), a.k.a. webrtc-rs).
//!
//! NAT traversal is handled by ICE/STUN, with WebRTC's own TURN as last resort.
//! The initial SDP/ICE handshake is carried by an injected [`Signaling`] channel
//! (e.g. the AetherNet QUIC/HTTP relay, the radio mesh, or an SMS ignition link),
//! so **no central signalling server is required**. This is Rust's first real,
//! internet-capable transport — the others are in-process simulations.
//!
//! Mirrors the C# (`AetherNet.Transport.WebRtc`, SIPSorcery) and Go
//! (`go/transport/webrtc`, pion) reference implementations:
//!   * a [`WebRtcTransport`] implementing [`TransportService`],
//!   * a [`Signal`] / [`SignalType`] message + a [`Signaling`] trait,
//!   * an [`InMemorySignalingBus`] routing signals in-process in send order,
//!   * trickle ICE over the signalling channel.
//!
//! Received bytes are surfaced through the same callback the in-process
//! transport uses ([`TransportService::set_data_received_handler`]); because the
//! data-channel callbacks fire on background tasks, the handler is shared so it
//! reaches every peer link, including ones created after it is registered.
//!
//! Gated behind the `webrtc` Cargo feature (the native stack is heavy and the
//! default build relies on the in-process transport).

use super::{PerTransportMetrics, TransportService};
use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use tokio::sync::{mpsc, oneshot, Mutex as AsyncMutex};

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::MediaEngine;
use webrtc::api::{APIBuilder, API};
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice_transport::ice_candidate::{RTCIceCandidate, RTCIceCandidateInit};
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::interceptor::registry::Registry;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;

const DATA_CHANNEL_LABEL: &str = "aether";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(20);

/// Callback invoked with `(peer_uhid, payload)` for every inbound message.
/// Shared across the transport and every peer link so it can be registered
/// once and still reach links created later.
type DataHandler = Arc<dyn Fn(&str, &[u8]) + Send + Sync>;
type SharedHandler = Arc<Mutex<Option<DataHandler>>>;

/// Serverless default: NO ICE servers, so a node never contacts a STUN/TURN server. Direct
/// links form on the same LAN or when a peer has a public address; for NAT traversal without a
/// server, route through the circuit-relay-v2 transport (peers relay for peers). Callers opt
/// into STUN/TURN by passing an explicit list.
pub fn default_ice_servers() -> Vec<RTCIceServer> {
    Vec::new()
}

// ── Signalling messages ────────────────────────────────────────────────────────

/// The kind of WebRTC signalling message exchanged while a direct link is set up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SignalType {
    /// SDP offer from the initiating peer.
    Offer,
    /// SDP answer from the responding peer.
    Answer,
    /// A trickled ICE candidate.
    IceCandidate,
}

/// A single WebRTC signalling message — an SDP offer/answer or an ICE candidate
/// that two peers must exchange before a direct data channel can open.
///
/// Carried by a [`Signaling`] channel (the relay, the mesh, or the in-process
/// bus), never a central signalling server.
#[derive(Debug, Clone)]
pub struct Signal {
    /// UHID of the node that produced this signal.
    pub from_uhid: String,
    /// UHID of the node this signal is addressed to.
    pub to_uhid: String,
    /// What this signal carries.
    pub signal_type: SignalType,
    /// SDP text — set for [`SignalType::Offer`] / [`SignalType::Answer`].
    pub sdp: Option<String>,
    /// ICE candidate string — set for [`SignalType::IceCandidate`].
    pub candidate: Option<String>,
    /// SDP mid for the ICE candidate.
    pub sdp_mid: Option<String>,
    /// SDP m-line index for the ICE candidate (0 for the single data section).
    pub sdp_mline_index: u16,
}

/// Carries WebRTC SDP/ICE signalling between two peers by UHID, so a direct data
/// channel can be negotiated without a central signalling server.
///
/// Any already-reachable channel can back this — the AetherNet QUIC/HTTP relay,
/// the radio mesh, or (for cold first contact) an SMS ignition link.
#[async_trait]
pub trait Signaling: Send + Sync {
    /// Delivers a signalling message to its addressee. Returns `true` if the
    /// signal was handed to the underlying channel.
    async fn send_signal(&self, peer_uhid: &str, signal: Signal) -> bool;

    /// Registers the handler invoked for signals addressed to the local node.
    fn on_signal(&self, handler: Box<dyn Fn(Signal) + Send + Sync>);
}

// ── In-memory signalling bus ────────────────────────────────────────────────────

/// In-process [`Signaling`] bus that routes signals between endpoints by UHID.
///
/// The reference signalling implementation: it needs no network and no server, so
/// it backs same-process scenarios (multi-node simulations, a single device
/// holding several identities) and the test suite. Production cross-device
/// signalling rides a real transport instead.
///
/// Each endpoint delivers inbound signals on its own single-reader queue, so
/// signals arrive in send order and never re-enter the sender's call stack —
/// matching the ordered, reliable delivery a real signalling channel provides.
pub struct InMemorySignalingBus {
    endpoints: Mutex<HashMap<String, Arc<BusEndpoint>>>,
}

impl InMemorySignalingBus {
    /// Creates an empty bus.
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            endpoints: Mutex::new(HashMap::new()),
        })
    }

    /// Returns (creating once) the [`Signaling`] endpoint for `uhid`.
    pub fn endpoint(self: &Arc<Self>, uhid: &str) -> Arc<dyn Signaling> {
        let mut map = self.endpoints.lock().unwrap();
        if let Some(existing) = map.get(uhid) {
            return existing.clone();
        }
        let endpoint = BusEndpoint::new(Arc::downgrade(self));
        map.insert(uhid.to_owned(), endpoint.clone());
        endpoint
    }

    fn route(&self, signal: Signal) -> bool {
        let target = {
            let map = self.endpoints.lock().unwrap();
            map.get(&signal.to_uhid).cloned()
        };
        match target {
            Some(endpoint) => endpoint.deliver(signal),
            None => false,
        }
    }
}

impl Default for InMemorySignalingBus {
    fn default() -> Self {
        Self {
            endpoints: Mutex::new(HashMap::new()),
        }
    }
}

struct BusEndpoint {
    bus: std::sync::Weak<InMemorySignalingBus>,
    tx: mpsc::UnboundedSender<Signal>,
    handler: Arc<Mutex<Option<Box<dyn Fn(Signal) + Send + Sync>>>>,
}

impl BusEndpoint {
    fn new(bus: std::sync::Weak<InMemorySignalingBus>) -> Arc<Self> {
        let (tx, mut rx) = mpsc::unbounded_channel::<Signal>();
        let handler: Arc<Mutex<Option<Box<dyn Fn(Signal) + Send + Sync>>>> =
            Arc::new(Mutex::new(None));

        // Single-reader pump: invokes the handler in send order, off the
        // sender's stack — mirrors the C#/Go reference endpoints.
        let pump_handler = handler.clone();
        tokio::spawn(async move {
            while let Some(signal) = rx.recv().await {
                let guard = pump_handler.lock().unwrap();
                if let Some(h) = guard.as_ref() {
                    h(signal);
                }
            }
        });

        Arc::new(Self { bus, tx, handler })
    }

    fn deliver(&self, signal: Signal) -> bool {
        // Unbounded queue; on a closed receiver the send fails (best-effort —
        // ICE re-gathers on reconnect).
        self.tx.send(signal).is_ok()
    }
}

#[async_trait]
impl Signaling for BusEndpoint {
    async fn send_signal(&self, _peer_uhid: &str, signal: Signal) -> bool {
        match self.bus.upgrade() {
            Some(bus) => bus.route(signal),
            None => false,
        }
    }

    fn on_signal(&self, handler: Box<dyn Fn(Signal) + Send + Sync>) {
        *self.handler.lock().unwrap() = Some(handler);
    }
}

// ── Transport ───────────────────────────────────────────────────────────────────

/// Direct peer-to-peer transport over a WebRTC data channel.
///
/// Implements [`TransportService`] so a transport manager can rank it between the
/// radio mesh (cheap, proximity) and the QUIC/HTTP relay (last resort) — a direct
/// internet path is used when one can be negotiated, otherwise the relay carries
/// the traffic.
pub struct WebRtcTransport {
    local_uhid: String,
    signaling: Arc<dyn Signaling>,
    ice_servers: Vec<RTCIceServer>,
    api: Arc<API>,
    metrics: Arc<PerTransportMetrics>,
    on_data: SharedHandler,
    peers: Arc<AsyncMutex<HashMap<String, Arc<PeerLink>>>>,
    closed: Arc<Mutex<bool>>,
}

impl WebRtcTransport {
    /// Builds a transport for `local_uhid`. With `None` `ice_servers` the
    /// transport uses the serverless default of NO ICE servers
    /// (host-candidate-only ICE) — it never contacts a STUN/TURN server, and
    /// links form on the same LAN or when a peer has a public address. For NAT
    /// traversal without a server, route through the circuit-relay-v2 transport
    /// (peers relay for peers). Pass an explicit list to opt into STUN/TURN; an
    /// explicit **empty** list keeps host-candidate-only ICE (as the loopback
    /// test does).
    pub async fn new(
        local_uhid: impl Into<String>,
        signaling: Arc<dyn Signaling>,
        ice_servers: Option<Vec<RTCIceServer>>,
    ) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let local_uhid = local_uhid.into();
        if local_uhid.is_empty() {
            return Err("webrtc: local_uhid required".into());
        }

        // None => the serverless default (NO ICE servers); an explicit (even
        // empty) list is respected verbatim, so a caller can keep
        // host-candidate-only ICE or opt into STUN/TURN.
        let ice_servers = ice_servers.unwrap_or_else(default_ice_servers);

        let mut media = MediaEngine::default();
        let registry = register_default_interceptors(Registry::new(), &mut media)?;

        // webrtc-rs defaults the multicast-DNS candidate mode to `Disabled`
        // (unlike a browser), so gathered host candidates carry raw IPs and a
        // host-candidate-only negotiation connects on the loopback path the test
        // exercises — matching pion's effective behaviour in the Go reference. We
        // therefore leave the SettingEngine at its default and only customise the
        // media engine + interceptors.
        let api = Arc::new(
            APIBuilder::new()
                .with_media_engine(media)
                .with_interceptor_registry(registry)
                .build(),
        );

        let transport = Arc::new(Self {
            local_uhid,
            signaling: signaling.clone(),
            ice_servers,
            api,
            metrics: PerTransportMetrics::new(),
            on_data: Arc::new(Mutex::new(None)),
            peers: Arc::new(AsyncMutex::new(HashMap::new())),
            closed: Arc::new(Mutex::new(false)),
        });

        // Route inbound signals to the handler. The bus pump invokes this
        // synchronously; we hop onto a task so the async link work doesn't run
        // on the pump.
        let weak = Arc::downgrade(&transport);
        signaling.on_signal(Box::new(move |signal: Signal| {
            if let Some(t) = weak.upgrade() {
                tokio::spawn(async move {
                    t.handle_signal(signal).await;
                });
            }
        }));

        Ok(transport)
    }

    /// Registers the handler for inbound bytes (the receive surface). Equivalent
    /// to [`TransportService::set_data_received_handler`] but available on the
    /// shared (`&self`) handle, since the transport is held behind an `Arc`.
    pub fn on_data_received(&self, handler: DataHandler) {
        *self.on_data.lock().unwrap() = Some(handler);
    }

    /// Tears down all peer connections and marks the transport unavailable
    /// (mirrors the Go `Close()` / C# `Dispose()`).
    pub async fn close(&self) {
        *self.closed.lock().unwrap() = true;
        let links: Vec<Arc<PeerLink>> = {
            let mut peers = self.peers.lock().await;
            peers.drain().map(|(_, link)| link).collect()
        };
        for link in links {
            link.close().await;
        }
    }

    async fn handle_signal(&self, signal: Signal) {
        if signal.to_uhid != self.local_uhid {
            return;
        }
        match signal.signal_type {
            SignalType::Offer => {
                if let Some(link) = self.get_or_create_link(&signal.from_uhid, false).await {
                    if let Some(sdp) = signal.sdp {
                        link.accept_offer(sdp).await;
                    }
                }
            }
            SignalType::Answer => {
                let link = {
                    let peers = self.peers.lock().await;
                    peers.get(&signal.from_uhid).cloned()
                };
                if let (Some(link), Some(sdp)) = (link, signal.sdp) {
                    link.accept_answer(sdp).await;
                }
            }
            SignalType::IceCandidate => {
                let link = {
                    let peers = self.peers.lock().await;
                    peers.get(&signal.from_uhid).cloned()
                };
                if let Some(link) = link {
                    link.add_remote_candidate(signal).await;
                }
            }
        }
    }

    async fn get_or_create_link(&self, peer_uhid: &str, initiator: bool) -> Option<Arc<PeerLink>> {
        if *self.closed.lock().unwrap() {
            return None;
        }

        {
            let peers = self.peers.lock().await;
            if let Some(existing) = peers.get(peer_uhid) {
                if !existing.is_closed() {
                    let link = existing.clone();
                    drop(peers);
                    if initiator {
                        link.wait_open(CONNECT_TIMEOUT).await;
                    }
                    return Some(link);
                }
            }
        }

        let link = match PeerLink::new(
            self.local_uhid.clone(),
            peer_uhid.to_owned(),
            &self.api,
            self.ice_servers.clone(),
            self.signaling.clone(),
            self.on_data.clone(),
        )
        .await
        {
            Ok(link) => link,
            Err(_) => return None,
        };

        {
            let mut peers = self.peers.lock().await;
            // Lost a race — discard ours, use the winner.
            if let Some(winner) = peers.get(peer_uhid) {
                if !winner.is_closed() {
                    let winner = winner.clone();
                    drop(peers);
                    link.close().await;
                    if initiator {
                        winner.wait_open(CONNECT_TIMEOUT).await;
                    }
                    return Some(winner);
                }
            }
            peers.insert(peer_uhid.to_owned(), link.clone());
        }

        if link.start(initiator).await.is_err() {
            return None;
        }
        if initiator {
            link.wait_open(CONNECT_TIMEOUT).await;
        }
        Some(link)
    }
}

#[async_trait]
impl TransportService for WebRtcTransport {
    fn name(&self) -> &str {
        "WebRTC P2P"
    }

    fn is_available(&self) -> bool {
        !*self.closed.lock().unwrap()
    }

    fn max_bandwidth_bps(&self) -> i64 {
        100_000_000 // direct link — bounded by the local NIC
    }

    fn max_range_meters(&self) -> i32 {
        0 // internet — unbounded
    }

    fn power_cost_relative(&self) -> i32 {
        5 // dearer than local radio on the 1-10 scale, cheaper than the relay
    }

    fn max_concurrent_peers(&self) -> i32 {
        256
    }

    async fn send_async(
        &self,
        peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        if peer_uhid.is_empty() {
            return Ok(false);
        }
        let link = match self.get_or_create_link(peer_uhid, true).await {
            Some(link) => link,
            None => return Ok(false),
        };
        let ok = link.send(data).await;
        self.metrics.record_sample(0, ok, if ok { data.len() as u64 } else { 0 });
        Ok(ok)
    }

    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>> {
        use std::io::Read;
        let mut data = Vec::new();
        stream.read_to_end(&mut data)?;
        self.send_async(peer_uhid, &data).await
    }

    fn is_connected(&self, peer_uhid: &str) -> bool {
        // Synchronous read of an async-locked map: try_lock keeps the trait
        // signature non-blocking. A contended lock reads as "not yet connected",
        // which is the safe answer for a ladder probe.
        if let Ok(peers) = self.peers.try_lock() {
            peers.get(peer_uhid).map(|l| l.is_open()).unwrap_or(false)
        } else {
            false
        }
    }

    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    ) {
        *self.on_data.lock().unwrap() = Some(Arc::from(handler));
    }

    fn metrics(&self) -> Option<Arc<PerTransportMetrics>> {
        Some(self.metrics.clone())
    }
}

// ── One WebRTC connection to a single peer ──────────────────────────────────────

struct PeerLink {
    local_uhid: String,
    peer_uhid: String,
    signaling: Arc<dyn Signaling>,
    /// Shared inbound-data handler, owned by the transport and cloned into every
    /// channel callback so both the initiator (its created channel) and the
    /// responder (the channel delivered by `on_data_channel`) surface bytes.
    on_data: SharedHandler,
    pc: Arc<RTCPeerConnection>,
    channel: AsyncMutex<Option<Arc<RTCDataChannel>>>,
    /// Resolved exactly once when the channel opens or the link closes.
    open_tx: Mutex<Option<oneshot::Sender<bool>>>,
    open_rx: AsyncMutex<Option<oneshot::Receiver<bool>>>,
    /// `(opened, closed)` latches. `opened` flips true once the data channel
    /// opens and never clears — so a transient `Disconnected` after a successful
    /// open does not retract the connected verdict (matching the Go/C# behaviour
    /// where the readyState/connection check still reads open during the live
    /// session). `closed` flips true on a terminal connection/channel state.
    flags: Mutex<LinkFlags>,
}

#[derive(Clone, Copy, Default)]
struct LinkFlags {
    opened: bool,
    closed: bool,
}

impl PeerLink {
    async fn new(
        local_uhid: String,
        peer_uhid: String,
        api: &Arc<API>,
        ice_servers: Vec<RTCIceServer>,
        signaling: Arc<dyn Signaling>,
        on_data: SharedHandler,
    ) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        let config = RTCConfiguration {
            ice_servers,
            ..Default::default()
        };
        let pc = Arc::new(api.new_peer_connection(config).await?);
        let (open_tx, open_rx) = oneshot::channel::<bool>();

        let link = Arc::new(Self {
            local_uhid,
            peer_uhid,
            signaling,
            on_data: on_data.clone(),
            pc: pc.clone(),
            channel: AsyncMutex::new(None),
            open_tx: Mutex::new(Some(open_tx)),
            open_rx: AsyncMutex::new(Some(open_rx)),
            flags: Mutex::new(LinkFlags::default()),
        });

        // Trickle local ICE candidates out over the signalling channel.
        {
            let weak = Arc::downgrade(&link);
            pc.on_ice_candidate(Box::new(move |candidate: Option<RTCIceCandidate>| {
                let weak = weak.clone();
                Box::pin(async move {
                    let Some(candidate) = candidate else {
                        return; // None signals end-of-gathering
                    };
                    let Some(link) = weak.upgrade() else { return };
                    if let Ok(init) = candidate.to_json() {
                        let signal = Signal {
                            from_uhid: link.local_uhid.clone(),
                            to_uhid: link.peer_uhid.clone(),
                            signal_type: SignalType::IceCandidate,
                            sdp: None,
                            candidate: Some(init.candidate),
                            sdp_mid: init.sdp_mid,
                            sdp_mline_index: init.sdp_mline_index.unwrap_or(0),
                        };
                        let _ = link
                            .signaling
                            .send_signal(&link.peer_uhid, signal)
                            .await;
                    }
                })
            }));
        }

        // Responder receives the channel here.
        {
            let weak = Arc::downgrade(&link);
            pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
                let weak = weak.clone();
                Box::pin(async move {
                    if let Some(link) = weak.upgrade() {
                        link.attach_channel(dc).await;
                    }
                })
            }));
        }

        // Terminal connection states close the link.
        {
            let weak = Arc::downgrade(&link);
            pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
                let weak = weak.clone();
                Box::pin(async move {
                    if matches!(
                        state,
                        RTCPeerConnectionState::Failed
                            | RTCPeerConnectionState::Disconnected
                            | RTCPeerConnectionState::Closed
                    ) {
                        if let Some(link) = weak.upgrade() {
                            link.mark_closed();
                        }
                    }
                })
            }));
        }

        Ok(link)
    }

    /// Begins the handshake. The initiator creates the data channel + sends the
    /// offer; the responder waits for the inbound offer (see `accept_offer`).
    async fn start(self: &Arc<Self>, initiator: bool) -> Result<(), Box<dyn std::error::Error>> {
        if !initiator {
            return Ok(());
        }

        let dc = self
            .pc
            .create_data_channel(DATA_CHANNEL_LABEL, None)
            .await?;
        // Initiator owns the channel it just created.
        self.attach_channel(dc).await;

        let offer = self.pc.create_offer(None).await?;
        self.pc.set_local_description(offer.clone()).await?;
        let _ = self
            .signaling
            .send_signal(
                &self.peer_uhid,
                Signal {
                    from_uhid: self.local_uhid.clone(),
                    to_uhid: self.peer_uhid.clone(),
                    signal_type: SignalType::Offer,
                    sdp: Some(offer.sdp),
                    candidate: None,
                    sdp_mid: None,
                    sdp_mline_index: 0,
                },
            )
            .await;
        Ok(())
    }

    async fn accept_offer(self: &Arc<Self>, sdp: String) {
        let offer = match RTCSessionDescription::offer(sdp) {
            Ok(o) => o,
            Err(_) => return,
        };
        if self.pc.set_remote_description(offer).await.is_err() {
            return;
        }
        let answer = match self.pc.create_answer(None).await {
            Ok(a) => a,
            Err(_) => return,
        };
        if self.pc.set_local_description(answer.clone()).await.is_err() {
            return;
        }
        let _ = self
            .signaling
            .send_signal(
                &self.peer_uhid,
                Signal {
                    from_uhid: self.local_uhid.clone(),
                    to_uhid: self.peer_uhid.clone(),
                    signal_type: SignalType::Answer,
                    sdp: Some(answer.sdp),
                    candidate: None,
                    sdp_mid: None,
                    sdp_mline_index: 0,
                },
            )
            .await;
    }

    async fn accept_answer(&self, sdp: String) {
        if let Ok(answer) = RTCSessionDescription::answer(sdp) {
            let _ = self.pc.set_remote_description(answer).await;
        }
    }

    async fn add_remote_candidate(&self, signal: Signal) {
        let Some(candidate) = signal.candidate else {
            return;
        };
        if candidate.is_empty() {
            return;
        }
        let init = RTCIceCandidateInit {
            candidate,
            sdp_mid: signal.sdp_mid,
            sdp_mline_index: Some(signal.sdp_mline_index),
            username_fragment: None,
        };
        let _ = self.pc.add_ice_candidate(init).await;
    }

    async fn attach_channel(self: &Arc<Self>, dc: Arc<RTCDataChannel>) {
        {
            let mut guard = self.channel.lock().await;
            *guard = Some(dc.clone());
        }

        // open → resolve the waiter exactly once.
        {
            let weak = Arc::downgrade(self);
            dc.on_open(Box::new(move || {
                let weak = weak.clone();
                Box::pin(async move {
                    if let Some(link) = weak.upgrade() {
                        link.mark_open();
                    }
                })
            }));
        }

        // message → surface bytes through the shared transport handler.
        {
            let peer = self.peer_uhid.clone();
            let on_data = self.on_data.clone();
            dc.on_message(Box::new(move |msg: DataChannelMessage| {
                let peer = peer.clone();
                let on_data = on_data.clone();
                Box::pin(async move {
                    let cb = {
                        let guard = on_data.lock().unwrap();
                        guard.clone()
                    };
                    if let Some(cb) = cb {
                        cb(&peer, &msg.data);
                    }
                })
            }));
        }

        // closed/error → terminal.
        {
            let weak = Arc::downgrade(self);
            dc.on_close(Box::new(move || {
                let weak = weak.clone();
                Box::pin(async move {
                    if let Some(link) = weak.upgrade() {
                        link.mark_closed();
                    }
                })
            }));
        }
        {
            let weak = Arc::downgrade(self);
            dc.on_error(Box::new(move |_err| {
                let weak = weak.clone();
                Box::pin(async move {
                    if let Some(link) = weak.upgrade() {
                        link.mark_closed();
                    }
                })
            }));
        }
    }

    fn mark_open(&self) {
        {
            let mut flags = self.flags.lock().unwrap();
            if flags.opened {
                return;
            }
            flags.opened = true;
        }
        if let Some(tx) = self.open_tx.lock().unwrap().take() {
            let _ = tx.send(true);
        }
    }

    fn mark_closed(&self) {
        {
            let mut flags = self.flags.lock().unwrap();
            if flags.closed {
                return;
            }
            flags.closed = true;
        }
        // Resolve any still-pending waiter as "never opened". If the channel had
        // already opened, the sender was consumed by `mark_open` and this is a
        // no-op — the `opened` latch keeps `is_open` true for the live session.
        if let Some(tx) = self.open_tx.lock().unwrap().take() {
            let _ = tx.send(false);
        }
    }

    /// True once the data channel has opened (the latch never clears, so it keeps
    /// reporting connected through a transient disconnect during a live session).
    fn is_open(&self) -> bool {
        self.flags.lock().unwrap().opened
    }

    /// True once the link has hit a terminal state — used to discard a dead link
    /// and renegotiate a fresh one.
    fn is_closed(&self) -> bool {
        self.flags.lock().unwrap().closed
    }

    async fn wait_open(&self, timeout: Duration) -> bool {
        // `opened` is the authoritative latch — check it before `closed` so a
        // link that opened and later tore down still reports the successful open.
        if self.is_open() {
            return true;
        }
        if self.is_closed() {
            return false;
        }
        let rx = {
            let mut guard = self.open_rx.lock().await;
            guard.take()
        };
        match rx {
            Some(rx) => match tokio::time::timeout(timeout, rx).await {
                Ok(Ok(opened)) => opened,
                _ => self.is_open(),
            },
            // Receiver already consumed by an earlier waiter — fall back to the
            // current state once it settles.
            None => self.is_open(),
        }
    }

    async fn send(&self, data: &[u8]) -> bool {
        if !self.wait_open(CONNECT_TIMEOUT).await {
            return false;
        }
        let channel = {
            let guard = self.channel.lock().await;
            guard.clone()
        };
        match channel {
            Some(dc) => dc.send(&Bytes::copy_from_slice(data)).await.is_ok(),
            None => false,
        }
    }

    async fn close(&self) {
        self.mark_closed();
        {
            let dc = {
                let guard = self.channel.lock().await;
                guard.clone()
            };
            if let Some(dc) = dc {
                let _ = dc.close().await;
            }
        }
        let _ = self.pc.close().await;
    }
}

// ── Loopback test ───────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// Stands up two real [`WebRtcTransport`] instances wired only through an
    /// in-process signalling bus — no central server, no STUN — and proves a
    /// direct data channel negotiates over host candidates and carries bytes.
    #[tokio::test]
    async fn two_peers_exchange_bytes_no_server() {
        let bus = InMemorySignalingBus::new();

        // empty (not None) => host-candidate-only ICE, no network dependency.
        let host_only: Vec<RTCIceServer> = Vec::new();

        let alice = WebRtcTransport::new("alice", bus.endpoint("alice"), Some(host_only.clone()))
            .await
            .expect("new alice");
        let bob = WebRtcTransport::new("bob", bus.endpoint("bob"), Some(host_only))
            .await
            .expect("new bob");

        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        bob.on_data_received(Arc::new(move |from: &str, data: &[u8]| {
            if from == "alice" {
                let _ = tx.send(data.to_vec());
            }
        }));

        let payload = b"hello over a serverless webrtc datachannel".to_vec();
        let ok = alice
            .send_async("bob", &payload)
            .await
            .expect("send result");
        assert!(ok, "send_async should report success");

        let received = tokio::time::timeout(Duration::from_secs(30), rx.recv())
            .await
            .expect("timed out waiting for bytes over the data channel")
            .expect("sender dropped before delivering bytes");

        assert_eq!(received, payload, "payload mismatch over the data channel");

        assert!(alice.is_connected("bob"), "alice should report connected to bob");
        assert!(bob.is_connected("alice"), "bob should report connected to alice");
    }

    /// Checks the ladder-facing metadata, mirroring the Go reference.
    #[tokio::test]
    async fn transport_metadata() {
        let bus = InMemorySignalingBus::new();
        let tr = WebRtcTransport::new("x", bus.endpoint("x"), Some(Vec::new()))
            .await
            .expect("new");

        assert_eq!(tr.name(), "WebRTC P2P");
        assert!(tr.is_available());
        assert_eq!(tr.max_range_meters(), 0, "internet range should be 0 (unbounded)");
        assert!(tr.metrics().is_some());
    }
}
