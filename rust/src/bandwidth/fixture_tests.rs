// SPDX-License-Identifier: MIT

//! Cross-language fixture driver for the AetherNet Bandwidth Measurement
//! Framework (ABMF).
//!
//! Drives the Rust reference through the shared corpus at
//! `tests/cross-language/bandwidth-fixtures.json`. Every other AetherNet SDK
//! drives the SAME corpus and MUST produce identical results. This mirrors the
//! C# oracle in `AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs` —
//! without it, "identical by construction" is unverified.
//!
//! Integer/string/enum fields are asserted EXACTLY; floating-point fields
//! (srttMs, rttVarMs, rtPropMs, lossRate) are asserted within `toleranceAbs`.

#![cfg(test)]

use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use serde_json::Value;

use crate::bandwidth::director::BandwidthDirector;
use crate::bandwidth::estimator::BandwidthEstimator;
use crate::bandwidth::models::{
    BandwidthConfidence, BandwidthGossipPayload, BandwidthProbeAck, BandwidthSample,
};

// ── Corpus loading ──────────────────────────────────────────────────────────

/// The cross-language corpus, embedded at compile time relative to the crate
/// manifest so the test binary is location-independent.
const CORPUS_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../tests/cross-language/bandwidth-fixtures.json"
));

fn corpus() -> Value {
    serde_json::from_str(CORPUS_JSON).expect("bandwidth-fixtures.json must be valid JSON")
}

fn tolerance(root: &Value) -> f64 {
    root["toleranceAbs"]
        .as_f64()
        .expect("toleranceAbs must be a number")
}

// ── Field accessors (panic loudly with the field name on a corpus shape error)

fn i64_of(v: &Value, key: &str) -> i64 {
    v[key]
        .as_i64()
        .unwrap_or_else(|| panic!("field `{key}` missing or not an integer in {v}"))
}

fn i32_of(v: &Value, key: &str) -> i32 {
    i64_of(v, key) as i32
}

fn f64_of(v: &Value, key: &str) -> f64 {
    v[key]
        .as_f64()
        .unwrap_or_else(|| panic!("field `{key}` missing or not a number in {v}"))
}

fn str_of<'a>(v: &'a Value, key: &str) -> &'a str {
    v[key]
        .as_str()
        .unwrap_or_else(|| panic!("field `{key}` missing or not a string in {v}"))
}

fn name_of(v: &Value) -> &str {
    str_of(v, "name")
}

fn parse_confidence(s: &str) -> BandwidthConfidence {
    match s {
        "None" => BandwidthConfidence::None,
        "Low" => BandwidthConfidence::Low,
        "Medium" => BandwidthConfidence::Medium,
        "High" => BandwidthConfidence::High,
        other => panic!("bad confidence {other}"),
    }
}

fn ms_of_duration(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

// ── probeAck ────────────────────────────────────────────────────────────────
//
// Mirrors C# ProbeAck_RttAndOwd_Exact: rtt() µs and forward_owd() µs are EXACT.

#[test]
fn fixture_probe_ack_rtt_and_owd_exact() {
    let root = corpus();
    let cases = root["probeAck"]
        .as_array()
        .expect("probeAck must be an array");

    for f in cases {
        let name = name_of(f);
        let ack = BandwidthProbeAck {
            sequence: 1,
            sender_send_us: i64_of(f, "senderSendUs"),
            receiver_receive_us: i64_of(f, "receiverReceiveUs"),
            receiver_send_us: i64_of(f, "receiverSendUs"),
            sender_receive_us: i64_of(f, "senderReceiveUs"),
            probe_bytes: i32_of(f, "probeBytes"),
        };

        let expect_rtt = i64_of(f, "expectRttUs");
        let actual_rtt = ack.rtt().as_micros() as i64;
        assert_eq!(
            expect_rtt, actual_rtt,
            "probeAck[{name}] rtt µs: expected {expect_rtt}, actual {actual_rtt}"
        );

        let expect_owd = i64_of(f, "expectForwardOwdUs");
        let actual_owd = ack.forward_owd().as_micros() as i64;
        assert_eq!(
            expect_owd, actual_owd,
            "probeAck[{name}] forward_owd µs: expected {expect_owd}, actual {actual_owd}"
        );
    }
}

// ── rto ─────────────────────────────────────────────────────────────────────
//
// Mirrors C# Rto_Clamped_MatchesRfc6298: build a BandwidthSample with the given
// srttMs/rttVarMs (rt_prop = 10 ms, placeholders elsewhere), assert rto() ms
// within ±0.1 (C# uses precision: 1).

#[test]
fn fixture_rto_clamped_matches_rfc6298() {
    let root = corpus();
    let tol = 0.1_f64;
    let cases = root["rto"].as_array().expect("rto must be an array");

    for f in cases {
        let name = name_of(f);
        let srtt_ms = f64_of(f, "srttMs");
        let rtt_var_ms = f64_of(f, "rttVarMs");

        let sample = BandwidthSample {
            transport_name: "T".to_string(),
            btl_bw_bps: 1_000_000,
            available_bps: 900_000,
            bdp_bytes: 1000,
            srtt: Duration::from_secs_f64(srtt_ms / 1000.0),
            rtt_var: Duration::from_secs_f64(rtt_var_ms / 1000.0),
            rt_prop: Duration::from_millis(10),
            loss_rate: 0.0,
            phy_cap_bps: 0,
            confidence: BandwidthConfidence::High,
            measured_at: SystemTime::now(),
        };

        let expect_ms = f64_of(f, "expectRtoMs");
        let actual_ms = ms_of_duration(sample.rto());
        assert!(
            (expect_ms - actual_ms).abs() <= tol,
            "rto[{name}] ms: expected {expect_ms}, actual {actual_ms} (tol {tol})"
        );
    }
}

// ── phyCap ──────────────────────────────────────────────────────────────────
//
// Mirrors C# PhyCap_FromRssi_Exact: new estimator(max 10_000_000_000),
// apply_phy_hint(rssiDbm), assert current_sample().phy_cap_bps EXACT.

#[test]
fn fixture_phy_cap_from_rssi_exact() {
    let root = corpus();
    let cases = root["phyCap"].as_array().expect("phyCap must be an array");

    for f in cases {
        let name = name_of(f);
        let est = BandwidthEstimator::new("T", 10_000_000_000);
        est.apply_phy_hint(i32_of(f, "rssiDbm"));

        let expect = i64_of(f, "expectCapBps");
        let actual = est.current_sample().phy_cap_bps;
        assert_eq!(
            expect, actual,
            "phyCap[{name}] phy_cap_bps: expected {expect}, actual {actual}"
        );
    }
}

// ── estimator ───────────────────────────────────────────────────────────────
//
// Mirrors C# Estimator_DrivesToExpectedSample: new estimator(transport, maxBps);
// apply each op (delivery/loss/phyHint/gossip — gossip rtPropMs as ms Duration);
// assert integer/enum fields EXACT, float fields within toleranceAbs.

#[test]
fn fixture_estimator_drives_to_expected_sample() {
    let root = corpus();
    let tol = tolerance(&root);
    let cases = root["estimator"]
        .as_array()
        .expect("estimator must be an array");

    for f in cases {
        let name = name_of(f);
        let est = BandwidthEstimator::new(str_of(f, "transport"), i64_of(f, "maxBps"));

        for op in f["ops"].as_array().expect("ops must be an array") {
            match str_of(op, "op") {
                "delivery" => est.record_delivery(
                    i32_of(op, "bytes"),
                    i64_of(op, "sendUs"),
                    i64_of(op, "deliverUs"),
                ),
                "loss" => est.record_loss(i32_of(op, "bytes")),
                "phyHint" => est.apply_phy_hint(i32_of(op, "rssiDbm")),
                "gossip" => est.warm_from_gossip(
                    i64_of(op, "btlBwBps"),
                    Duration::from_secs_f64(f64_of(op, "rtPropMs") / 1000.0),
                    parse_confidence(str_of(op, "confidence")),
                ),
                other => panic!("estimator[{name}] unknown op `{other}`"),
            }
        }

        let s = est.current_sample();
        let exp = &f["expect"];

        // Integer / enum fields — exact.
        if let Some(v) = exp.get("btlBwBps").and_then(Value::as_i64) {
            assert_eq!(
                v, s.btl_bw_bps,
                "estimator[{name}] btl_bw_bps: expected {v}, actual {}",
                s.btl_bw_bps
            );
        }
        if let Some(v) = exp.get("effectiveBps").and_then(Value::as_i64) {
            assert_eq!(
                v,
                s.effective_bps(),
                "estimator[{name}] effective_bps: expected {v}, actual {}",
                s.effective_bps()
            );
        }
        if let Some(v) = exp.get("availableBps").and_then(Value::as_i64) {
            assert_eq!(
                v, s.available_bps,
                "estimator[{name}] available_bps: expected {v}, actual {}",
                s.available_bps
            );
        }
        if let Some(v) = exp.get("bdpBytes").and_then(Value::as_i64) {
            assert_eq!(
                v, s.bdp_bytes,
                "estimator[{name}] bdp_bytes: expected {v}, actual {}",
                s.bdp_bytes
            );
        }
        if let Some(v) = exp.get("phyCapBps").and_then(Value::as_i64) {
            assert_eq!(
                v, s.phy_cap_bps,
                "estimator[{name}] phy_cap_bps: expected {v}, actual {}",
                s.phy_cap_bps
            );
        }
        if let Some(v) = exp.get("confidence").and_then(Value::as_str) {
            let want = parse_confidence(v);
            assert_eq!(
                want, s.confidence,
                "estimator[{name}] confidence: expected {want:?}, actual {:?}",
                s.confidence
            );
        }

        // Float fields — tolerance.
        if let Some(v) = exp.get("srttMs").and_then(Value::as_f64) {
            let actual = ms_of_duration(s.srtt);
            assert!(
                (v - actual).abs() <= tol,
                "estimator[{name}] srtt_ms: expected {v}, actual {actual} (tol {tol})"
            );
        }
        if let Some(v) = exp.get("rttVarMs").and_then(Value::as_f64) {
            let actual = ms_of_duration(s.rtt_var);
            assert!(
                (v - actual).abs() <= tol,
                "estimator[{name}] rtt_var_ms: expected {v}, actual {actual} (tol {tol})"
            );
        }
        if let Some(v) = exp.get("rtPropMs").and_then(Value::as_f64) {
            let actual = ms_of_duration(s.rt_prop);
            assert!(
                (v - actual).abs() <= tol,
                "estimator[{name}] rt_prop_ms: expected {v}, actual {actual} (tol {tol})"
            );
        }
        if let Some(v) = exp.get("lossRate").and_then(Value::as_f64) {
            assert!(
                (v - s.loss_rate).abs() <= tol,
                "estimator[{name}] loss_rate: expected {v}, actual {} (tol {tol})",
                s.loss_rate
            );
        }
    }
}

// ── director ────────────────────────────────────────────────────────────────
//
// Mirrors C# Director_RecommendsExpectedTransport: register one estimator per
// `register` name (generous maxBps so PHY default does not cap gossip seeds);
// apply each gossip (rtPropUs int µs); recommend_transport(peer, payload) must
// equal expectTransport (None when JSON null).

#[test]
fn fixture_director_recommends_expected_transport() {
    let root = corpus();
    let cases = root["director"]
        .as_array()
        .expect("director must be an array");

    for f in cases {
        let name = name_of(f);
        let director = BandwidthDirector::new();

        for t in f["register"].as_array().expect("register must be an array") {
            let transport = t.as_str().expect("register entry must be a string");
            director.register(Arc::new(Mutex::new(BandwidthEstimator::new(
                transport,
                10_000_000_000,
            ))));
        }

        for g in f["gossips"].as_array().expect("gossips must be an array") {
            director.apply_gossip(BandwidthGossipPayload {
                peer_uhid: str_of(g, "peerUhid").to_string(),
                transport_name: str_of(g, "transport").to_string(),
                btl_bw_bps: i64_of(g, "btlBwBps"),
                rt_prop_us: i64_of(g, "rtPropUs"),
                confidence: parse_confidence(str_of(g, "confidence")),
                measured_at: SystemTime::now(),
            });
        }

        let rec = &f["recommend"];
        let result = director.recommend_transport(
            str_of(rec, "peerUhid"),
            i64_of(rec, "payloadBytes"),
        );

        let expect_el = &f["expectTransport"];
        if expect_el.is_null() {
            assert!(
                result.is_none(),
                "director[{name}] expected None, actual {result:?}"
            );
        } else {
            let expect = expect_el
                .as_str()
                .expect("expectTransport must be a string or null");
            assert_eq!(
                Some(expect),
                result.as_deref(),
                "director[{name}] transport: expected {expect:?}, actual {result:?}"
            );
        }
    }
}
