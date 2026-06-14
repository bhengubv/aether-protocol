// SPDX-License-Identifier: MIT
//
// File-level helpers over [`ReedSolomonCodec`]: split a plaintext blob into K systematic data shards
// (zero-padded), produce the full N-shard set, and reconstruct the original blob from any K surviving
// shards. Byte-identical to the C# / Go vault data layout: shardSize = ceil(size/K), data shard i is
// plaintext[i*shardSize .. (i+1)*shardSize] zero-padded, and recovery concatenates the K recovered
// data shards in index order then trims to the original size.

use std::collections::BTreeMap;

use crate::vault::{ReedSolomonCodec, VaultError};

/// Slices `data` into K equal zero-padded data shards of length `shardSize = ceil(len(data)/K)`. This
/// is the systematic prefix the encoder leaves unchanged.
pub fn split_into_data_shards(data: &[u8], k: usize) -> Result<Vec<Vec<u8>>, VaultError> {
    if k < 1 {
        return Err(VaultError::InvalidParameters("K must be >= 1".into()));
    }
    if data.is_empty() {
        return Err(VaultError::InvalidParameters("data must not be empty".into()));
    }
    let shard_size = data.len().div_ceil(k);
    let mut shards: Vec<Vec<u8>> = Vec::with_capacity(k);
    for i in 0..k {
        let mut shard = vec![0u8; shard_size];
        let offset = i * shard_size;
        if offset < data.len() {
            let length = shard_size.min(data.len() - offset);
            shard[..length].copy_from_slice(&data[offset..offset + length]);
        }
        shards.push(shard);
    }
    Ok(shards)
}

impl ReedSolomonCodec {
    /// Splits `data` into K systematic data shards and returns the full set of N = K+M shards.
    pub fn encode_data(&self, data: &[u8]) -> Result<Vec<Vec<u8>>, VaultError> {
        let data_shards = split_into_data_shards(data, self.data_shards())?;
        self.encode(&data_shards)
    }

    /// Reconstructs the original blob of `original_size` bytes from any K surviving shards.
    /// `available` maps a shard index (0..N-1) to its bytes. Returns an error if fewer than K shards
    /// are supplied.
    pub fn reconstruct_data(
        &self,
        available: &BTreeMap<usize, Vec<u8>>,
        original_size: usize,
    ) -> Result<Vec<u8>, VaultError> {
        let data_shards = self.decode_data_shards(available)?;

        let shard_size = data_shards[0].len();
        let mut out = vec![0u8; self.data_shards() * shard_size];
        for (j, shard) in data_shards.iter().enumerate() {
            out[j * shard_size..j * shard_size + shard_size].copy_from_slice(shard);
        }
        if original_size > out.len() {
            return Err(VaultError::InvalidParameters(
                "original_size exceeds reconstructed length".into(),
            ));
        }
        out.truncate(original_size);
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn split_zero_pads_final_shard() {
        // 5 bytes into K=2 -> shardSize=3, shard0 = [1,2,3], shard1 = [4,5,0].
        let shards = split_into_data_shards(&[1, 2, 3, 4, 5], 2).unwrap();
        assert_eq!(shards, vec![vec![1, 2, 3], vec![4, 5, 0]]);
    }

    #[test]
    fn encode_then_reconstruct_round_trips() {
        let codec = ReedSolomonCodec::new(10, 4).unwrap();
        let data: Vec<u8> = (0..=200u8).cycle().take(2222).collect();
        let shards = codec.encode_data(&data).unwrap();
        assert_eq!(shards.len(), 14);

        // Recover from all data shards (fast path).
        let mut available = BTreeMap::new();
        for (idx, item) in shards.iter().enumerate().take(10) {
            available.insert(idx, item.clone());
        }
        let recovered = codec.reconstruct_data(&available, data.len()).unwrap();
        assert_eq!(recovered, data);
    }
}
