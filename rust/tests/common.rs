// SPDX-License-Identifier: MIT

//! Shared test fakes for routing/dtn/sos integration tests.
//!
//! Compiled as part of each top-level integration test (#[path = "common.rs"]
//! mod common;) so the FakeMeshSender mirrors the C# / Go / Python FakeMeshSender.

#![allow(dead_code)]

use async_trait::async_trait;
use std::sync::{Arc, Mutex};

use aethernet_protocol::{
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
    routing::sender::MeshSender,
};

#[derive(Clone)]
pub struct UnicastRecord {
    pub packet: MeshPacket,
    pub next_hop_uhid: String,
}

pub struct FakeMeshSender {
    local_uhid: String,
    local_geohash: Option<String>,
    inner: Mutex<FakeInner>,
}

struct FakeInner {
    peers: Vec<PeerInfo>,
    fail_peers: std::collections::HashSet<String>,
    pub unicasts: Vec<UnicastRecord>,
    pub broadcasts: Vec<MeshPacket>,
}

impl FakeMeshSender {
    pub fn new(local_uhid: impl Into<String>) -> Arc<Self> {
        Arc::new(Self {
            local_uhid: local_uhid.into(),
            local_geohash: None,
            inner: Mutex::new(FakeInner {
                peers: Vec::new(),
                fail_peers: std::collections::HashSet::new(),
                unicasts: Vec::new(),
                broadcasts: Vec::new(),
            }),
        })
    }

    pub fn set_geohash(self: &Arc<Self>, geohash: impl Into<String>) {
        // SAFETY: only ever called pre-test setup, on the test thread.
        let ptr = Arc::as_ptr(self) as *mut Self;
        unsafe {
            (*ptr).local_geohash = Some(geohash.into());
        }
    }

    pub fn add_peer(&self, peer: PeerInfo) {
        self.inner.lock().unwrap().peers.push(peer);
    }

    pub fn fail_sends_to(&self, uhid: impl Into<String>) {
        self.inner.lock().unwrap().fail_peers.insert(uhid.into());
    }

    pub fn unicasts(&self) -> Vec<UnicastRecord> {
        self.inner.lock().unwrap().unicasts.clone()
    }

    pub fn broadcasts(&self) -> Vec<MeshPacket> {
        self.inner.lock().unwrap().broadcasts.clone()
    }

    pub fn clear(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.unicasts.clear();
        inner.broadcasts.clear();
    }
}

#[async_trait]
impl MeshSender for FakeMeshSender {
    fn local_uhid(&self) -> String {
        self.local_uhid.clone()
    }

    fn local_geohash(&self) -> Option<String> {
        self.local_geohash.clone()
    }

    fn connected_peers(&self) -> Vec<PeerInfo> {
        self.inner.lock().unwrap().peers.clone()
    }

    async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
        let mut inner = self.inner.lock().unwrap();
        if inner.fail_peers.contains(next_hop_uhid) {
            return false;
        }
        inner.unicasts.push(UnicastRecord {
            packet: packet.clone(),
            next_hop_uhid: next_hop_uhid.to_string(),
        });
        true
    }

    async fn broadcast(&self, packet: &MeshPacket) -> usize {
        let mut inner = self.inner.lock().unwrap();
        inner.broadcasts.push(packet.clone());
        inner.peers.len()
    }
}

pub fn new_rreq(source: &str, dest: &str, ttl: i32) -> MeshPacket {
    let mut p = MeshPacket::new(PacketType::RouteRequest, source.to_string());
    p.destination_uhid = dest.to_string();
    p.ttl = ttl;
    p
}

pub fn new_rrep(source: &str, dest: &str, ttl: i32) -> MeshPacket {
    let mut p = MeshPacket::new(PacketType::RouteReply, source.to_string());
    p.destination_uhid = dest.to_string();
    p.ttl = ttl;
    p
}
