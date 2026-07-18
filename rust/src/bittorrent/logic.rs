// SPDX-License-Identifier: MIT
//! Rarest-first picker + SHA-1-verified piece store.

use sha1::{Digest, Sha1};
use std::collections::HashMap;

pub struct RarestFirstPicker {
    count: usize,
    have: Vec<bool>,
    inflight: Vec<bool>,
    avail: Vec<usize>,
    peer_has: HashMap<String, Vec<bool>>,
}

impl RarestFirstPicker {
    pub fn new(count: usize) -> Self {
        Self { count, have: vec![false; count], inflight: vec![false; count], avail: vec![0; count], peer_has: HashMap::new() }
    }
    pub fn set_have(&mut self, i: usize) {
        if i < self.count {
            self.have[i] = true;
            self.inflight[i] = false;
        }
    }
    pub fn add_peer(&mut self, peer: &str) {
        self.peer_has.entry(peer.to_string()).or_insert_with(|| vec![false; self.count]);
    }
    pub fn peer_has_piece(&mut self, peer: &str, i: usize) {
        self.add_peer(peer);
        if i < self.count {
            let h = self.peer_has.get_mut(peer).unwrap();
            if !h[i] {
                h[i] = true;
                self.avail[i] += 1;
            }
        }
    }
    pub fn pick_for(&mut self, peer: &str) -> i64 {
        let has = match self.peer_has.get(peer) {
            Some(h) => h.clone(),
            None => return -1,
        };
        let mut best: i64 = -1;
        let mut best_avail = 0usize;
        for i in 0..self.count {
            if self.have[i] || self.inflight[i] || !has[i] {
                continue;
            }
            if best == -1 || self.avail[i] < best_avail {
                best = i as i64;
                best_avail = self.avail[i];
            }
        }
        if best >= 0 {
            self.inflight[best as usize] = true;
        }
        best
    }
    pub fn release(&mut self, i: usize) {
        if i < self.count {
            self.inflight[i] = false;
        }
    }
    pub fn is_complete(&self) -> bool {
        self.count > 0 && self.have.iter().all(|&h| h)
    }
}

pub struct PieceStore {
    piece_length: usize,
    total_length: usize,
    pub piece_hashes: Vec<Vec<u8>>,
    pieces: HashMap<usize, Vec<u8>>,
}

impl PieceStore {
    pub fn new(piece_length: usize, total_length: usize, piece_hashes: Vec<Vec<u8>>) -> Self {
        Self { piece_length, total_length, piece_hashes, pieces: HashMap::new() }
    }
    pub fn piece_count(&self) -> usize {
        self.piece_hashes.len()
    }
    pub fn length_of_piece(&self, i: usize) -> usize {
        if i >= self.piece_hashes.len() {
            return 0;
        }
        if i == self.piece_hashes.len() - 1 {
            self.total_length - i * self.piece_length
        } else {
            self.piece_length
        }
    }
    pub fn has(&self, i: usize) -> bool {
        self.pieces.contains_key(&i)
    }
    pub fn try_complete(&mut self, i: usize, data: &[u8]) -> bool {
        if i >= self.piece_hashes.len() || data.len() != self.length_of_piece(i) {
            return false;
        }
        let mut h = Sha1::new();
        h.update(data);
        if h.finalize().as_slice() != self.piece_hashes[i].as_slice() {
            return false;
        }
        self.pieces.insert(i, data.to_vec());
        true
    }
    pub fn is_complete(&self) -> bool {
        self.pieces.len() == self.piece_hashes.len()
    }
    pub fn assemble(&self) -> Option<Vec<u8>> {
        if !self.is_complete() {
            return None;
        }
        let mut out = Vec::with_capacity(self.total_length);
        for i in 0..self.piece_hashes.len() {
            out.extend_from_slice(&self.pieces[&i]);
        }
        Some(out)
    }
}

pub fn piece_store_from_content(data: &[u8], piece_length: usize) -> PieceStore {
    let pc = data.len().div_ceil(piece_length);
    let mut hashes = Vec::new();
    let mut store = PieceStore::new(piece_length, data.len(), Vec::new());
    for i in 0..pc {
        let start = i * piece_length;
        let end = (start + piece_length).min(data.len());
        let mut h = Sha1::new();
        h.update(&data[start..end]);
        hashes.push(h.finalize().to_vec());
        store.pieces.insert(i, data[start..end].to_vec());
    }
    store.piece_hashes = hashes;
    store
}
