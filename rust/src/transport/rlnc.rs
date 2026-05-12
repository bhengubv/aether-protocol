// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
//
// Components
// ──────────
//   GF256_EXP / GF256_LOG — const precomputed tables (no heap, no lazy_static).
//   gf256_mul / gf256_inv  — O(1) field arithmetic.
//   RlncEncoder             — systematic + random-repair packet generation.
//   RlncDecoder             — incremental Gauss-Jordan elimination.
//   RlncCodec               — FecCodec trait adapter.
//
// Wire format per packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]

use std::error::Error;
use rand::RngCore;

// ── GF(2⁸) tables — generated at compile time ────────────────────────────────

// We build the tables with a `const fn` so they live in the binary (BSS section).
// No heap allocation, no lazy_static, no std::sync::OnceLock — fully const.

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
            x ^= 0x11D; // reduce mod primitive polynomial
        }
        x &= 0xFF;
        i += 1;
    }
    i = 255;
    while i < 512 {
        exp[i] = exp[i - 255];
        i += 1;
    }
    log[1] = 0; // log_α(1) = 0
    (exp, log)
}

const _TABLES: ([u8; 512], [u8; 256]) = build_gf256_tables();
const GF256_EXP: [u8; 512] = _TABLES.0;
const GF256_LOG: [u8; 256] = _TABLES.1;

#[inline(always)]
fn gf256_mul(a: u8, b: u8) -> u8 {
    if a == 0 || b == 0 {
        return 0;
    }
    GF256_EXP[GF256_LOG[a as usize] as usize + GF256_LOG[b as usize] as usize]
}

#[inline(always)]
fn gf256_inv(a: u8) -> u8 {
    assert!(a != 0, "rlnc: GF256 inverse of zero");
    GF256_EXP[255 - GF256_LOG[a as usize] as usize]
}

#[inline(always)]
fn gf256_add(a: u8, b: u8) -> u8 { a ^ b }

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/// Encodes K source symbols as systematic + random-repair RLNC packets.
///
/// The first `generation_size()` packets are systematic (identity coefficient
/// vectors; byte-identical to the source symbols).  Subsequent packets use
/// random GF(2⁸) coefficients drawn via `getrandom`.
pub struct RlncEncoder {
    source:     Vec<Vec<u8>>,
    next_index: usize,
    systematic: bool,
}

impl RlncEncoder {
    /// Create an encoder for a generation of K source symbols.
    ///
    /// `systematic = true` makes the first K packets identical to the source.
    pub fn new(source: Vec<Vec<u8>>, systematic: bool) -> Self {
        assert!(!source.is_empty(), "rlnc: source must have at least one symbol");
        Self { source, next_index: 0, systematic }
    }

    pub fn generation_size(&self) -> usize { self.source.len() }
    pub fn symbol_size(&self)     -> usize { self.source[0].len() }

    /// Returns `(coefficients, encoded_symbol)` for the next packet.
    pub fn next_packet(&mut self) -> Result<(Vec<u8>, Vec<u8>), Box<dyn Error>> {
        let k = self.generation_size();
        let s = self.symbol_size();

        if self.systematic && self.next_index < k {
            // Systematic: e_i coefficient vector.
            let mut coeff = vec![0u8; k];
            coeff[self.next_index] = 1;
            let encoded = self.source[self.next_index].clone();
            self.next_index += 1;
            return Ok((coeff, encoded));
        }

        // Repair: random GF(256) coefficient vector.
        let mut coeff = vec![0u8; k];
        rand::thread_rng().fill_bytes(&mut coeff);
        if coeff.iter().all(|&c| c == 0) { coeff[0] = 1; }

        let encoded = self.encode_symbol(&coeff);
        self.next_index += 1;
        Ok((coeff, encoded))
    }

    fn encode_symbol(&self, coefficients: &[u8]) -> Vec<u8> {
        let s = self.symbol_size();
        let mut out = vec![0u8; s];
        for (k_idx, sym) in self.source.iter().enumerate() {
            let c = coefficients[k_idx];
            if c == 0 { continue; }
            for i in 0..s {
                out[i] = gf256_add(out[i], gf256_mul(c, sym[i]));
            }
        }
        out
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/// Incremental Gauss-Jordan decoder over GF(2⁸).
///
/// Maintains the accumulated coefficient matrix in RREF as packets arrive.
/// Decoding is immediate when `rank()` equals the generation size K.
pub struct RlncDecoder {
    k:           usize,
    symbol_size: usize,
    pivot_coeff: Vec<Option<Vec<u8>>>,
    pivot_data:  Vec<Option<Vec<u8>>>,
    rank:        usize,
}

impl RlncDecoder {
    pub fn new(k: usize, symbol_size: usize) -> Self {
        Self {
            k,
            symbol_size,
            pivot_coeff: vec![None; k],
            pivot_data:  vec![None; k],
            rank:        0,
        }
    }

    /// Number of linearly independent packets received.
    pub fn rank(&self) -> usize { self.rank }

    /// `true` when all K source symbols can be reconstructed.
    pub fn is_complete(&self) -> bool { self.rank == self.k }

    /// Submit an encoded packet. Returns `true` if rank increased.
    pub fn add_packet(&mut self, coefficients: &[u8], encoded_symbol: &[u8]) -> bool {
        let k = self.k;
        let s = self.symbol_size;
        let mut row  = coefficients.to_vec();
        let mut data = encoded_symbol.to_vec();

        // ── Forward-elimination ──────────────────────────────────────────────
        for j in 0..k {
            if row[j] == 0 { continue; }
            if let Some(pr) = self.pivot_coeff[j].clone() {
                let pd = self.pivot_data[j].clone().unwrap();
                let c  = row[j];
                for i in 0..k { row[i]  = gf256_add(row[i],  gf256_mul(c, pr[i])); }
                for i in 0..s { data[i] = gf256_add(data[i], gf256_mul(c, pd[i])); }
            }
        }

        // ── Find pivot column ────────────────────────────────────────────────
        let pivot_col = match (0..k).find(|&j| row[j] != 0) {
            Some(j) => j,
            None    => return false, // linearly dependent
        };

        // ── Normalise ────────────────────────────────────────────────────────
        let inv = gf256_inv(row[pivot_col]);
        for i in 0..k { row[i]  = gf256_mul(inv, row[i]); }
        for i in 0..s { data[i] = gf256_mul(inv, data[i]); }

        // ── Back-substitution ────────────────────────────────────────────────
        for r in 0..k {
            if let Some(pr) = self.pivot_coeff[r].as_mut() {
                let c = pr[pivot_col];
                if c != 0 {
                    let pd = self.pivot_data[r].as_mut().unwrap();
                    for i in 0..k { pr[i] = gf256_add(pr[i], gf256_mul(c, row[i])); }
                    for i in 0..s { pd[i] = gf256_add(pd[i], gf256_mul(c, data[i])); }
                }
            }
        }

        self.pivot_coeff[pivot_col] = Some(row);
        self.pivot_data[pivot_col]  = Some(data);
        self.rank += 1;
        true
    }

    /// Returns the decoded source bytes when `is_complete`, or `None` otherwise.
    pub fn try_decode(&self) -> Option<Vec<u8>> {
        if !self.is_complete() { return None; }
        let mut result = vec![0u8; self.k * self.symbol_size];
        for j in 0..self.k {
            let base = j * self.symbol_size;
            let src  = self.pivot_data[j].as_ref().unwrap();
            result[base..base + self.symbol_size].copy_from_slice(src);
        }
        Some(result)
    }
}

// ── RlncCodec ────────────────────────────────────────────────────────────────

/// RLNC FEC codec over GF(2⁸) — implements the `FecCodec` trait.
///
/// Each encoded packet is `[ K coefficient bytes ][ symbolSize data bytes ]`.
pub struct RlncCodec {
    k: usize,
}

impl RlncCodec {
    /// Create a new codec with the given generation size K.
    pub fn new(generation_size: usize) -> Self {
        assert!(
            generation_size >= 1 && generation_size <= 255,
            "rlnc: generation_size must be in [1, 255]"
        );
        Self { k: generation_size }
    }

    pub fn codec_name(&self)              -> &str   { "RLNC-GF256" }
    pub fn device_tier_required(&self)    -> u8     { 0 }
    pub fn overhead_fraction(&self)       -> f64    { 0.05 }
    pub fn fixed_symbol_size_bytes(&self) -> usize  { 0 }

    /// Encode source into `target_symbol_count` concatenated packets.
    pub fn encode(
        &self,
        source: &[u8],
        target_symbol_count: usize,
    ) -> Result<Vec<u8>, Box<dyn Error>> {
        assert!(!source.is_empty(), "rlnc: source must not be empty");
        let k           = self.k;
        let symbol_size = source.len().div_ceil(k);
        let packet_size = k + symbol_size;
        let symbols     = split_into_symbols(source, k, symbol_size);

        let mut enc    = RlncEncoder::new(symbols, true);
        let mut output = vec![0u8; target_symbol_count * packet_size];

        for i in 0..target_symbol_count {
            let (coeff, data) = enc.next_packet()?;
            let offset        = i * packet_size;
            output[offset..offset + k].copy_from_slice(&coeff);
            output[offset + k..offset + packet_size].copy_from_slice(&data);
        }
        Ok(output)
    }

    /// Reconstruct source from received packets.
    pub fn try_decode(
        &self,
        received_symbols: &[Vec<u8>],
        _source_symbol_count: usize,
    ) -> Option<Vec<u8>> {
        if received_symbols.is_empty() { return None; }
        let k           = self.k;
        let symbol_size = received_symbols[0].len().saturating_sub(k);
        if symbol_size == 0 { return None; }

        let mut dec = RlncDecoder::new(k, symbol_size);
        for pkt in received_symbols {
            if pkt.len() < k { continue; }
            dec.add_packet(&pkt[..k], &pkt[k..]);
            if dec.is_complete() { break; }
        }
        dec.try_decode()
    }
}

fn split_into_symbols(source: &[u8], k: usize, symbol_size: usize) -> Vec<Vec<u8>> {
    (0..k)
        .map(|i| {
            let mut sym    = vec![0u8; symbol_size];
            let offset     = i * symbol_size;
            let length     = symbol_size.min(source.len().saturating_sub(offset));
            if length > 0 {
                sym[..length].copy_from_slice(&source[offset..offset + length]);
            }
            sym
        })
        .collect()
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    // ── GF(2⁸) arithmetic ────────────────────────────────────────────────────

    #[test]
    fn gf256_mul_by_zero_is_zero() {
        for x in 0u8..=255 {
            assert_eq!(0, gf256_mul(0, x), "0 * {x} != 0");
            assert_eq!(0, gf256_mul(x, 0), "{x} * 0 != 0");
        }
    }

    #[test]
    fn gf256_mul_by_one_is_identity() {
        for x in 0u8..=255 {
            assert_eq!(x, gf256_mul(1, x), "1 * {x} != {x}");
            assert_eq!(x, gf256_mul(x, 1), "{x} * 1 != {x}");
        }
    }

    #[test]
    fn gf256_mul_is_commutative() {
        // Spot-check a selection of pairs.
        let pairs = [(2u8, 3u8), (7, 11), (17, 31), (0xAB, 0xCD), (255, 128)];
        for (a, b) in pairs {
            assert_eq!(gf256_mul(a, b), gf256_mul(b, a), "not commutative: {a}*{b}");
        }
    }

    #[test]
    fn gf256_mul_two_by_two() {
        // 2 * 2 = 4 in GF(2^8) (shift left, no reduction needed since < 256).
        assert_eq!(4, gf256_mul(2, 2));
    }

    #[test]
    fn gf256_mul_overflow_reduces_correctly() {
        // 0x80 * 0x02 = 0x100 → reduce mod 0x11D → 0x1D
        assert_eq!(0x1D, gf256_mul(0x80, 0x02));
    }

    #[test]
    fn gf256_inv_of_one_is_one() {
        assert_eq!(1, gf256_inv(1));
    }

    #[test]
    fn gf256_inv_satisfies_a_times_inv_a_eq_one() {
        for a in 1u8..=255 {
            let inv = gf256_inv(a);
            assert_eq!(1, gf256_mul(a, inv), "a={a}, inv={inv}");
        }
    }

    #[test]
    fn gf256_add_is_xor() {
        for a in 0u8..=255 {
            for b in [0u8, 1, 0x80, 0xFF] {
                assert_eq!(a ^ b, gf256_add(a, b), "gf256_add({a},{b}) != {}", a ^ b);
            }
        }
    }

    // ── RlncEncoder ──────────────────────────────────────────────────────────

    #[test]
    #[should_panic(expected = "rlnc: source must have at least one symbol")]
    fn encoder_panics_on_empty_source() {
        let _ = RlncEncoder::new(vec![], true);
    }

    #[test]
    fn encoder_generation_size_and_symbol_size() {
        let source = vec![vec![1u8, 2, 3], vec![4u8, 5, 6]];
        let enc = RlncEncoder::new(source, true);
        assert_eq!(2, enc.generation_size());
        assert_eq!(3, enc.symbol_size());
    }

    #[test]
    fn encoder_systematic_packets_match_source() {
        let source = vec![vec![0xAAu8, 0xBB], vec![0xCCu8, 0xDD]];
        let mut enc = RlncEncoder::new(source.clone(), true);

        let (coeff0, data0) = enc.next_packet().unwrap();
        assert_eq!(vec![1u8, 0], coeff0, "first systematic coeff");
        assert_eq!(source[0], data0, "first systematic data");

        let (coeff1, data1) = enc.next_packet().unwrap();
        assert_eq!(vec![0u8, 1], coeff1, "second systematic coeff");
        assert_eq!(source[1], data1, "second systematic data");
    }

    #[test]
    fn encoder_repair_packet_has_correct_sizes() {
        let source = vec![vec![1u8, 2, 3, 4]; 3];
        let k = source.len();
        let s = source[0].len();
        let mut enc = RlncEncoder::new(source, false); // non-systematic → all random
        let (coeff, data) = enc.next_packet().unwrap();
        assert_eq!(k, coeff.len(), "coeff len");
        assert_eq!(s, data.len(), "data len");
    }

    #[test]
    fn encoder_repair_coefficients_are_not_all_zero() {
        // Statistically nearly impossible to generate 100 consecutive all-zero coefficient vectors.
        let source = vec![vec![0u8; 4]; 4];
        let mut enc = RlncEncoder::new(source, false);
        for _ in 0..100 {
            let (coeff, _) = enc.next_packet().unwrap();
            if coeff.iter().any(|&c| c != 0) {
                return; // passed
            }
        }
        panic!("all 100 repair coefficient vectors were all-zero — extremely unlikely");
    }

    // ── RlncDecoder ──────────────────────────────────────────────────────────

    #[test]
    fn decoder_starts_at_rank_zero() {
        let dec = RlncDecoder::new(3, 4);
        assert_eq!(0, dec.rank());
        assert!(!dec.is_complete());
    }

    #[test]
    fn decoder_rank_increases_per_independent_packet() {
        let k = 3;
        let s = 4;
        let mut dec = RlncDecoder::new(k, s);

        // Feed systematic (identity) packets.
        for i in 0..k {
            let mut coeff = vec![0u8; k];
            coeff[i] = 1;
            let data = vec![(i as u8 + 1) * 10; s];
            let increased = dec.add_packet(&coeff, &data);
            assert!(increased, "rank should increase for packet {i}");
            assert_eq!(i + 1, dec.rank());
        }
        assert!(dec.is_complete());
    }

    #[test]
    fn decoder_rejects_linearly_dependent_packet() {
        let k = 2;
        let s = 2;
        let mut dec = RlncDecoder::new(k, s);

        // Add first packet.
        dec.add_packet(&[1, 0], &[0xAA, 0xBB]);
        // Add the same packet again — should be linearly dependent.
        let increased = dec.add_packet(&[1, 0], &[0xAA, 0xBB]);
        assert!(!increased, "duplicate packet should not increase rank");
        assert_eq!(1, dec.rank());
    }

    #[test]
    fn decoder_try_decode_returns_none_when_incomplete() {
        let mut dec = RlncDecoder::new(2, 2);
        dec.add_packet(&[1, 0], &[0x01, 0x02]);
        assert!(dec.try_decode().is_none());
    }

    // ── Full encode/decode round-trips ────────────────────────────────────────

    #[test]
    fn round_trip_k1_single_symbol() {
        let source = vec![vec![0xDE, 0xAD, 0xBE, 0xEF]];
        let mut enc = RlncEncoder::new(source.clone(), true);
        let mut dec = RlncDecoder::new(1, 4);

        let (coeff, data) = enc.next_packet().unwrap();
        dec.add_packet(&coeff, &data);

        let recovered = dec.try_decode().unwrap();
        assert_eq!(source[0], recovered);
    }

    #[test]
    fn round_trip_k3_systematic() {
        let source: Vec<Vec<u8>> = (0..3).map(|i| vec![(i as u8) * 0x11; 4]).collect();
        let mut enc = RlncEncoder::new(source.clone(), true);
        let mut dec = RlncDecoder::new(3, 4);

        for _ in 0..3 {
            let (coeff, data) = enc.next_packet().unwrap();
            dec.add_packet(&coeff, &data);
        }

        let recovered = dec.try_decode().unwrap();
        let expected: Vec<u8> = source.concat();
        assert_eq!(expected, recovered);
    }

    #[test]
    fn round_trip_with_repair_packet() {
        // K=2, S=4: feed one systematic + one repair → should decode.
        let source = vec![vec![0x11u8, 0x22, 0x33, 0x44], vec![0x55u8, 0x66, 0x77, 0x88]];
        let mut enc = RlncEncoder::new(source.clone(), true);
        let mut dec = RlncDecoder::new(2, 4);

        let (c0, d0) = enc.next_packet().unwrap(); // systematic 0
        dec.add_packet(&c0, &d0);
        // Skip systematic 1, get a repair instead.
        enc.next_packet().unwrap(); // consume systematic 1
        let (c_rep, d_rep) = enc.next_packet().unwrap(); // repair
        dec.add_packet(&c_rep, &d_rep);

        if dec.is_complete() {
            let recovered = dec.try_decode().unwrap();
            let expected: Vec<u8> = source.concat();
            assert_eq!(expected, recovered);
        }
        // If by extreme chance the repair was linearly dependent on c0, the
        // test just verifies no panic — the probability is 1/255.
    }

    // ── RlncCodec ────────────────────────────────────────────────────────────

    #[test]
    #[should_panic]
    fn codec_panics_on_generation_size_zero() {
        let _ = RlncCodec::new(0);
    }

    #[test]
    fn codec_properties() {
        let codec = RlncCodec::new(4);
        assert_eq!("RLNC-GF256", codec.codec_name());
        assert_eq!(0, codec.device_tier_required());
        assert!((0.0..=1.0).contains(&codec.overhead_fraction()));
        assert_eq!(0, codec.fixed_symbol_size_bytes()); // variable-symbol codec
    }

    #[test]
    fn codec_encode_decode_round_trip() {
        let source = b"hello world from aether rlnc";
        let k = 4usize;
        let codec = RlncCodec::new(k);

        let encoded = codec.encode(source, k).unwrap();
        let symbol_size = source.len().div_ceil(k);
        let packet_size = k + symbol_size;
        assert_eq!(k * packet_size, encoded.len());

        // Split encoded bytes back into Vec<Vec<u8>> packets.
        let packets: Vec<Vec<u8>> = (0..k)
            .map(|i| encoded[i * packet_size..(i + 1) * packet_size].to_vec())
            .collect();

        let decoded = codec.try_decode(&packets, k).unwrap();

        // First `source.len()` bytes of decoded should match source (remainder is padding).
        assert_eq!(source, &decoded[..source.len()]);
    }

    #[test]
    fn codec_decode_empty_packets_returns_none() {
        let codec = RlncCodec::new(2);
        assert!(codec.try_decode(&[], 2).is_none());
    }

    #[test]
    fn codec_encode_more_than_k_symbols_no_panic() {
        let source = b"test payload for rlnc repair";
        let k = 3;
        let codec = RlncCodec::new(k);
        // Request k+2 encoded symbols — the extra two are repair packets.
        let encoded = codec.encode(source, k + 2).unwrap();
        let symbol_size = source.len().div_ceil(k);
        let packet_size = k + symbol_size;
        assert_eq!((k + 2) * packet_size, encoded.len());
    }
}
