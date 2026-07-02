// SPDX-License-Identifier: MIT

//! ABMF WIRE bindings over [`PacketType::BandwidthProbe`] (53),
//! [`PacketType::BandwidthAck`] (54) and [`PacketType::BandwidthGossip`] (55).
//!
//! Binds the three ABMF packet types to the mesh: send probes (directed) + their
//! acks (directed reply), and broadcast / receive warm-start gossip. Inbound
//! packets surface via tokio broadcast events; the host feeds them into the
//! bandwidth estimator (`record_probe_result` / `warm_from_gossip`) and replies
//! to probes.
//!
//! Mirrors the C# `BandwidthWireService` / `BandwidthWireCodec`. All multi-byte
//! integers are LITTLE-ENDIAN and there is NO version byte — the layouts are the
//! ones documented on the `PacketType` members and pinned by the byte-identity
//! gate `fixtures/bandwidth/vectors.json`:
//!
//! ```text
//! Probe(53)  : sequence u32 | sender_send_us i64                                                     (12 B)
//! Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
//! Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                          (13 B)
//! ```
//!
//! `sender_receive_us` is NOT on the wire — the prober fills it locally on
//! receipt (0 on deserialize). `peer_uhid` / `transport_name` / `measured_at` of
//! a gossip come from the enclosing packet + local clock, not the wire body.

use std::sync::Arc;
use std::time::SystemTime;
use tokio::sync::broadcast;

use crate::bandwidth::{BandwidthConfidence, BandwidthGossipPayload, BandwidthProbeAck};
use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const PROBE_CHANNEL_CAPACITY: usize = 64;
const ACK_CHANNEL_CAPACITY: usize = 64;
const GOSSIP_CHANNEL_CAPACITY: usize = 64;

const PROBE_LEN: usize = 12;
const ACK_LEN: usize = 32;
const GOSSIP_LEN: usize = 13;

// ── Wire request model ──────────────────────────────────────────────────────

/// A latency / throughput probe request (`PacketType::BandwidthProbe` = 53 body).
///
/// Wire layout (little-endian, 12 B): `sequence` u32 | `sender_send_us` i64.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BandwidthProbe {
    pub sequence: u32,
    /// Microseconds since Unix epoch on the sender's local clock when the probe
    /// was emitted.
    pub sender_send_us: i64,
}

// ── Confidence <-> byte mapping ─────────────────────────────────────────────
//
// `BandwidthConfidence` is not `#[repr(u8)]`, so we map explicitly. The byte
// values MUST match the C# enum: None=0, Low=1, Medium=2, High=3.

fn confidence_to_byte(c: BandwidthConfidence) -> u8 {
    match c {
        BandwidthConfidence::None => 0,
        BandwidthConfidence::Low => 1,
        BandwidthConfidence::Medium => 2,
        BandwidthConfidence::High => 3,
    }
}

fn confidence_from_byte(b: u8) -> BandwidthConfidence {
    match b {
        1 => BandwidthConfidence::Low,
        2 => BandwidthConfidence::Medium,
        3 => BandwidthConfidence::High,
        _ => BandwidthConfidence::None,
    }
}

// ── Little-endian read helpers ──────────────────────────────────────────────

fn read_u32_le(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

fn read_i32_le(b: &[u8], off: usize) -> i32 {
    i32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

fn read_i64_le(b: &[u8], off: usize) -> i64 {
    i64::from_le_bytes([
        b[off],
        b[off + 1],
        b[off + 2],
        b[off + 3],
        b[off + 4],
        b[off + 5],
        b[off + 6],
        b[off + 7],
    ])
}

// ── Codec ───────────────────────────────────────────────────────────────────

/// Binary wire codec for the three ABMF packets. All multi-byte integers are
/// little-endian; no version byte. Byte-identity gate:
/// `fixtures/bandwidth/vectors.json`. Mirrors the C# `BandwidthWireCodec`.
pub struct BandwidthWireCodec;

impl BandwidthWireCodec {
    /// Serialize a [`BandwidthProbe`] to its 12-byte wire form.
    pub fn serialize_probe(p: &BandwidthProbe) -> Vec<u8> {
        let mut buf = Vec::with_capacity(PROBE_LEN);
        buf.extend_from_slice(&p.sequence.to_le_bytes());
        buf.extend_from_slice(&p.sender_send_us.to_le_bytes());
        buf
    }

    /// Deserialize a [`BandwidthProbe`] from a wire body. Returns `None` when the
    /// body is shorter than 12 bytes.
    pub fn deserialize_probe(b: &[u8]) -> Option<BandwidthProbe> {
        if b.len() < PROBE_LEN {
            return None;
        }
        Some(BandwidthProbe {
            sequence: read_u32_le(b, 0),
            sender_send_us: read_i64_le(b, 4),
        })
    }

    /// Serialize a [`BandwidthProbeAck`] to its 32-byte wire form. `sender_receive_us`
    /// is local-only and is NOT written.
    pub fn serialize_ack(a: &BandwidthProbeAck) -> Vec<u8> {
        let mut buf = Vec::with_capacity(ACK_LEN);
        buf.extend_from_slice(&a.sequence.to_le_bytes());
        buf.extend_from_slice(&a.sender_send_us.to_le_bytes());
        buf.extend_from_slice(&a.receiver_receive_us.to_le_bytes());
        buf.extend_from_slice(&a.receiver_send_us.to_le_bytes());
        buf.extend_from_slice(&a.probe_bytes.to_le_bytes());
        buf
    }

    /// Deserialize a [`BandwidthProbeAck`] from a wire body. `sender_receive_us`
    /// is not carried on the wire and is set to 0 (the prober fills it on
    /// receipt). Returns `None` when the body is shorter than 32 bytes.
    pub fn deserialize_ack(b: &[u8]) -> Option<BandwidthProbeAck> {
        if b.len() < ACK_LEN {
            return None;
        }
        Some(BandwidthProbeAck {
            sequence: read_u32_le(b, 0),
            sender_send_us: read_i64_le(b, 4),
            receiver_receive_us: read_i64_le(b, 12),
            receiver_send_us: read_i64_le(b, 20),
            sender_receive_us: 0, // not on wire — filled by the prober on receipt
            probe_bytes: read_i32_le(b, 28),
        })
    }

    /// Serialize a [`BandwidthGossipPayload`] to its 13-byte wire form.
    /// `peer_uhid` / `transport_name` / `measured_at` are not on the wire.
    /// `rt_prop_us` is clamped to `[0, i32::MAX]` to fit the wire's i32 field
    /// (mirrors the C# `Math.Clamp`).
    pub fn serialize_gossip(g: &BandwidthGossipPayload) -> Vec<u8> {
        let mut buf = Vec::with_capacity(GOSSIP_LEN);
        let rtprop = g.rt_prop_us.clamp(0, i32::MAX as i64) as i32;
        buf.extend_from_slice(&g.btl_bw_bps.to_le_bytes());
        buf.extend_from_slice(&rtprop.to_le_bytes());
        buf.push(confidence_to_byte(g.confidence));
        buf
    }

    /// Deserialize a [`BandwidthGossipPayload`] from a wire body. `peer_uhid` /
    /// `transport_name` default to empty and `measured_at` to
    /// [`SystemTime::UNIX_EPOCH`]; the service fills `peer_uhid` from the packet.
    /// Returns `None` when the body is shorter than 13 bytes.
    pub fn deserialize_gossip(b: &[u8]) -> Option<BandwidthGossipPayload> {
        if b.len() < GOSSIP_LEN {
            return None;
        }
        Some(BandwidthGossipPayload {
            peer_uhid: String::new(),
            transport_name: String::new(),
            btl_bw_bps: read_i64_le(b, 0),
            rt_prop_us: read_i32_le(b, 8) as i64,
            confidence: confidence_from_byte(b[12]),
            measured_at: SystemTime::UNIX_EPOCH,
        })
    }
}

// ── Events ──────────────────────────────────────────────────────────────────

/// Event emitted when an inbound [`PacketType::BandwidthProbe`] is accepted: the
/// probe plus the peer that sent it (so the host can reply with an ack). Mirrors
/// the C# `BandwidthProbeReceived`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BandwidthProbeReceivedEvent {
    pub probe: BandwidthProbe,
    pub from_uhid: String,
}

// ── Service ─────────────────────────────────────────────────────────────────

/// Binds the three ABMF PacketTypes to the mesh. Sends probes (directed) + their
/// acks (directed reply), and broadcasts / receives warm-start gossip. Inbound
/// packets surface via tokio broadcast events. Mirrors the C#
/// `BandwidthWireService`.
pub struct BandwidthWireService {
    sender: Arc<dyn MeshSender>,
    probe_tx: broadcast::Sender<BandwidthProbeReceivedEvent>,
    ack_tx: broadcast::Sender<BandwidthProbeAck>,
    gossip_tx: broadcast::Sender<BandwidthGossipPayload>,
}

impl BandwidthWireService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (probe_tx, _) = broadcast::channel(PROBE_CHANNEL_CAPACITY);
        let (ack_tx, _) = broadcast::channel(ACK_CHANNEL_CAPACITY);
        let (gossip_tx, _) = broadcast::channel(GOSSIP_CHANNEL_CAPACITY);
        Self {
            sender,
            probe_tx,
            ack_tx,
            gossip_tx,
        }
    }

    /// Subscribe to inbound-probe events. Each subscriber receives an event the
    /// moment an inbound [`PacketType::BandwidthProbe`] is accepted.
    pub fn subscribe_probe(&self) -> broadcast::Receiver<BandwidthProbeReceivedEvent> {
        self.probe_tx.subscribe()
    }

    /// Subscribe to inbound-ack events.
    pub fn subscribe_ack(&self) -> broadcast::Receiver<BandwidthProbeAck> {
        self.ack_tx.subscribe()
    }

    /// Subscribe to inbound-gossip events. The event's `peer_uhid` is filled from
    /// the enclosing packet's source.
    pub fn subscribe_gossip(&self) -> broadcast::Receiver<BandwidthGossipPayload> {
        self.gossip_tx.subscribe()
    }

    /// Send a directed [`PacketType::BandwidthProbe`] to a peer. Returns the
    /// sender's delivery result. A blank `peer_uhid` short-circuits to `false`.
    pub async fn send_probe(&self, peer_uhid: &str, probe: &BandwidthProbe) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }
        self.send_directed(
            peer_uhid,
            PacketType::BandwidthProbe,
            BandwidthWireCodec::serialize_probe(probe),
        )
        .await
    }

    /// Send a directed [`PacketType::BandwidthAck`] reply to the prober. Returns
    /// the sender's delivery result. A blank `peer_uhid` short-circuits to
    /// `false`.
    pub async fn send_ack(&self, peer_uhid: &str, ack: &BandwidthProbeAck) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }
        self.send_directed(
            peer_uhid,
            PacketType::BandwidthAck,
            BandwidthWireCodec::serialize_ack(ack),
        )
        .await
    }

    async fn send_directed(&self, peer_uhid: &str, ptype: PacketType, payload: Vec<u8>) -> bool {
        let mut packet = MeshPacket::new(ptype, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = payload;
        self.sender.send(&packet, peer_uhid).await
    }

    /// Broadcast a [`PacketType::BandwidthGossip`] warm-start estimate
    /// (`destination_uhid` `*`, TTL [`DEFAULT_TTL`]). Returns the number of peers
    /// reached.
    pub async fn broadcast_gossip(&self, gossip: &BandwidthGossipPayload) -> usize {
        let mut packet = MeshPacket::new(PacketType::BandwidthGossip, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = BandwidthWireCodec::serialize_gossip(gossip);
        self.sender.broadcast(&packet).await
    }

    /// Dispatch an inbound bandwidth packet to the matching event. Returns
    /// `false` on the wrong packet type or a malformed (too-short) body; `true`
    /// once the packet has been surfaced. Gossip events have `peer_uhid` filled
    /// from the packet source.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        match packet.packet_type {
            PacketType::BandwidthProbe => {
                let probe = match BandwidthWireCodec::deserialize_probe(&packet.payload) {
                    Some(p) => p,
                    None => return false,
                };
                let _ = self.probe_tx.send(BandwidthProbeReceivedEvent {
                    probe,
                    from_uhid: packet.source_uhid.clone(),
                });
                true
            }
            PacketType::BandwidthAck => {
                let ack = match BandwidthWireCodec::deserialize_ack(&packet.payload) {
                    Some(a) => a,
                    None => return false,
                };
                let _ = self.ack_tx.send(ack);
                true
            }
            PacketType::BandwidthGossip => {
                let mut gossip = match BandwidthWireCodec::deserialize_gossip(&packet.payload) {
                    Some(g) => g,
                    None => return false,
                };
                gossip.peer_uhid = packet.source_uhid.clone();
                let _ = self.gossip_tx.send(gossip);
                true
            }
            _ => false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Manual lowercase-hex encoder — `hex` is not a crate dependency, and the
    /// byte-identity gate only needs a stable string form.
    fn to_hex(b: &[u8]) -> String {
        const HEX: &[u8; 16] = b"0123456789abcdef";
        let mut s = String::with_capacity(b.len() * 2);
        for &byte in b {
            s.push(HEX[(byte >> 4) as usize] as char);
            s.push(HEX[(byte & 0x0f) as usize] as char);
        }
        s
    }

    // ── Byte-identity gates (fixtures/bandwidth/vectors.json) ────────────────

    #[test]
    fn probe_serializes_to_canonical_bytes() {
        let bytes = BandwidthWireCodec::serialize_probe(&BandwidthProbe {
            sequence: 42,
            sender_send_us: 1_700_000_000_000_000,
        });
        assert_eq!(to_hex(&bytes), "2a00000000401e18240a0600");
        assert_eq!(bytes.len(), PROBE_LEN);
    }

    #[test]
    fn ack_serializes_to_canonical_bytes() {
        // sender_receive_us (999) is local-only and must NOT change the wire bytes.
        let ack = BandwidthProbeAck {
            sequence: 42,
            sender_send_us: 1_700_000_000_000_000,
            receiver_receive_us: 1_700_000_000_012_345,
            receiver_send_us: 1_700_000_000_013_000,
            sender_receive_us: 999,
            probe_bytes: 1200,
        };
        assert_eq!(
            to_hex(&BandwidthWireCodec::serialize_ack(&ack)),
            "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000"
        );
    }

    #[test]
    fn gossip_serializes_to_canonical_bytes() {
        // peer_uhid / transport_name / measured_at are not on the wire.
        let g = BandwidthGossipPayload {
            peer_uhid: "peer".to_string(),
            transport_name: "tp".to_string(),
            btl_bw_bps: 5_000_000,
            rt_prop_us: 25_000,
            confidence: BandwidthConfidence::Medium,
            measured_at: SystemTime::UNIX_EPOCH,
        };
        assert_eq!(
            to_hex(&BandwidthWireCodec::serialize_gossip(&g)),
            "404b4c0000000000a861000002"
        );
    }

    #[test]
    fn ack_round_trips_sender_receive_us_zeroed() {
        let back = BandwidthWireCodec::deserialize_ack(&BandwidthWireCodec::serialize_ack(
            &BandwidthProbeAck {
                sequence: 7,
                sender_send_us: 100,
                receiver_receive_us: 200,
                receiver_send_us: 300,
                sender_receive_us: 400,
                probe_bytes: 512,
            },
        ))
        .expect("32-byte ack must deserialize");
        assert_eq!(back.sequence, 7);
        assert_eq!(back.sender_send_us, 100);
        assert_eq!(back.receiver_receive_us, 200);
        assert_eq!(back.receiver_send_us, 300);
        assert_eq!(back.sender_receive_us, 0); // not on wire
        assert_eq!(back.probe_bytes, 512);
    }

    #[test]
    fn probe_round_trips() {
        let probe = BandwidthProbe {
            sequence: 9,
            sender_send_us: 123,
        };
        let back = BandwidthWireCodec::deserialize_probe(&BandwidthWireCodec::serialize_probe(
            &probe,
        ))
        .expect("12-byte probe must deserialize");
        assert_eq!(back, probe);
    }

    #[test]
    fn confidence_byte_mapping_matches_csharp() {
        assert_eq!(confidence_to_byte(BandwidthConfidence::None), 0);
        assert_eq!(confidence_to_byte(BandwidthConfidence::Low), 1);
        assert_eq!(confidence_to_byte(BandwidthConfidence::Medium), 2);
        assert_eq!(confidence_to_byte(BandwidthConfidence::High), 3);
        assert_eq!(confidence_from_byte(0), BandwidthConfidence::None);
        assert_eq!(confidence_from_byte(1), BandwidthConfidence::Low);
        assert_eq!(confidence_from_byte(2), BandwidthConfidence::Medium);
        assert_eq!(confidence_from_byte(3), BandwidthConfidence::High);
    }

    #[test]
    fn deserialize_rejects_short_bodies() {
        assert!(BandwidthWireCodec::deserialize_probe(&[0u8; 11]).is_none());
        assert!(BandwidthWireCodec::deserialize_ack(&[0u8; 31]).is_none());
        assert!(BandwidthWireCodec::deserialize_gossip(&[0u8; 12]).is_none());
    }
}
