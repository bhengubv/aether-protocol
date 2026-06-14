// SPDX-License-Identifier: MIT
//
// Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault — the production
// erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
// reconstruct it"). Rust port of `AetherNet.Vault.ReedSolomonCodec` (and the Go `vault.ReedSolomonCodec`),
// byte-identical to the C# reference and every other language implementation.
//
// FIELD: arithmetic is over GF(2^8) with primitive polynomial x^8+x^4+x^3+x^2+1 (0x11D, the
// AES/Rijndael polynomial), alpha = 2 — the SAME field as [`crate::transport::rlnc`] (RlncCodec).
// Identical field => identical parity bytes, which is what makes a parity shard scattered by one node
// decodable by any other node on the mesh.
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0..K-1) is exactly
//   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short, shardSize = ceil(size/K)
// The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
// original.
//
// MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ^ y_j) over GF(256) with two
// disjoint sets of distinct field elements (y_0..y_{K-1} = 0..K-1, x_0..x_{M-1} = K..K+M-1). Every
// square submatrix of a Cauchy matrix is invertible, so stacked on the systematic K x K identity it
// yields a true MDS code: any K of the N generator rows are linearly independent => any K surviving
// shards reconstruct the original; K-1 or fewer is unrecoverable.

use std::collections::BTreeMap;

use crate::vault::VaultError;

// ── GF(2^8) arithmetic — primitive polynomial 0x11D, alpha = 2 ────────────────
// Byte-for-byte identical field to `transport::rlnc`'s GF256 (that module's tables are private, so
// the table-generation logic is mirrored here rather than referenced across the module boundary —
// identical tables are what guarantee identical parity bytes). Built with a `const fn` so the tables
// live in the binary (no heap, no lazy init), matching the existing RLNC idiom.

const fn build_gf256_tables() -> ([u8; 512], [u8; 256]) {
    let mut exp = [0u8; 512];
    let mut log = [0u8; 256];
    let mut x: usize = 1;
    let mut i: usize = 0;
    while i < 255 {
        exp[i] = x as u8;
        log[x] = i as u8;
        x <<= 1;
        if x & 0x100 != 0 {
            x ^= 0x11D; // reduce mod p(x) = x^8+x^4+x^3+x^2+1
        }
        x &= 0xFF;
        i += 1;
    }
    i = 255;
    while i < 512 {
        exp[i] = exp[i - 255];
        i += 1;
    }
    log[1] = 0; // log_alpha(1) = 0
    (exp, log)
}

const _TABLES: ([u8; 512], [u8; 256]) = build_gf256_tables();
const GF256_EXP: [u8; 512] = _TABLES.0;
const GF256_LOG: [u8; 256] = _TABLES.1;

#[inline(always)]
fn gf_mul(a: u8, b: u8) -> u8 {
    if a == 0 || b == 0 {
        return 0;
    }
    GF256_EXP[GF256_LOG[a as usize] as usize + GF256_LOG[b as usize] as usize]
}

#[inline(always)]
fn gf_inv(a: u8) -> u8 {
    debug_assert!(a != 0, "vault: GF256 inverse of zero");
    GF256_EXP[255 - GF256_LOG[a as usize] as usize]
}

/// A systematic Reed-Solomon (K data + M parity) erasure codec over GF(2^8). The K data shards are
/// the plaintext partitioned into equal zero-padded slices (byte-identical to the vault data layout);
/// the M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any K of the K+M
/// shards reconstruct the original.
pub struct ReedSolomonCodec {
    k: usize,
    m: usize,
    n: usize,
    /// Parity generator rows: `parity[i]` (i = 0..M-1) is the K-byte Cauchy coefficient vector for
    /// parity shard K+i. Together with the implicit K x K systematic identity for the data shards
    /// these form the full N x K MDS generator matrix.
    parity: Vec<Vec<u8>>,
}

impl ReedSolomonCodec {
    /// Creates a codec with `k` data shards and `m` parity shards. `k` must be >= 1, `m` must be
    /// >= 0, and `k + m` must be <= 256.
    pub fn new(k: usize, m: usize) -> Result<Self, VaultError> {
        if k < 1 {
            return Err(VaultError::InvalidParameters("K must be >= 1".into()));
        }
        if k + m > 256 {
            return Err(VaultError::InvalidParameters("K + M must be <= 256".into()));
        }
        Ok(Self {
            k,
            m,
            n: k + m,
            parity: build_cauchy_parity_matrix(k, m),
        })
    }

    /// Total shard count (K + M).
    pub fn shard_count(&self) -> usize {
        self.n
    }

    /// Number of data shards (K).
    pub fn data_shards(&self) -> usize {
        self.k
    }

    /// Number of parity shards (M).
    pub fn parity_shards(&self) -> usize {
        self.m
    }

    /// Encodes `data_shards` (K byte vectors of equal length `shardSize`) into the full set of N
    /// shards. Shards 0..K-1 are the data shards unchanged (systematic); shards K..N-1 are the M
    /// Reed-Solomon parity shards. The returned shards are fresh copies — callers keep ownership of
    /// the input.
    pub fn encode(&self, data_shards: &[Vec<u8>]) -> Result<Vec<Vec<u8>>, VaultError> {
        if data_shards.len() != self.k {
            return Err(VaultError::InvalidParameters(format!(
                "expected {} data shards, got {}",
                self.k,
                data_shards.len()
            )));
        }

        let shard_size = data_shards[0].len();
        for shard in data_shards {
            if shard.len() != shard_size {
                return Err(VaultError::InvalidParameters(
                    "all data shards must be the same length".into(),
                ));
            }
        }

        let mut shards: Vec<Vec<u8>> = Vec::with_capacity(self.n);

        // Systematic: the first K shards ARE the data shards (defensive copy — callers keep
        // ownership).
        for shard in data_shards {
            shards.push(shard.clone());
        }

        // Parity: shard K+i = sum_j parity[i][j] * dataShards[j] over GF(256).
        for i in 0..self.m {
            let mut parity_shard = vec![0u8; shard_size];
            let coeffs = &self.parity[i];
            for (j, shard) in data_shards.iter().enumerate() {
                let c = coeffs[j];
                if c == 0 {
                    continue;
                }
                for b in 0..shard_size {
                    parity_shard[b] ^= gf_mul(c, shard[b]);
                }
            }
            shards.push(parity_shard);
        }

        Ok(shards)
    }

    /// Reconstructs the K data shards from any K available shards. `available` maps a shard index
    /// (0..N-1) to its bytes; exactly K distinct entries are required, all of equal length. Returns
    /// the K data shards (indices 0..K-1, in order). Returns [`VaultError::Unrecoverable`] if fewer
    /// than K shards are supplied (K-1 or fewer is unrecoverable).
    pub fn decode_data_shards(
        &self,
        available: &BTreeMap<usize, Vec<u8>>,
    ) -> Result<Vec<Vec<u8>>, VaultError> {
        // Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
        // A BTreeMap iterates in ascending key order, so this is already sorted.
        let picked: Vec<(usize, &Vec<u8>)> = available
            .iter()
            .filter(|(&idx, _)| idx < self.n)
            .map(|(&idx, val)| (idx, val))
            .take(self.k)
            .collect();

        if picked.len() < self.k {
            return Err(VaultError::Unrecoverable(format!(
                "only {}/{} shards available",
                picked.len(),
                self.k
            )));
        }

        let shard_size = picked[0].1.len();
        for (_, val) in &picked {
            if val.len() != shard_size {
                return Err(VaultError::InvalidParameters(
                    "all supplied shards must be the same length".into(),
                ));
            }
        }

        // Fast path: if all K data shards (0..K-1) are present, no inversion is needed — the data is
        // the systematic prefix verbatim. This is the common, byte-identical-to-canonical recovery
        // case.
        if picked.iter().all(|(idx, _)| *idx < self.k) {
            let mut direct = vec![Vec::new(); self.k];
            for (idx, val) in &picked {
                direct[*idx] = (*val).clone();
            }
            return Ok(direct);
        }

        // General path: build the K x K generator submatrix A for the picked shard indices, invert
        // it, and apply A^-1 to the picked symbol-vectors to recover the K source (data) symbols.
        let mut a: Vec<Vec<u8>> = Vec::with_capacity(self.k);
        let mut rhs: Vec<Vec<u8>> = Vec::with_capacity(self.k);
        for (idx, val) in &picked {
            a.push(self.generator_row(*idx));
            rhs.push((*val).clone());
        }

        let inv = self.invert_matrix(&a)?;

        let mut data: Vec<Vec<u8>> = Vec::with_capacity(self.k);
        for inv_row in inv.iter().take(self.k) {
            let mut symbol = vec![0u8; shard_size];
            for (col, rhs_col) in rhs.iter().enumerate().take(self.k) {
                let coeff = inv_row[col];
                if coeff == 0 {
                    continue;
                }
                for b in 0..shard_size {
                    symbol[b] ^= gf_mul(coeff, rhs_col[b]);
                }
            }
            data.push(symbol);
        }

        Ok(data)
    }

    // ── generator matrix ─────────────────────────────────────────────────────

    /// The K-byte generator row for shard `index` (identity for a data shard, Cauchy coefficients
    /// for a parity shard).
    fn generator_row(&self, index: usize) -> Vec<u8> {
        if index < self.k {
            // Systematic data row = standard basis vector e_index.
            let mut row = vec![0u8; self.k];
            row[index] = 1;
            row
        } else {
            // Parity row (copy — the caller mutates rows during inversion).
            self.parity[index - self.k].clone()
        }
    }

    // ── GF(256) matrix inversion (Gauss-Jordan) ──────────────────────────────

    /// Inverts a K x K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack
    /// guarantees the picked submatrix is non-singular.
    fn invert_matrix(&self, m: &[Vec<u8>]) -> Result<Vec<Vec<u8>>, VaultError> {
        let n = self.k;
        // Augment [m | I].
        let mut aug: Vec<Vec<u8>> = Vec::with_capacity(n);
        for (r, row) in m.iter().enumerate().take(n) {
            let mut augmented = vec![0u8; 2 * n];
            augmented[..n].copy_from_slice(&row[..n]);
            augmented[n + r] = 1;
            aug.push(augmented);
        }

        for col in 0..n {
            // Find a pivot row at or below `col` with a non-zero entry in this column.
            let mut pivot: Option<usize> = None;
            for (r, aug_row) in aug.iter().enumerate().take(n).skip(col) {
                if aug_row[col] != 0 {
                    pivot = Some(r);
                    break;
                }
            }
            let pivot = pivot.ok_or_else(|| {
                VaultError::Unrecoverable("singular matrix — shard set is not decodable".into())
            })?;

            if pivot != col {
                aug.swap(col, pivot);
            }

            // Normalise the pivot row so the pivot element becomes 1.
            let inv = gf_inv(aug[col][col]);
            for c in 0..2 * n {
                aug[col][c] = gf_mul(aug[col][c], inv);
            }

            // Eliminate this column from every other row.
            for r in 0..n {
                if r == col {
                    continue;
                }
                let factor = aug[r][col];
                if factor == 0 {
                    continue;
                }
                for c in 0..2 * n {
                    let term = gf_mul(factor, aug[col][c]);
                    aug[r][c] ^= term;
                }
            }
        }

        // Right half is the inverse.
        let mut result: Vec<Vec<u8>> = Vec::with_capacity(n);
        for row in aug.iter().take(n) {
            result.push(row[n..2 * n].to_vec());
        }
        Ok(result)
    }
}

/// Builds the M x K Cauchy parity matrix over GF(256): `C[i][j] = 1 / (x_i ^ y_j)` with disjoint
/// distinct element sets `y_j = j` (0..K-1) and `x_i = K + i` (K..K+M-1). Cauchy => every square
/// submatrix invertible => MDS when stacked on the systematic identity.
fn build_cauchy_parity_matrix(k: usize, m: usize) -> Vec<Vec<u8>> {
    let mut matrix: Vec<Vec<u8>> = Vec::with_capacity(m);
    for i in 0..m {
        let mut row = vec![0u8; k];
        let xi = (k + i) as u8;
        for (j, slot) in row.iter_mut().enumerate().take(k) {
            let yj = j as u8;
            // x_i and y_j are drawn from disjoint ranges, so x_i ^ y_j is never 0 -> always
            // invertible.
            *slot = gf_inv(xi ^ yj);
        }
        matrix.push(row);
    }
    matrix
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gf256_field_matches_canonical_polynomial() {
        // 0x80 * 0x02 = 0x100 -> reduce mod 0x11D -> 0x1D
        assert_eq!(0x1D, gf_mul(0x80, 0x02));
        // a * inv(a) == 1 for every non-zero element.
        for a in 1u8..=255 {
            assert_eq!(1, gf_mul(a, gf_inv(a)), "a={a}");
        }
    }

    #[test]
    fn k_minus_one_shards_fail() {
        // Round-trip sanity on a small blob: K-1 survivors must be unrecoverable.
        let codec = ReedSolomonCodec::new(4, 2).unwrap();
        let data_shards: Vec<Vec<u8>> = (0..4).map(|i| vec![(i as u8 + 1) * 0x11; 8]).collect();
        let shards = codec.encode(&data_shards).unwrap();
        assert_eq!(shards.len(), 6);

        // Only 3 survivors (K-1) -> error.
        let mut available = BTreeMap::new();
        for idx in [1usize, 3, 5] {
            available.insert(idx, shards[idx].clone());
        }
        assert!(matches!(
            codec.decode_data_shards(&available),
            Err(VaultError::Unrecoverable(_))
        ));
    }

    #[test]
    fn parity_assisted_recovery_round_trips() {
        let codec = ReedSolomonCodec::new(4, 2).unwrap();
        let data_shards: Vec<Vec<u8>> = (0..4).map(|i| vec![(i as u8) ^ 0xA5; 16]).collect();
        let shards = codec.encode(&data_shards).unwrap();

        // Drop data shards 0 and 1; survive on data 2,3 + both parity = K total.
        let mut available = BTreeMap::new();
        for idx in [2usize, 3, 4, 5] {
            available.insert(idx, shards[idx].clone());
        }
        let recovered = codec.decode_data_shards(&available).unwrap();
        assert_eq!(recovered, data_shards);
    }
}
