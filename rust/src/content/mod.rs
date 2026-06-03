// SPDX-License-Identifier: MIT
//
// ChunkBitmap wire-format codec for the Aether Chunk Shuffle / SAPI protocol.
//
// Wire format:
//   • JSON, snake_case property names.
//   • Bitset: LSB-first within each byte — bit i is set in byte (i/8), at
//     position (i%8). Length = ceil(chunk_count / 8).
//   • Bitset transmitted as standard Base64 (with padding).
//   • Field order in canonical JSON: root_hash, chunk_count, have_bitset,
//     generation.

use base64::{engine::general_purpose::STANDARD, Engine as _};

/// Encode chunk indices into an LSB-first compact bitset.
///
/// Returns `Vec<u8>` of length `ceil(chunk_count / 8)`.
/// Returns `Err` if any index is outside `[0, chunk_count)`.
pub fn bitset_encode(chunk_count: usize, have_indices: &[usize]) -> Result<Vec<u8>, String> {
    if chunk_count == 0 {
        return Ok(vec![]);
    }
    let mut bytes = vec![0u8; (chunk_count + 7) / 8];
    for &i in have_indices {
        if i >= chunk_count {
            return Err(format!("Index {i} out of range [0, {chunk_count})"));
        }
        bytes[i >> 3] |= 1 << (i & 7);
    }
    Ok(bytes)
}

/// Decode a compact bitset back to sorted chunk indices.
pub fn bitset_decode(bitset: &[u8], chunk_count: usize) -> Vec<usize> {
    let mut result = Vec::new();
    let limit = chunk_count.min(bitset.len() * 8);
    for i in 0..limit {
        if bitset[i >> 3] & (1 << (i & 7)) != 0 {
            result.push(i);
        }
    }
    result
}

/// Produce the canonical wire JSON for a ChunkBitmapPayload.
///
/// Field order: root_hash → chunk_count → have_bitset → generation.
pub fn marshal_chunk_bitmap_json(
    root_hash: &str,
    chunk_count: usize,
    have_bitset: &[u8],
    generation: u32,
) -> String {
    let b64 = STANDARD.encode(have_bitset);
    format!(
        r#"{{"root_hash":{rh},"chunk_count":{cc},"have_bitset":{hb},"generation":{gen}}}"#,
        rh  = serde_json_string(root_hash),
        cc  = chunk_count,
        hb  = serde_json_string(&b64),
        gen = generation,
    )
}

/// Minimal JSON string encoder (escapes `"` and `\`).
fn serde_json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"'  => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c    => out.push(c),
        }
    }
    out.push('"');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Vector {
        name:            &'static str,
        root_hash:       &'static str,
        chunk_count:     usize,
        have_indices:    Vec<usize>,
        have_bitset_hex: &'static str,
        have_bitset_b64: &'static str,
        generation:      u32,
        expected_json:   &'static str,
    }

    fn vectors() -> Vec<Vector> {
        vec![
            Vector {
                name:            "chunk_bitmap_sparse",
                root_hash:       "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                chunk_count:     8,
                have_indices:    vec![0, 2, 5],
                have_bitset_hex: "25",
                have_bitset_b64: "JQ==",
                generation:      1,
                expected_json:   r#"{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"JQ==","generation":1}"#,
            },
            Vector {
                name:            "chunk_bitmap_empty",
                root_hash:       "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                chunk_count:     8,
                have_indices:    vec![],
                have_bitset_hex: "00",
                have_bitset_b64: "AA==",
                generation:      1,
                expected_json:   r#"{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"AA==","generation":1}"#,
            },
            Vector {
                name:            "chunk_bitmap_full",
                root_hash:       "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                chunk_count:     8,
                have_indices:    vec![0, 1, 2, 3, 4, 5, 6, 7],
                have_bitset_hex: "ff",
                have_bitset_b64: "/w==",
                generation:      2,
                expected_json:   r#"{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"/w==","generation":2}"#,
            },
            Vector {
                name:            "chunk_bitmap_16chunks_partial",
                root_hash:       "ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a",
                chunk_count:     16,
                have_indices:    vec![0, 8],
                have_bitset_hex: "0101",
                have_bitset_b64: "AQE=",
                generation:      5,
                expected_json:   r#"{"root_hash":"ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a","chunk_count":16,"have_bitset":"AQE=","generation":5}"#,
            },
        ]
    }

    #[test]
    fn encode_produces_correct_bitset() {
        for v in vectors() {
            let bitset = bitset_encode(v.chunk_count, &v.have_indices).unwrap();
            let hex: String = bitset.iter().map(|b| format!("{:02x}", b)).collect();
            assert_eq!(hex, v.have_bitset_hex, "hex mismatch for {}", v.name);
            let b64 = STANDARD.encode(&bitset);
            assert_eq!(b64, v.have_bitset_b64, "base64 mismatch for {}", v.name);
        }
    }

    #[test]
    fn decode_recovers_correct_indices() {
        for v in vectors() {
            let bitset = STANDARD.decode(v.have_bitset_b64).unwrap();
            let mut recovered = bitset_decode(&bitset, v.chunk_count);
            recovered.sort_unstable();
            let mut expected = v.have_indices.clone();
            expected.sort_unstable();
            assert_eq!(recovered, expected, "decode mismatch for {}", v.name);
        }
    }

    #[test]
    fn json_serialization_matches_expected() {
        for v in vectors() {
            let bitset = bitset_encode(v.chunk_count, &v.have_indices).unwrap();
            let actual = marshal_chunk_bitmap_json(v.root_hash, v.chunk_count, &bitset, v.generation);
            assert_eq!(actual, v.expected_json, "JSON mismatch for {}", v.name);
        }
    }

    #[test]
    fn bitset_length_is_ceil_div8() {
        for v in vectors() {
            let bitset = bitset_encode(v.chunk_count, &v.have_indices).unwrap();
            assert_eq!(bitset.len(), (v.chunk_count + 7) / 8, "length mismatch for {}", v.name);
        }
    }

    #[test]
    fn trailing_bits_are_zero() {
        for v in vectors() {
            let bitset = bitset_encode(v.chunk_count, &v.have_indices).unwrap();
            if bitset.is_empty() { continue; }
            let trailing = v.chunk_count % 8;
            if trailing == 0 { continue; }
            let last = *bitset.last().unwrap();
            let valid_mask = (1u8 << trailing).wrapping_sub(1);
            assert_eq!(last & !valid_mask, 0, "trailing bits not zero for {}", v.name);
        }
    }
}
