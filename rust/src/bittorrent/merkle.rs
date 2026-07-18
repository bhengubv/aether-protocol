// SPDX-License-Identifier: MIT
//! BitTorrent v2 (BEP-52) SHA-256 merkle hashing + v2 info-hash.

use sha2::{Digest, Sha256};

pub const MERKLE_BLOCK_SIZE: usize = 16384;

pub fn merkle_root(data: &[u8]) -> Vec<u8> {
    merkle_root_block(data, MERKLE_BLOCK_SIZE)
}

pub fn merkle_root_block(data: &[u8], block_size: usize) -> Vec<u8> {
    let mut leaves: Vec<Vec<u8>> = Vec::new();
    let mut i = 0;
    while i < data.len() {
        let end = (i + block_size).min(data.len());
        let mut h = Sha256::new();
        h.update(&data[i..end]);
        leaves.push(h.finalize().to_vec());
        i += block_size;
    }
    if leaves.is_empty() {
        return vec![0u8; 32];
    }
    root_of(leaves)
}

fn root_of(mut level: Vec<Vec<u8>>) -> Vec<u8> {
    let mut width = 1;
    while width < level.len() {
        width <<= 1;
    }
    while level.len() < width {
        level.push(vec![0u8; 32]);
    }
    while level.len() > 1 {
        let mut next = Vec::with_capacity(level.len() / 2);
        for pair in level.chunks(2) {
            let mut h = Sha256::new();
            h.update(&pair[0]);
            h.update(&pair[1]);
            next.push(h.finalize().to_vec());
        }
        level = next;
    }
    level.into_iter().next().unwrap()
}

pub fn v2_info_hash(info_dict_bytes: &[u8]) -> Vec<u8> {
    let mut h = Sha256::new();
    h.update(info_dict_bytes);
    h.finalize().to_vec()
}

pub fn v2_info_hash_truncated(info_dict_bytes: &[u8]) -> Vec<u8> {
    v2_info_hash(info_dict_bytes)[..20].to_vec()
}
