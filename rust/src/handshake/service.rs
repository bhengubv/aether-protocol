// SPDX-License-Identifier: MIT

//! Capability handshake service. Mirror of the C# `HandshakeService`
//! (commit 9380631).
//!
//! Wire flow:
//! ```text
//! A → B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
//! A ← B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
//! ```
//!
//! Negotiation rules:
//!   * Negotiated version = `min(ourMax, theirMax)`.
//!   * If `min(ourMax, theirMax) < max(ourMin, theirMin)` the ranges do not
//!     overlap → emit an [`IncompatiblePeer`] event, refuse to lock in.
//!   * Locked-in capability set = `ourCaps ∩ theirCaps`.

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;
use std::time::SystemTime;

use crate::constants::PROTOCOL_VERSION_SIGNED;
use crate::handshake::hello_payload::{
    HelloPayload, IncompatiblePeer, IncompatibleReason, PeerCapabilities,
};
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::MeshSender;

/// Default capability tags advertised by this implementation. Mirrors C#
/// `HandshakeService.DefaultCapabilities`.
pub fn default_capabilities() -> HashSet<String> {
    [
        "signal-x3dh",
        "double-ratchet",
        "dtn-custody",
        "sos",
        "voice",
        "stream",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect()
}

/// Default implementation banner emitted in our Hello/HelloAck.
pub const DEFAULT_IMPLEMENTATION: &str = "aether/2";

/// Outcome of a single received Hello / HelloAck.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HandshakeEvent {
    /// We negotiated a protocol-version + capability intersection with the
    /// peer. Caller (host) typically broadcasts this on a peer-changed event
    /// stream.
    Negotiated(PeerCapabilities),
    /// The peer's announced range can't be reconciled with ours. No session
    /// is locked in.
    Incompatible(IncompatiblePeer),
}

/// Per-instance state. Held inside a [`Mutex`] so the service is Send + Sync.
struct State {
    /// Peers we have already sent a Hello to, to suppress duplicate sends.
    hello_sent: HashSet<String>,
    /// Peers we have finished negotiating with.
    negotiated: HashMap<String, PeerCapabilities>,
}

impl State {
    fn new() -> Self {
        State {
            hello_sent: HashSet::new(),
            negotiated: HashMap::new(),
        }
    }
}

/// Capability handshake service — wire-equivalent of the C# `HandshakeService`.
/// Holds a [`MeshSender`] for outbound Hello / HelloAck packets and locks in
/// the negotiated `(version, caps)` per peer.
///
/// `Send + Sync` because the inner state is a [`Mutex`]; safe to share across
/// async tasks.
pub struct HandshakeService<S: MeshSender + 'static> {
    sender: S,
    our_min_version: u8,
    our_max_version: u8,
    our_capabilities: HashSet<String>,
    our_implementation: String,
    state: Mutex<State>,
}

impl<S: MeshSender + 'static> HandshakeService<S> {
    /// Construct a handshake service with the codebase defaults: speaks
    /// versions `1..=PROTOCOL_VERSION_SIGNED` and advertises
    /// [`default_capabilities`].
    pub fn new(sender: S) -> Self {
        Self::with_options(
            sender,
            1,
            PROTOCOL_VERSION_SIGNED,
            default_capabilities(),
            DEFAULT_IMPLEMENTATION.to_string(),
        )
    }

    /// Construct a handshake service with explicit version range, capability
    /// set, and implementation banner.
    ///
    /// Panics if `our_min_version > our_max_version`.
    pub fn with_options(
        sender: S,
        our_min_version: u8,
        our_max_version: u8,
        our_capabilities: HashSet<String>,
        our_implementation: String,
    ) -> Self {
        assert!(
            our_min_version <= our_max_version,
            "our_min_version ({}) cannot exceed our_max_version ({})",
            our_min_version,
            our_max_version
        );
        HandshakeService {
            sender,
            our_min_version,
            our_max_version,
            our_capabilities,
            our_implementation,
            state: Mutex::new(State::new()),
        }
    }

    /// Send a Hello to `peer_uhid` if we haven't already. Returns `true` iff
    /// the Hello was both built and (per [`MeshSender::send`]) delivered.
    ///
    /// Idempotent: the first call broadcasts, subsequent calls suppress
    /// duplicates. Use [`renegotiate`](Self::renegotiate) to reset.
    pub async fn initiate(&self, peer_uhid: &str) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }
        if peer_uhid == self.sender.local_uhid() {
            return false;
        }

        // Suppress duplicate Hellos. Note: we hold the mutex only across the
        // bookkeeping check, NOT across the await. We do release & re-acquire
        // around the I/O.
        {
            let mut state = self.state.lock().expect("handshake mutex poisoned");
            if !state.hello_sent.insert(peer_uhid.to_string()) {
                return false;
            }
        }

        let hello = self.build_packet(PacketType::Hello, peer_uhid);
        self.sender.send(&hello, peer_uhid).await
    }

    /// Handle a received Hello packet. Negotiates a `(version, caps)` pair
    /// against ours, sends a HelloAck back, and returns the resulting event.
    ///
    /// Returns `None` if the packet is malformed (wrong type, empty source,
    /// self-loop, undecodable payload).
    pub async fn handle_hello(&self, packet: &MeshPacket) -> Option<HandshakeEvent> {
        if packet.packet_type != PacketType::Hello {
            return None;
        }
        if packet.source_uhid.is_empty() {
            return None;
        }
        if packet.source_uhid == self.sender.local_uhid() {
            return None;
        }

        let theirs = Self::try_deserialize(packet)?;
        let event = self.try_negotiate(&packet.source_uhid, &theirs);

        if let HandshakeEvent::Negotiated(ref negotiated) = event {
            {
                let mut state = self.state.lock().expect("handshake mutex poisoned");
                state
                    .negotiated
                    .insert(packet.source_uhid.clone(), negotiated.clone());
            }
            // Reply with a HelloAck. The C# reference always replies, even if
            // we already sent them an unprompted Hello — the spec is symmetric
            // and the ack carries our own range/caps.
            let ack = self.build_packet(PacketType::HelloAck, &packet.source_uhid);
            let _delivered = self.sender.send(&ack, &packet.source_uhid).await;
        }

        Some(event)
    }

    /// Handle a received HelloAck packet. Locks in the negotiated peer
    /// capabilities. Returns `None` for malformed input, otherwise the event.
    pub fn handle_hello_ack(&self, packet: &MeshPacket) -> Option<HandshakeEvent> {
        if packet.packet_type != PacketType::HelloAck {
            return None;
        }
        if packet.source_uhid.is_empty() {
            return None;
        }
        if packet.source_uhid == self.sender.local_uhid() {
            return None;
        }

        let theirs = Self::try_deserialize(packet)?;
        let event = self.try_negotiate(&packet.source_uhid, &theirs);

        if let HandshakeEvent::Negotiated(ref negotiated) = event {
            let mut state = self.state.lock().expect("handshake mutex poisoned");
            state
                .negotiated
                .insert(packet.source_uhid.clone(), negotiated.clone());
        }

        Some(event)
    }

    /// Look up the cached negotiated capabilities for `peer_uhid`, or
    /// `None` if no handshake has completed yet.
    pub fn get_peer_capabilities(&self, peer_uhid: &str) -> Option<PeerCapabilities> {
        let state = self.state.lock().expect("handshake mutex poisoned");
        state.negotiated.get(peer_uhid).cloned()
    }

    /// Forget any cached negotiation with `peer_uhid` so the next contact
    /// triggers a fresh Hello. Idempotent.
    pub fn renegotiate(&self, peer_uhid: &str) {
        let mut state = self.state.lock().expect("handshake mutex poisoned");
        state.negotiated.remove(peer_uhid);
        state.hello_sent.remove(peer_uhid);
    }

    /// Snapshot of every peer we've finished a handshake with.
    pub fn get_all_negotiated(&self) -> Vec<PeerCapabilities> {
        let state = self.state.lock().expect("handshake mutex poisoned");
        state.negotiated.values().cloned().collect()
    }

    /// Backward-compat: install a "v1, no caps" record for a peer that
    /// never replied to our Hello within the timeout window. Hosts call this
    /// from their own timer / heartbeat loop. Idempotent — if the peer has
    /// since replied with a HelloAck, the existing record wins.
    pub fn assume_legacy_v1(&self, peer_uhid: &str) -> Option<PeerCapabilities> {
        if peer_uhid.is_empty() || peer_uhid == self.sender.local_uhid() {
            return None;
        }
        let mut state = self.state.lock().expect("handshake mutex poisoned");
        if let Some(existing) = state.negotiated.get(peer_uhid) {
            return Some(existing.clone());
        }
        let fallback = PeerCapabilities {
            peer_uhid: peer_uhid.to_string(),
            negotiated_version: 1,
            capabilities: HashSet::new(),
            implementation_version: String::new(),
            negotiated_at: SystemTime::now(),
        };
        state
            .negotiated
            .insert(peer_uhid.to_string(), fallback.clone());
        Some(fallback)
    }

    fn build_packet(&self, packet_type: PacketType, destination_uhid: &str) -> MeshPacket {
        let payload = HelloPayload::new(
            self.our_min_version,
            self.our_max_version,
            self.our_capabilities.iter().cloned().collect(),
            self.our_implementation.clone(),
        );
        let payload_bytes =
            serde_json::to_vec(&payload).expect("HelloPayload serialisation never fails");

        let mut packet = MeshPacket::new(packet_type, self.sender.local_uhid());
        packet.destination_uhid = destination_uhid.to_string();
        packet.ttl = 1; // direct hop only — handshake never relays
        packet.priority = 0;
        packet.protocol_version = self.our_max_version;
        packet.payload = payload_bytes;
        packet
    }

    fn try_deserialize(packet: &MeshPacket) -> Option<HelloPayload> {
        if packet.payload.is_empty() {
            return None;
        }
        serde_json::from_slice::<HelloPayload>(&packet.payload).ok()
    }

    fn try_negotiate(&self, peer_uhid: &str, theirs: &HelloPayload) -> HandshakeEvent {
        if theirs.min_version > theirs.max_version {
            return HandshakeEvent::Incompatible(IncompatiblePeer {
                peer_uhid: peer_uhid.to_string(),
                their_min_version: theirs.min_version,
                their_max_version: theirs.max_version,
                our_min_version: self.our_min_version,
                our_max_version: self.our_max_version,
                reason: IncompatibleReason::InvertedVersionRange,
            });
        }

        // Overlap check: highest min must be <= lowest max.
        let overlap_min = self.our_min_version.max(theirs.min_version);
        let overlap_max = self.our_max_version.min(theirs.max_version);
        if overlap_min > overlap_max {
            return HandshakeEvent::Incompatible(IncompatiblePeer {
                peer_uhid: peer_uhid.to_string(),
                their_min_version: theirs.min_version,
                their_max_version: theirs.max_version,
                our_min_version: self.our_min_version,
                our_max_version: self.our_max_version,
                reason: IncompatibleReason::NoVersionOverlap,
            });
        }

        let chosen_version = overlap_max;

        // Capability intersection — case-sensitive ordinal compare. Capability
        // names are wire constants, not human strings.
        let mut intersection: HashSet<String> = HashSet::new();
        for cap in &theirs.capabilities {
            if !cap.is_empty() && self.our_capabilities.contains(cap) {
                intersection.insert(cap.clone());
            }
        }

        HandshakeEvent::Negotiated(PeerCapabilities {
            peer_uhid: peer_uhid.to_string(),
            negotiated_version: chosen_version,
            capabilities: intersection,
            implementation_version: theirs.implementation.clone(),
            negotiated_at: SystemTime::now(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::PeerInfo;
    use crate::protocol::{MeshPacket, PacketType};
    use async_trait::async_trait;
    use std::sync::Mutex as StdMutex;

    /// Minimal MeshSender that records every send for assertion. Used by the
    /// handshake unit tests so we don't need the InProcessTransport machinery.
    struct CapturingSender {
        local: String,
        sent: StdMutex<Vec<(MeshPacket, String)>>,
    }

    impl CapturingSender {
        fn new(local: &str) -> Self {
            CapturingSender {
                local: local.to_string(),
                sent: StdMutex::new(Vec::new()),
            }
        }

        fn sent_packets(&self) -> Vec<(MeshPacket, String)> {
            self.sent.lock().unwrap().clone()
        }
    }

    #[async_trait]
    impl MeshSender for CapturingSender {
        fn local_uhid(&self) -> String {
            self.local.clone()
        }

        fn local_geohash(&self) -> Option<String> {
            None
        }

        fn connected_peers(&self) -> Vec<PeerInfo> {
            Vec::new()
        }

        async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
            self.sent
                .lock()
                .unwrap()
                .push((packet.clone(), next_hop_uhid.to_string()));
            true
        }

        async fn broadcast(&self, _packet: &MeshPacket) -> usize {
            0
        }
    }

    fn make_hello_packet(
        source: &str,
        dest: &str,
        min: u8,
        max: u8,
        caps: Vec<&str>,
        impl_banner: &str,
    ) -> MeshPacket {
        let payload = HelloPayload {
            min_version: min,
            max_version: max,
            capabilities: caps.iter().map(|s| s.to_string()).collect(),
            implementation: impl_banner.to_string(),
        };
        let mut p = MeshPacket::new(PacketType::Hello, source.to_string());
        p.destination_uhid = dest.to_string();
        p.ttl = 1;
        p.priority = 0;
        p.protocol_version = max;
        p.payload = serde_json::to_vec(&payload).unwrap();
        p
    }

    #[tokio::test]
    async fn test_initiate_sends_hello_with_snake_case_payload() {
        let sender = CapturingSender::new("alice");
        let svc = HandshakeService::new(sender);
        // We can't move sender — get a reference back via the service's
        // internal field. Instead we test the public effects via the events.
        // For this test we'll rebuild with a separate sender:
        let alice_sender = CapturingSender::new("alice");
        // Move the sender into the service — but then we lose access. The
        // pattern we use is: keep a parallel reference outside via Arc, OR
        // simply re-construct. Easiest: capture via the sent_packets API —
        // but the sender is moved. Rework: keep an Arc<CapturingSender>.
        // For simplicity, drop this test and use the explicit Arc pattern in
        // the next test which will verify the Hello payload is snake-case.
        drop(svc);
        drop(alice_sender);
    }

    #[tokio::test]
    async fn test_initiate_sends_hello_packet_to_peer() {
        use std::sync::Arc;

        // Wrap CapturingSender in an Arc so we can both pass it to the
        // HandshakeService and inspect what was sent.
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        let delivered = svc.initiate("bob").await;
        assert!(delivered, "send_async should be true for the wrapped Arc");

        let sent = cap.sent_packets();
        assert_eq!(sent.len(), 1, "exactly one Hello sent");
        let (packet, next_hop) = &sent[0];
        assert_eq!(packet.packet_type, PacketType::Hello);
        assert_eq!(packet.source_uhid, "alice");
        assert_eq!(packet.destination_uhid, "bob");
        assert_eq!(next_hop, "bob");
        assert_eq!(packet.ttl, 1, "Hello must be TTL=1, never relayed");

        // JSON shape check — the payload bytes must be snake_case JSON
        // matching the C# wire shape.
        let body: serde_json::Value = serde_json::from_slice(&packet.payload).unwrap();
        assert!(body.get("min_version").is_some(), "must use snake_case keys");
        assert!(body.get("max_version").is_some());
        assert!(body.get("capabilities").is_some());
        assert!(body.get("implementation").is_some());
        assert!(body.get("minVersion").is_none(), "must NOT use camelCase");
    }

    #[tokio::test]
    async fn test_initiate_suppresses_duplicate_hellos() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        let first = svc.initiate("bob").await;
        let second = svc.initiate("bob").await;
        assert!(first, "first call must Send");
        assert!(!second, "second call must be suppressed");
        assert_eq!(cap.sent_packets().len(), 1);
    }

    #[tokio::test]
    async fn test_handle_hello_picks_higher_version_and_intersects_caps() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        // Configure Alice: speaks 1..=2, caps {"a","b","c"}.
        let svc = HandshakeService::with_options(
            ArcSender(cap.clone()),
            1,
            2,
            ["a", "b", "c"].iter().map(|s| s.to_string()).collect(),
            "test/0.0.1".to_string(),
        );

        // Bob's Hello: speaks 1..=2, caps {"b","c","z"}.
        let hello = make_hello_packet(
            "bob",
            "alice",
            1,
            2,
            vec!["b", "c", "z"],
            "test/0.0.2",
        );

        let event = svc.handle_hello(&hello).await.expect("returns event");
        match event {
            HandshakeEvent::Negotiated(neg) => {
                assert_eq!(neg.peer_uhid, "bob");
                assert_eq!(neg.negotiated_version, 2);
                let mut caps: Vec<String> = neg.capabilities.into_iter().collect();
                caps.sort();
                assert_eq!(caps, vec!["b".to_string(), "c".to_string()]);
                assert_eq!(neg.implementation_version, "test/0.0.2");
            }
            HandshakeEvent::Incompatible(_) => panic!("should be compatible"),
        }

        // A HelloAck should have been sent in reply.
        let sent = cap.sent_packets();
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].0.packet_type, PacketType::HelloAck);
        assert_eq!(sent[0].1, "bob");

        // Cached lookup matches.
        let stored = svc.get_peer_capabilities("bob").expect("cached after handshake");
        assert_eq!(stored.negotiated_version, 2);
    }

    #[tokio::test]
    async fn test_handle_hello_no_overlap_emits_incompatible() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        // Alice speaks 3..=4. Bob (in this test) speaks 1..=2 — no overlap.
        let svc = HandshakeService::with_options(
            ArcSender(cap.clone()),
            3,
            4,
            HashSet::new(),
            "test/0.0.1".to_string(),
        );

        let hello = make_hello_packet("bob", "alice", 1, 2, vec!["x"], "test");
        let event = svc.handle_hello(&hello).await.expect("returns event");
        match event {
            HandshakeEvent::Incompatible(inc) => {
                assert_eq!(inc.peer_uhid, "bob");
                assert_eq!(inc.reason, IncompatibleReason::NoVersionOverlap);
            }
            HandshakeEvent::Negotiated(_) => panic!("should NOT be compatible"),
        }
        // No HelloAck should have been sent for incompatible peers.
        assert_eq!(cap.sent_packets().len(), 0);
        assert!(svc.get_peer_capabilities("bob").is_none());
    }

    #[tokio::test]
    async fn test_handle_hello_inverted_range_emits_incompatible() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        // Inverted: min=5, max=3.
        let hello = make_hello_packet("bob", "alice", 5, 3, vec![], "test");
        let event = svc.handle_hello(&hello).await.expect("returns event");
        match event {
            HandshakeEvent::Incompatible(inc) => {
                assert_eq!(inc.reason, IncompatibleReason::InvertedVersionRange);
            }
            HandshakeEvent::Negotiated(_) => panic!("inverted range must be incompatible"),
        }
    }

    #[tokio::test]
    async fn test_assume_legacy_v1_when_no_reply() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        let fallback = svc.assume_legacy_v1("bob").expect("returns fallback");
        assert_eq!(fallback.peer_uhid, "bob");
        assert_eq!(fallback.negotiated_version, 1);
        assert!(fallback.capabilities.is_empty());

        // Idempotent — second call returns the cached entry.
        let again = svc.assume_legacy_v1("bob").expect("returns cached");
        assert_eq!(again.negotiated_at, fallback.negotiated_at);
    }

    #[tokio::test]
    async fn test_renegotiate_clears_cache_and_allows_new_hello() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        assert!(svc.initiate("bob").await);
        assert!(!svc.initiate("bob").await, "duplicate suppressed");

        svc.renegotiate("bob");

        // After renegotiate, a fresh Hello may be sent.
        assert!(svc.initiate("bob").await);
        assert_eq!(cap.sent_packets().len(), 2);
    }

    #[tokio::test]
    async fn test_self_hello_is_ignored() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));
        // Self-Hello = should never enqueue a send, never produce an event.
        assert!(!svc.initiate("alice").await);

        let self_hello = make_hello_packet("alice", "alice", 1, 2, vec!["a"], "test");
        let event = svc.handle_hello(&self_hello).await;
        assert!(event.is_none(), "self-Hello must be ignored");
    }

    #[tokio::test]
    async fn test_malformed_payload_returns_none() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        // Hello packet with garbage payload bytes.
        let mut p = MeshPacket::new(PacketType::Hello, "bob".to_string());
        p.destination_uhid = "alice".to_string();
        p.payload = b"this is not json".to_vec();

        assert!(svc.handle_hello(&p).await.is_none());
    }

    #[tokio::test]
    async fn test_handle_hello_ack_does_not_send_back() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        // Build a HelloAck (not a Hello).
        let payload = HelloPayload::new(1, 2, vec!["a".to_string()], "test".to_string());
        let mut ack = MeshPacket::new(PacketType::HelloAck, "bob".to_string());
        ack.destination_uhid = "alice".to_string();
        ack.payload = serde_json::to_vec(&payload).unwrap();

        let event = svc.handle_hello_ack(&ack).expect("returns event");
        match event {
            HandshakeEvent::Negotiated(neg) => {
                assert_eq!(neg.peer_uhid, "bob");
                assert_eq!(neg.negotiated_version, 2);
            }
            HandshakeEvent::Incompatible(_) => panic!("should be compatible"),
        }

        // No outbound packet — receiver of HelloAck doesn't reply.
        assert_eq!(cap.sent_packets().len(), 0);
    }

    #[tokio::test]
    async fn test_handle_hello_with_wrong_packet_type_returns_none() {
        use std::sync::Arc;
        struct ArcSender(Arc<CapturingSender>);
        #[async_trait]
        impl MeshSender for ArcSender {
            fn local_uhid(&self) -> String {
                self.0.local.clone()
            }
            async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
                self.0.send(packet, next_hop_uhid).await
            }
            async fn broadcast(&self, packet: &MeshPacket) -> usize {
                self.0.broadcast(packet).await
            }
        }

        let cap = Arc::new(CapturingSender::new("alice"));
        let svc = HandshakeService::new(ArcSender(cap.clone()));

        // Pass a Data packet to handle_hello — must be ignored cleanly.
        let mut p = MeshPacket::new(PacketType::Data, "bob".to_string());
        p.payload = b"{}".to_vec();
        assert!(svc.handle_hello(&p).await.is_none());
    }
}
