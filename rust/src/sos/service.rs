// SPDX-License-Identifier: MIT

//! SOS broadcast origination and re-flooding.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::{MAX_SOS_BROADCASTS_PER_HOUR, SOS_PRIORITY, SOS_TTL};
use crate::extensibility::{BackendClient, IncentiveProvider, NoopBackendClient, NoopIncentiveProvider};
use crate::models::SosAlert;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const SOS_ACKNOWLEDGED_CHANNEL_CAPACITY: usize = 64;

#[derive(Debug, Clone, Serialize, Deserialize)]
struct SosWire {
    broadcast_id: Uuid,
    broadcast_type: String,
    message: Option<String>,
    latitude: f64,
    longitude: f64,
    geohash: Option<String>,
}

/// Wire payload for [`PacketType::SosAck`] packets. UTF-8 JSON, snake_case keys,
/// field order `broadcast_id` then `received_at_ms`, no whitespace. Every field is
/// integer- or string-typed (no floating point), so the encoding is byte-identical
/// across all eight language ports — see `fixtures/sos/vectors.json`.
///
/// An `SosAck` is sent by a node that has just received an
/// [`PacketType::SosBroadcast`], directed back toward the alert's originator, so the
/// person raising the emergency learns their broadcast actually reached at least one
/// device. The acknowledging node's identity is carried by the enclosing packet's
/// `source_uhid` — it is not duplicated in the body.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct SosAckPayload {
    /// Id of the [`SosAlert`] / SOS broadcast being acknowledged.
    broadcast_id: Uuid,
    /// Unix timestamp in milliseconds at which the acknowledging node received the SOS.
    received_at_ms: i64,
}

/// Event emitted on the ORIGINATING node when a peer acknowledges receipt of one of
/// its active SOS alerts — proof the emergency reached at least one device. Mirrors
/// the C# `SosAcknowledgement` and the Go / Python / TS / Kotlin / Swift ports.
#[derive(Debug, Clone)]
pub struct SosAcknowledgedEvent {
    /// Id of the SOS broadcast that was acknowledged.
    pub broadcast_id: Uuid,
    /// UHID of the peer that acknowledged receiving the SOS.
    pub responder_uhid: String,
    /// Total distinct peers that have acknowledged this SOS so far (this responder included).
    pub total_distinct_acks: usize,
}

/// SOS broadcast service. Originates and re-floods SOS broadcasts.
/// Dedups by packet ID; rate-limited to MAX_SOS_BROADCASTS_PER_HOUR per rolling hour.
pub struct SosBroadcastService {
    sender: Arc<dyn MeshSender>,
    backend: Arc<dyn BackendClient>,
    incentives: Arc<dyn IncentiveProvider>,

    state: Mutex<SosState>,

    /// Broadcast channel for SOS-acknowledged events. Subscribers on the
    /// originating node receive an event each time a distinct peer acknowledges
    /// one of our active SOS alerts.
    acknowledged_tx: broadcast::Sender<SosAcknowledgedEvent>,
}

struct SosState {
    recent_origins: VecDeque<u64>, // Unix-epoch seconds
    seen: HashSet<Uuid>,
    active_alerts: HashMap<Uuid, SosAlert>,
    /// Distinct responder UHIDs per broadcast id, populated on the ORIGINATING
    /// node only as `SosAck` packets arrive back. Kept separate from `SosAlert`
    /// so the alert's wire model stays byte-identical across ports.
    acknowledged_by: HashMap<Uuid, HashSet<String>>,
}

impl SosBroadcastService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        Self::with_dependencies(sender, Arc::new(NoopBackendClient), Arc::new(NoopIncentiveProvider))
    }

    pub fn with_dependencies(
        sender: Arc<dyn MeshSender>,
        backend: Arc<dyn BackendClient>,
        incentives: Arc<dyn IncentiveProvider>,
    ) -> Self {
        let (acknowledged_tx, _) = broadcast::channel(SOS_ACKNOWLEDGED_CHANNEL_CAPACITY);
        Self {
            sender,
            backend,
            incentives,
            state: Mutex::new(SosState {
                recent_origins: VecDeque::new(),
                seen: HashSet::new(),
                active_alerts: HashMap::new(),
                acknowledged_by: HashMap::new(),
            }),
            acknowledged_tx,
        }
    }

    /// Subscribe to SOS-acknowledged events. On the originating node, each
    /// subscriber receives an event the moment a distinct peer acknowledges one of
    /// our active SOS alerts. Best-effort / fire-and-forget: events are dropped
    /// when there are no live receivers.
    pub fn subscribe_acknowledged(&self) -> broadcast::Receiver<SosAcknowledgedEvent> {
        self.acknowledged_tx.subscribe()
    }

    /// Originate an SOS. Floods the mesh and (if a backend client is wired up)
    /// mirrors the alert via cloud. Returns false if the rolling rate limit is exhausted.
    pub async fn broadcast(
        &self,
        broadcast_type: &str,
        message: Option<String>,
        latitude: f64,
        longitude: f64,
        geohash: Option<String>,
    ) -> bool {
        if broadcast_type.is_empty() {
            return false;
        }

        let now = unix_secs();
        {
            let mut state = self.state.lock().unwrap();
            prune_old_origins(&mut state.recent_origins, now);
            if state.recent_origins.len() >= MAX_SOS_BROADCASTS_PER_HOUR {
                return false;
            }
            state.recent_origins.push_back(now);
        }

        let alert = SosAlert {
            id: Uuid::new_v4(),
            sender_uhid: self.sender.local_uhid(),
            broadcast_type: broadcast_type.to_string(),
            message: message.clone(),
            latitude,
            longitude,
            geohash: geohash.clone(),
            received_at: now,
        };
        {
            let mut state = self.state.lock().unwrap();
            state.active_alerts.insert(alert.id, alert.clone());
        }

        let body = serde_json::to_vec(&SosWire {
            broadcast_id: alert.id,
            broadcast_type: broadcast_type.to_string(),
            message,
            latitude,
            longitude,
            geohash,
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::SosBroadcast, self.sender.local_uhid());
        packet.destination_uhid = String::new();
        packet.ttl = SOS_TTL;
        packet.priority = SOS_PRIORITY;
        packet.payload = body;
        {
            let mut state = self.state.lock().unwrap();
            state.seen.insert(packet.id);
        }

        self.sender.broadcast(&packet).await;
        self.backend.sync_sos(&alert).await;
        true
    }

    /// Mark an SOS resolved locally and stop forwarding it.
    pub fn resolve(&self, broadcast_id: &Uuid) {
        let mut state = self.state.lock().unwrap();
        state.active_alerts.remove(broadcast_id);
        state.acknowledged_by.remove(broadcast_id);
    }

    pub fn get_active_alerts(&self) -> Vec<SosAlert> {
        let state = self.state.lock().unwrap();
        state.active_alerts.values().cloned().collect()
    }

    /// Distinct responder UHIDs that have acknowledged the given SOS broadcast on
    /// this (originating) node. Empty if the broadcast is unknown or unacknowledged.
    pub fn acknowledged_by(&self, broadcast_id: &Uuid) -> Vec<String> {
        let state = self.state.lock().unwrap();
        state
            .acknowledged_by
            .get(broadcast_id)
            .map(|s| s.iter().cloned().collect())
            .unwrap_or_default()
    }

    /// Pump an inbound SOS packet. Dedups, surfaces the alert, and re-broadcasts.
    pub async fn handle(&self, packet: &mut MeshPacket) {
        if packet.packet_type != PacketType::SosBroadcast {
            return;
        }
        {
            let mut state = self.state.lock().unwrap();
            if !state.seen.insert(packet.id) {
                return;
            }
        }

        let body: SosWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return,
        };
        if packet.source_uhid == self.sender.local_uhid() {
            return;
        }

        let alert = SosAlert {
            id: body.broadcast_id,
            sender_uhid: packet.source_uhid.clone(),
            broadcast_type: body.broadcast_type,
            message: body.message,
            latitude: body.latitude,
            longitude: body.longitude,
            geohash: body.geohash,
            received_at: unix_secs(),
        };
        let broadcast_id = alert.id;
        {
            let mut state = self.state.lock().unwrap();
            state.active_alerts.insert(alert.id, alert);
        }

        // Acknowledge back to the originator so the sender learns their SOS reached a device.
        self.send_ack(broadcast_id, &packet.source_uhid).await;

        if packet.ttl > 1 {
            packet.ttl -= 1;
            self.sender.broadcast(packet).await;
            self.incentives
                .record_relay(&self.sender.local_uhid(), packet)
                .await;
        }
    }

    /// Pump an inbound [`PacketType::SosAck`] packet into the service. On the
    /// originating node it records the responder against the matching active alert
    /// (deduping by responder UHID) and emits a [`SosAcknowledgedEvent`]. No-op if
    /// the ack references an SOS this node did not originate (or already resolved),
    /// or if the responder is this node itself. Returns `Err` if the packet is not
    /// an `SosAck`.
    pub async fn handle_ack(&self, packet: &MeshPacket) -> Result<(), String> {
        if packet.packet_type != PacketType::SosAck {
            return Err(format!("Expected SosAck, got {:?}", packet.packet_type));
        }

        let body: SosAckPayload = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return Ok(()),
        };

        let responder = packet.source_uhid.clone();
        if responder.is_empty() {
            return Ok(());
        }
        // Our own ack echoed back — ignore.
        if responder == self.sender.local_uhid() {
            return Ok(());
        }

        let total = {
            let mut state = self.state.lock().unwrap();
            // Only the ORIGINATOR holds this alert; every other node ignores the ack.
            if !state.active_alerts.contains_key(&body.broadcast_id) {
                return Ok(());
            }
            let responders = state.acknowledged_by.entry(body.broadcast_id).or_default();
            if !responders.insert(responder.clone()) {
                return Ok(()); // already counted this responder — dedup
            }
            responders.len()
        };

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.acknowledged_tx.send(SosAcknowledgedEvent {
            broadcast_id: body.broadcast_id,
            responder_uhid: responder,
            total_distinct_acks: total,
        });
        Ok(())
    }

    /// Send a directed [`PacketType::SosAck`] back to the alert originator so the
    /// sender learns their emergency reached this device. Best-effort: delivers
    /// when the originator is reachable as a next hop.
    async fn send_ack(&self, broadcast_id: Uuid, originator_uhid: &str) {
        if originator_uhid.is_empty() {
            return;
        }
        if originator_uhid == self.sender.local_uhid() {
            return;
        }

        let body = serde_json::to_vec(&SosAckPayload {
            broadcast_id,
            received_at_ms: unix_millis(),
        })
        .unwrap_or_default();

        let mut ack = MeshPacket::new(PacketType::SosAck, self.sender.local_uhid());
        ack.destination_uhid = originator_uhid.to_string();
        ack.ttl = SOS_TTL;
        ack.priority = SOS_PRIORITY;
        ack.payload = body;

        self.sender.send(&ack, originator_uhid).await;
    }
}

fn prune_old_origins(origins: &mut VecDeque<u64>, now: u64) {
    let cutoff = now.saturating_sub(3600);
    while let Some(&front) = origins.front() {
        if front < cutoff {
            origins.pop_front();
        } else {
            break;
        }
    }
}

fn unix_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: `SosAckPayload` must serialize to exactly these bytes in
    // every language (fixtures/sos/vectors.json). snake_case, field order
    // broadcast_id then received_at_ms, no whitespace, UUID lowercase-dashed,
    // received_at_ms a bare integer. Mirrors the C# `SosAckPayload_SerializesToCanonicalBytes`.
    #[test]
    fn sos_ack_payload_serializes_to_canonical_bytes() {
        let cases = [
            (
                "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
                1_700_000_000_000i64,
                "{\"broadcast_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"received_at_ms\":1700000000000}",
            ),
            (
                "00000000-0000-0000-0000-000000000000",
                0i64,
                "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\",\"received_at_ms\":0}",
            ),
        ];

        for (guid, ms, expected) in cases {
            let payload = SosAckPayload {
                broadcast_id: Uuid::parse_str(guid).unwrap(),
                received_at_ms: ms,
            };
            let bytes = serde_json::to_vec(&payload).unwrap();
            let json = String::from_utf8(bytes).unwrap();
            assert_eq!(json, expected, "byte-identity mismatch for guid={guid} ms={ms}");
        }
    }
}
