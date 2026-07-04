// SPDX-License-Identifier: MIT
//! Cross-language WebRTC signalling frame fixture verifier.
//!
//! Reads `../fixtures/webrtc/inputs.json` + `../fixtures/webrtc/expected/*.bin`
//! and asserts this crate's `AWS1`+JSON signalling frame is byte-identical to the
//! C# oracle (`AetherNet.Transport.WebRtc.RelayWebRtcSignaling` /
//! `WebRtcSignalJsonContext`) — and, transitively, to every other language port
//! that pins the SAME shared fixture — for every case, then round-trips each back
//! to matching fields. See `fixtures/README.md`.
//!
//! Gated behind the `webrtc` feature (as the `webrtc_relay_signaling` module is,
//! since `Signal`/`SignalType`/`frame_signal`/`parse_frame` only compile then).
//! Build/run with:
//!   cargo test --features webrtc --test webrtc_fixture

#![cfg(feature = "webrtc")]

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;

use aethernet_protocol::transport::webrtc::{Signal, SignalType};
use aethernet_protocol::transport::webrtc_relay_signaling::{frame_signal, parse_frame};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("webrtc")
}

fn load_inputs() -> Vec<Value> {
    let path = fixtures_dir().join("inputs.json");
    let raw = fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {:?}: {}", path, e));
    serde_json::from_str(&raw).expect("parse inputs.json")
}

fn expected(name: &str) -> Vec<u8> {
    let p = fixtures_dir().join("expected").join(format!("{}.bin", name));
    fs::read(&p).unwrap_or_else(|e| panic!("read {:?}: {}", p, e))
}

fn str_field<'a>(o: &'a Value, key: &str) -> &'a str {
    o.get(key).and_then(Value::as_str).unwrap_or("")
}

/// Optional string field: an absent OR empty value maps to `None`, matching the
/// fixture convention (empty `sdp`/`candidate`/`sdp_mid` = omitted from the frame).
fn opt_str_field(o: &Value, key: &str) -> Option<String> {
    let s = str_field(o, key);
    if s.is_empty() {
        None
    } else {
        Some(s.to_string())
    }
}

fn u16_field(o: &Value, key: &str) -> u16 {
    o.get(key).and_then(Value::as_u64).unwrap_or(0) as u16
}

fn signal_type(o: &Value) -> SignalType {
    let t = o.get("type").and_then(Value::as_u64).unwrap_or(0);
    match t {
        0 => SignalType::Offer,
        1 => SignalType::Answer,
        2 => SignalType::IceCandidate,
        other => panic!("invalid type {}", other),
    }
}

fn signal_from(o: &Value) -> Signal {
    Signal {
        from_uhid: str_field(o, "from_uhid").to_string(),
        to_uhid: str_field(o, "to_uhid").to_string(),
        signal_type: signal_type(o),
        sdp: opt_str_field(o, "sdp"),
        candidate: opt_str_field(o, "candidate"),
        sdp_mid: opt_str_field(o, "sdp_mid"),
        sdp_mline_index: u16_field(o, "sdp_mline_index"),
    }
}

#[test]
fn webrtc_frame_matches_csharp_oracle() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        let got = frame_signal(&signal_from(&o));
        assert_eq!(
            got,
            expected(name),
            "fixture {}: frame bytes diverge — see fixtures/README.md",
            name
        );
    }
}

#[test]
fn webrtc_frame_deframes_roundtrips_all_fields() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        let data = expected(name);
        let s = parse_frame(&data)
            .unwrap_or_else(|| panic!("{}: deframe (parse_frame) returned None", name));

        assert_eq!(s.from_uhid, str_field(&o, "from_uhid"), "{}: from_uhid", name);
        assert_eq!(s.to_uhid, str_field(&o, "to_uhid"), "{}: to_uhid", name);
        assert_eq!(s.signal_type, signal_type(&o), "{}: type", name);
        assert_eq!(s.sdp, opt_str_field(&o, "sdp"), "{}: sdp", name);
        assert_eq!(s.candidate, opt_str_field(&o, "candidate"), "{}: candidate", name);
        assert_eq!(s.sdp_mid, opt_str_field(&o, "sdp_mid"), "{}: sdp_mid", name);
        assert_eq!(
            s.sdp_mline_index,
            u16_field(&o, "sdp_mline_index"),
            "{}: sdp_mline_index",
            name
        );

        // Re-frame the decoded signal reproduces the oracle bytes exactly.
        let reframed = frame_signal(&s);
        assert_eq!(reframed, data, "{}: re-frame diverges", name);
    }
}
