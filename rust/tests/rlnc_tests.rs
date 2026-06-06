// SPDX-License-Identifier: MIT
//! Integration tests for the RLNC engine (GF(2⁸), encoder, decoder, codec).

use aethernet_protocol::transport::rlnc::{RlncCodec, RlncDecoder, RlncEncoder};

// ── Helpers ───────────────────────────────────────────────────────────────────

fn make_source(k: usize, sym_size: usize) -> Vec<Vec<u8>> {
    (0..k)
        .map(|i| (0..sym_size).map(|j| ((i * sym_size + j) & 0xFF) as u8).collect())
        .collect()
}

fn split_packets(buf: &[u8], count: usize) -> Vec<Vec<u8>> {
    let pkt_size = buf.len() / count;
    (0..count)
        .map(|i| buf[i * pkt_size..(i + 1) * pkt_size].to_vec())
        .collect()
}

// ── GF(256) arithmetic (via behavioural round-trips) ─────────────────────────

#[test]
fn gf256_encode_then_decode_k1_single_symbol() {
    // Encoding a single-element generation is the identity.
    let codec = RlncCodec::new(1);
    let source: Vec<u8> = vec![0xDE, 0xAD, 0xBE, 0xEF];
    let encoded = codec.encode(&source, 2).expect("encode ok");
    // Each packet is 1 coeff byte + 4 data bytes = 5 bytes.
    let pkts: Vec<Vec<u8>> = split_packets(&encoded, 2);
    let decoded = codec.try_decode(&pkts, 1).expect("decode ok");
    assert_eq!(&decoded[..source.len()], source.as_slice(),
        "K=1 round-trip failed");
}

// ── RlncEncoder ───────────────────────────────────────────────────────────────

#[test]
fn encoder_systematic_first_k_packets_equal_source() {
    let k = 4;
    let sym = 8usize;
    let source = make_source(k, sym);
    let mut enc = RlncEncoder::new(source.clone(), true);

    for i in 0..k {
        let (coeff, data) = enc.next_packet().expect("next_packet ok");
        // Systematic: coeff is e_i (only bit i set).
        assert_eq!(coeff[i], 1, "coeff[{i}] should be 1");
        for j in 0..k { if j != i { assert_eq!(coeff[j], 0, "coeff[{j}] != 0"); } }
        assert_eq!(&data, &source[i], "systematic pkt {i} data mismatch");
    }
}

#[test]
fn encoder_repair_packets_not_all_zero() {
    let syms: Vec<Vec<u8>> = vec![
        vec![1u8, 2, 3],
        vec![4u8, 5, 6],
        vec![7u8, 8, 9],
    ];
    let mut enc = RlncEncoder::new(syms, false);

    for i in 0..20 {
        let (coeff, _) = enc.next_packet().expect("next_packet ok");
        assert!(
            coeff.iter().any(|&c| c != 0),
            "repair pkt {i} has all-zero coefficient vector",
        );
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

#[test]
fn decoder_round_trip_k4() {
    let k = 4;
    let sym_size = 8;
    let source = make_source(k, sym_size);
    let mut enc = RlncEncoder::new(source.clone(), true);
    let mut dec = RlncDecoder::new(k, sym_size);

    while !dec.is_complete() {
        let (coeff, data) = enc.next_packet().expect("next_packet ok");
        dec.add_packet(&coeff, &data);
    }

    let decoded = dec.try_decode().expect("try_decode should succeed");
    let expected: Vec<u8> = source.into_iter().flatten().collect();
    assert_eq!(decoded, expected, "decoded output mismatch");
}

#[test]
fn decoder_exactly_k_systematic_packets_complete() {
    let k = 3;
    let sym_size = 4;
    let source = make_source(k, sym_size);
    let mut enc = RlncEncoder::new(source.clone(), true);
    let mut dec = RlncDecoder::new(k, sym_size);

    for _ in 0..k {
        let (coeff, data) = enc.next_packet().expect("next_packet ok");
        dec.add_packet(&coeff, &data);
    }

    assert!(dec.is_complete(), "decoder should be complete after K systematic packets");
    assert_eq!(dec.rank(), k);
}

#[test]
fn decoder_linearly_dependent_packet_ignored() {
    let k = 2;
    let sym_size = 4;
    let source = make_source(k, sym_size);
    let mut enc = RlncEncoder::new(source.clone(), true);
    let mut dec = RlncDecoder::new(k, sym_size);

    // Feed first systematic packet twice — second is linearly dependent.
    let (coeff0, data0) = enc.next_packet().expect("ok");
    dec.add_packet(&coeff0, &data0);
    let rank_before = dec.rank();
    dec.add_packet(&coeff0, &data0);
    assert_eq!(dec.rank(), rank_before, "duplicate packet should not increase rank");
}

#[test]
fn decoder_is_complete_at_rank_k() {
    let k = 2;
    let sym_size = 3;
    let source = make_source(k, sym_size);
    let mut enc = RlncEncoder::new(source, true);
    let mut dec = RlncDecoder::new(k, sym_size);

    assert!(!dec.is_complete());
    let (c, d) = enc.next_packet().expect("ok"); dec.add_packet(&c, &d);
    assert!(!dec.is_complete());
    let (c, d) = enc.next_packet().expect("ok"); dec.add_packet(&c, &d);
    assert!(dec.is_complete());
}

#[test]
fn decoder_repair_only_round_trip() {
    // Feed only repair (non-systematic) packets — must still decode.
    let k = 4;
    let sym_size = 8;
    let source = make_source(k, sym_size);
    let mut enc = RlncEncoder::new(source.clone(), false); // non-systematic
    let mut dec = RlncDecoder::new(k, sym_size);

    let mut attempts = 0;
    while !dec.is_complete() {
        let (coeff, data) = enc.next_packet().expect("ok");
        dec.add_packet(&coeff, &data);
        attempts += 1;
        assert!(attempts < 200, "repair-only decoder stalled");
    }

    let decoded = dec.try_decode().expect("decode ok");
    let expected: Vec<u8> = source.into_iter().flatten().collect();
    assert_eq!(decoded, expected, "repair-only round-trip mismatch");
}

// ── RlncCodec ─────────────────────────────────────────────────────────────────

#[test]
fn codec_metadata() {
    let codec = RlncCodec::new(16);
    assert_eq!(codec.codec_name(), "RLNC-GF256");
    assert_eq!(codec.device_tier_required(), 0);
    assert!((codec.overhead_fraction() - 0.05).abs() < 1e-9);
    assert_eq!(codec.fixed_symbol_size_bytes(), 0);
}

#[test]
fn codec_generation_size_bounds() {
    // Valid bounds: [1, 255].
    assert!(RlncCodec::new(1).codec_name() == "RLNC-GF256");
    assert!(RlncCodec::new(255).codec_name() == "RLNC-GF256");
}

#[test]
fn codec_large_payload_round_trip() {
    let codec = RlncCodec::new(16);
    let source: Vec<u8> = (0..1024).map(|i| (i & 0xFF) as u8).collect();
    let target_count = 20; // 16 systematic + 4 repair
    let encoded = codec.encode(&source, target_count).expect("encode ok");
    let pkts = split_packets(&encoded, target_count);
    let decoded = codec.try_decode(&pkts, 16).expect("decode ok");
    assert_eq!(&decoded[..source.len()], source.as_slice(),
        "large payload round-trip mismatch");
}

#[test]
fn codec_decode_with_losses() {
    // Lose 4 packets from a 20-packet block (K=16); should still decode.
    let codec = RlncCodec::new(16);
    let source: Vec<u8> = (0..512).map(|i| (i & 0xFF) as u8).collect();
    let target_count = 20;
    let encoded = codec.encode(&source, target_count).expect("encode ok");
    let mut pkts = split_packets(&encoded, target_count);
    // Remove packets 0, 3, 7, 11.
    for &idx in &[11usize, 7, 3, 0] { pkts.remove(idx); }
    let decoded = codec.try_decode(&pkts, 16).expect("decode ok after losses");
    assert_eq!(&decoded[..source.len()], source.as_slice(),
        "decode-with-losses mismatch");
}
