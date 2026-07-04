// SPDX-License-Identifier: MIT
//
// Acceptance for Gap 1 (Rust): a transport-backed WebRTC signalling carrier.
//
// The Rust twin of the C# `RelaySignalingTests`
// (tests/AetherNet.Transport.WebRtc.Tests/RelaySignalingTests.cs). It proves the
// PRODUCTION signalling path — `RelayWebRtcSignaling` framing each SDP/ICE signal
// as `AWS1`+JSON and carrying it over a real `TransportService` seam — using two
// SEPARATE carrier instances (two nodes) wired only through an in-process
// transport pair. No central signalling server; the transport pair stands in for
// the AetherNet QUIC/HTTP relay exactly as C#'s `LoopbackTransport` does.
//
// Gated behind the `webrtc` feature (as the module is). Build/run with:
//   cargo test --features webrtc --test webrtc_relay_signaling_tests
//
// ── Scope note (honest) ─────────────────────────────────────────────────────
// The load-bearing acceptance here is that two carriers round-trip an OFFER and
// an ANSWER over the transport — that is what proves the carrier. The full
// real-ICE WebRTC handshake needs UDP host candidates and has historically only
// been verified on a Linux box; it is NOT forced here. See the `#[ignore]`d
// `full_real_ice_handshake_over_two_carriers` at the bottom, which documents how
// to run it in an environment that permits UDP.

#![cfg(feature = "webrtc")]

use std::sync::{Arc, Mutex};

use aethernet_protocol::transport::webrtc::{Signal, SignalType, Signaling};
use aethernet_protocol::transport::webrtc_relay_signaling::RelayWebRtcSignaling;
use aethernet_protocol::transport::{PerTransportMetrics, TransportService};

// ── In-process transport pair (the "relay" stand-in) ────────────────────────
//
// Minimal `TransportService` that delivers everything it sends to its paired
// instance's shared data handler — ordered, reliable, in-process. Mirrors the
// C# test's `LoopbackTransport`. Unlike the crate's `InProcessTransport` (which
// only buffers), this one actually pumps bytes to the peer's handler, which is
// what the carrier needs to receive.
type DataHandler = Arc<dyn Fn(&str, &[u8]) + Send + Sync>;

struct LoopbackTransport {
    local_uhid: String,
    peer: Mutex<Option<Arc<LoopbackTransport>>>,
    on_data: Arc<Mutex<Option<DataHandler>>>,
}

impl LoopbackTransport {
    fn new(local_uhid: &str) -> Arc<Self> {
        Arc::new(LoopbackTransport {
            local_uhid: local_uhid.to_string(),
            peer: Mutex::new(None),
            on_data: Arc::new(Mutex::new(None)),
        })
    }

    /// Wires two endpoints together (call once).
    fn wire(a: &Arc<LoopbackTransport>, b: &Arc<LoopbackTransport>) {
        *a.peer.lock().unwrap() = Some(Arc::clone(b));
        *b.peer.lock().unwrap() = Some(Arc::clone(a));
    }

    fn deliver(&self, from_uhid: &str, data: &[u8]) {
        let handler = self.on_data.lock().unwrap().clone();
        if let Some(h) = handler {
            h(from_uhid, data);
        }
    }
}

#[async_trait::async_trait]
impl TransportService for LoopbackTransport {
    fn name(&self) -> &str {
        "Loopback"
    }
    fn is_available(&self) -> bool {
        true
    }
    fn max_bandwidth_bps(&self) -> i64 {
        i64::MAX
    }
    fn max_range_meters(&self) -> i32 {
        0
    }
    fn power_cost_relative(&self) -> i32 {
        100
    }
    fn max_concurrent_peers(&self) -> i32 {
        2
    }

    async fn send_async(
        &self,
        _peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        let peer = self.peer.lock().unwrap().clone();
        match peer {
            Some(p) => {
                p.deliver(&self.local_uhid, data); // ordered, reliable delivery to the far end
                Ok(true)
            }
            None => Ok(false),
        }
    }

    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>> {
        let mut data = Vec::new();
        stream.read_to_end(&mut data)?;
        self.send_async(peer_uhid, &data).await
    }

    fn is_connected(&self, _peer_uhid: &str) -> bool {
        self.peer.lock().unwrap().is_some()
    }

    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    ) {
        *self.on_data.lock().unwrap() = Some(Arc::from(handler));
    }

    fn set_shared_data_handler(&self, handler: DataHandler) -> bool {
        *self.on_data.lock().unwrap() = Some(handler);
        true
    }

    fn metrics(&self) -> Option<Arc<PerTransportMetrics>> {
        None
    }
}

// ── Acceptance: offer + answer round-trip over two carriers ─────────────────

/// The core proof of the carrier: two SEPARATE `RelayWebRtcSignaling` instances,
/// one per node, exchange an OFFER and an ANSWER through an in-process transport
/// pair. Each signal is framed `AWS1`+JSON by the sending carrier, carried as
/// opaque bytes by the transport, and decoded back to a `Signal` by the
/// receiving carrier — end to end, across the transport, with no shared object
/// state between the two carriers other than the wire.
#[tokio::test]
async fn two_carriers_round_trip_offer_and_answer_over_transport() {
    // Two "relay" endpoints wired to each other — the only thing the nodes share.
    let alice_relay = LoopbackTransport::new("alice");
    let bob_relay = LoopbackTransport::new("bob");
    LoopbackTransport::wire(&alice_relay, &bob_relay);

    let alice_sig: Arc<dyn Signaling> =
        RelayWebRtcSignaling::new(alice_relay.clone()).expect("alice carrier");
    let bob_sig: Arc<dyn Signaling> =
        RelayWebRtcSignaling::new(bob_relay.clone()).expect("bob carrier");

    // Bob records the signals his carrier surfaces.
    let bob_inbox: Arc<Mutex<Vec<Signal>>> = Arc::new(Mutex::new(Vec::new()));
    {
        let sink = bob_inbox.clone();
        bob_sig.on_signal(Box::new(move |s: Signal| sink.lock().unwrap().push(s)));
    }
    // Alice records too (to receive Bob's answer).
    let alice_inbox: Arc<Mutex<Vec<Signal>>> = Arc::new(Mutex::new(Vec::new()));
    {
        let sink = alice_inbox.clone();
        alice_sig.on_signal(Box::new(move |s: Signal| sink.lock().unwrap().push(s)));
    }

    // Alice → Bob: an SDP offer, framed and carried over the transport.
    let offer = Signal {
        from_uhid: "alice".into(),
        to_uhid: "bob".into(),
        signal_type: SignalType::Offer,
        sdp: Some("v=0\r\no=- 42 2 IN IP4 127.0.0.1\r\ns=-".into()),
        candidate: None,
        sdp_mid: None,
        sdp_mline_index: 0,
    };
    assert!(
        alice_sig.send_signal("bob", offer.clone()).await,
        "offer should be handed to the transport"
    );

    // Bob's carrier decoded exactly one offer, faithfully.
    {
        let got = bob_inbox.lock().unwrap();
        assert_eq!(got.len(), 1, "bob should have received exactly one signal");
        let g = &got[0];
        assert_eq!(g.signal_type, SignalType::Offer);
        assert_eq!(g.from_uhid, "alice");
        assert_eq!(g.to_uhid, "bob");
        assert_eq!(g.sdp.as_deref(), offer.sdp.as_deref());
    }

    // Bob → Alice: the SDP answer, back over the same transport.
    let answer = Signal {
        from_uhid: "bob".into(),
        to_uhid: "alice".into(),
        signal_type: SignalType::Answer,
        sdp: Some("v=0\r\no=- 99 3 IN IP4 127.0.0.1\r\ns=-".into()),
        candidate: None,
        sdp_mid: None,
        sdp_mline_index: 0,
    };
    assert!(
        bob_sig.send_signal("alice", answer.clone()).await,
        "answer should be handed to the transport"
    );

    // Alice's carrier decoded exactly one answer, faithfully.
    {
        let got = alice_inbox.lock().unwrap();
        assert_eq!(got.len(), 1, "alice should have received exactly one signal");
        let g = &got[0];
        assert_eq!(g.signal_type, SignalType::Answer);
        assert_eq!(g.from_uhid, "bob");
        assert_eq!(g.to_uhid, "alice");
        assert_eq!(g.sdp.as_deref(), answer.sdp.as_deref());
    }
}

/// A trickled ICE candidate also survives the round-trip with its mid /
/// m-line-index intact — the other signal kind the handshake needs.
#[tokio::test]
async fn ice_candidate_round_trips_over_transport() {
    let a = LoopbackTransport::new("a");
    let b = LoopbackTransport::new("b");
    LoopbackTransport::wire(&a, &b);

    let a_sig: Arc<dyn Signaling> = RelayWebRtcSignaling::new(a.clone()).expect("a");
    let b_sig: Arc<dyn Signaling> = RelayWebRtcSignaling::new(b.clone()).expect("b");

    let inbox: Arc<Mutex<Vec<Signal>>> = Arc::new(Mutex::new(Vec::new()));
    {
        let sink = inbox.clone();
        b_sig.on_signal(Box::new(move |s: Signal| sink.lock().unwrap().push(s)));
    }

    let cand = Signal {
        from_uhid: "a".into(),
        to_uhid: "b".into(),
        signal_type: SignalType::IceCandidate,
        sdp: None,
        candidate: Some("candidate:1 1 udp 2130706431 192.168.1.5 54321 typ host".into()),
        sdp_mid: Some("0".into()),
        sdp_mline_index: 0,
    };
    assert!(a_sig.send_signal("b", cand.clone()).await);

    let got = inbox.lock().unwrap();
    assert_eq!(got.len(), 1);
    let g = &got[0];
    assert_eq!(g.signal_type, SignalType::IceCandidate);
    assert_eq!(g.candidate.as_deref(), cand.candidate.as_deref());
    assert_eq!(g.sdp_mid.as_deref(), Some("0"));
    assert_eq!(g.sdp_mline_index, 0);
}

/// Ordinary application bytes (no `AWS1` prefix) that share the carrying
/// transport must never surface as a signal — mirrors the C#
/// `NonSignallingBytes_AreIgnored`.
#[tokio::test]
async fn non_signalling_bytes_on_the_transport_are_ignored() {
    let a = LoopbackTransport::new("a");
    let b = LoopbackTransport::new("b");
    LoopbackTransport::wire(&a, &b);

    let _a_sig: Arc<dyn Signaling> = RelayWebRtcSignaling::new(a.clone()).expect("a");
    let b_sig: Arc<dyn Signaling> = RelayWebRtcSignaling::new(b.clone()).expect("b");

    let raised = Arc::new(Mutex::new(false));
    {
        let flag = raised.clone();
        b_sig.on_signal(Box::new(move |_s: Signal| *flag.lock().unwrap() = true));
    }

    // Drive plain bytes into `b` by sending from `a`'s transport directly.
    assert!(a
        .send_async("b", b"ordinary app data, not a signal")
        .await
        .unwrap());
    assert!(
        !*raised.lock().unwrap(),
        "non-AWS1 app bytes must not be decoded as signalling"
    );
}

// ── Full real-ICE handshake (env-gated) ─────────────────────────────────────

/// The end-to-end proof that the carrier drives a REAL WebRTC negotiation
/// between two separate nodes: two `WebRtcTransport`s, each fed a separate
/// `RelayWebRtcSignaling` over the transport pair, negotiate a direct data
/// channel and carry a payload peer-to-peer — the handshake having ridden the
/// relay carrier.
///
/// This is `#[ignore]`d by default so it never gates the standard suite on an
/// environment where UDP host-candidate gathering is restricted (sandboxed CI,
/// locked-down containers). It is NOT, however, inherently Windows-blocked: it
/// was run explicitly on this Windows dev box and PASSED (a direct host-only
/// data channel negotiated in well under a second). Run it yourself with:
///
///   cargo test --features webrtc --test webrtc_relay_signaling_tests -- \
///       --ignored full_real_ice_handshake_over_two_carriers
///
/// The deterministic offer/answer/ICE round-trip tests above are what prove the
/// carrier on every platform without any UDP dependency; this one proves the
/// carrier + real ICE together wherever the environment permits UDP.
#[tokio::test]
#[ignore = "real WebRTC ICE needs UDP host candidates; ignored so it can't gate CI where UDP is restricted — run explicitly with --ignored (passes on a UDP-capable host, incl. this Windows box)"]
async fn full_real_ice_handshake_over_two_carriers() {
    use aethernet_protocol::transport::webrtc::WebRtcTransport;
    use std::time::Duration;
    use tokio::sync::mpsc;

    let alice_relay = LoopbackTransport::new("alice");
    let bob_relay = LoopbackTransport::new("bob");
    LoopbackTransport::wire(&alice_relay, &bob_relay);

    let alice_sig: Arc<dyn Signaling> =
        RelayWebRtcSignaling::new(alice_relay.clone()).expect("alice carrier");
    let bob_sig: Arc<dyn Signaling> =
        RelayWebRtcSignaling::new(bob_relay.clone()).expect("bob carrier");

    // Host-candidate-only ICE (explicit empty list) — no STUN/TURN dependency.
    let alice = WebRtcTransport::new("alice", alice_sig, Some(Vec::new()))
        .await
        .expect("new alice");
    let bob = WebRtcTransport::new("bob", bob_sig, Some(Vec::new()))
        .await
        .expect("new bob");

    let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
    bob.on_data_received(Arc::new(move |from: &str, data: &[u8]| {
        if from == "alice" {
            let _ = tx.send(data.to_vec());
        }
    }));

    let payload = b"handshake rode the relay carrier; the data went direct".to_vec();
    assert!(
        alice.send_async("bob", &payload).await.expect("send result"),
        "negotiation over the relay carrier should succeed"
    );

    let received = tokio::time::timeout(Duration::from_secs(30), rx.recv())
        .await
        .expect("timed out waiting for bytes over the data channel")
        .expect("sender dropped before delivering bytes");
    assert_eq!(received, payload);
    assert!(alice.is_connected("bob"));
    assert!(bob.is_connected("alice"));
}
