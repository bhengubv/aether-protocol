// SPDX-License-Identifier: MIT

//! SOS broadcast origination and re-flooding.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::constants::{MAX_SOS_BROADCASTS_PER_HOUR, SOS_PRIORITY, SOS_TTL};
use crate::extensibility::{BackendClient, IncentiveProvider, NoopBackendClient, NoopIncentiveProvider};
use crate::models::SosAlert;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

#[derive(Debug, Clone, Serialize, Deserialize)]
struct SosWire {
    broadcast_id: Uuid,
    broadcast_type: String,
    message: Option<String>,
    latitude: f64,
    longitude: f64,
    geohash: Option<String>,
}

/// SOS broadcast service. Originates and re-floods SOS broadcasts.
/// Dedups by packet ID; rate-limited to MAX_SOS_BROADCASTS_PER_HOUR per rolling hour.
pub struct SosBroadcastService {
    sender: Arc<dyn MeshSender>,
    backend: Arc<dyn BackendClient>,
    incentives: Arc<dyn IncentiveProvider>,

    state: Mutex<SosState>,
}

struct SosState {
    recent_origins: VecDeque<u64>, // Unix-epoch seconds
    seen: HashSet<Uuid>,
    active_alerts: HashMap<Uuid, SosAlert>,
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
        Self {
            sender,
            backend,
            incentives,
            state: Mutex::new(SosState {
                recent_origins: VecDeque::new(),
                seen: HashSet::new(),
                active_alerts: HashMap::new(),
            }),
        }
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
    }

    pub fn get_active_alerts(&self) -> Vec<SosAlert> {
        let state = self.state.lock().unwrap();
        state.active_alerts.values().cloned().collect()
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
        {
            let mut state = self.state.lock().unwrap();
            state.active_alerts.insert(alert.id, alert);
        }

        if packet.ttl > 1 {
            packet.ttl -= 1;
            self.sender.broadcast(packet).await;
            self.incentives
                .record_relay(&self.sender.local_uhid(), packet)
                .await;
        }
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
