// SPDX-License-Identifier: MIT

//! Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory). Rust port of
//! `AetherNet.Market.IPoVService` / `InMemoryPoVService`. Two users meet physically; their devices
//! exchange a signed token over a short-range transport (BLE/NFC/NearLink). Over time a directed trust
//! graph maps how many distinct humans have verified a profile.
//!
//! Signatures are REAL Ed25519 (ed25519-dalek via [`Ed25519SigningService`]) over the canonical token
//! body (`build_signable_token_data` = "SubjectUhid + TimestampTicks + Transport"). The single-node
//! service holds one identity key and produces both the witness and subject signatures with it; the
//! two-party mesh exchange (each side counter-signs with its own key) is `PoVTokenExchangeService`.
//!
//! SEPARATION: the resulting [`PoVScore`] is a purely local anti-Sybil routing/identity signal — it
//! attaches NO value semantics and never touches any money/reward layer.

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use crate::market::{build_signable_token_data, unix_ms_to_ticks, PoVScore, PoVToken, PoVTransportType};
use crate::security::ed25519::Ed25519SigningService;

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// The Proof-of-Vicinity trust service.
pub trait PoVService {
    /// Issue a PoV token to `subject_uhid` (both signatures from this node's identity key).
    fn issue_token(&self, witness_uhid: &str, subject_uhid: &str, transport: PoVTransportType) -> PoVToken;
    /// Record an incoming token iff it cryptographically verifies.
    fn accept_token(&self, token: &PoVToken);
    /// Return the current PoV score for a UHID.
    fn get_score(&self, uhid: &str) -> PoVScore;
    /// Whether the token is structurally and cryptographically valid.
    fn verify_token(&self, token: &PoVToken) -> bool;
    /// Reduce the witness's weighted score by 20%.
    fn report_defection(&self, witness_uhid: &str, defector_uhid: &str);
}

/// Single-node, in-memory [`PoVService`] for testing / single-node scenarios.
pub struct InMemoryPoVService {
    tokens_by_subject: Mutex<HashMap<String, Vec<PoVToken>>>,
    score_overrides: Mutex<HashMap<String, f64>>,
    private_key: Vec<u8>,
    public_key: Vec<u8>,
}

impl InMemoryPoVService {
    /// Construct a service with a fresh self-contained Ed25519 identity.
    pub fn new() -> Self {
        let (private_key, public_key) = Ed25519SigningService::generate_keypair();
        Self {
            tokens_by_subject: Mutex::new(HashMap::new()),
            score_overrides: Mutex::new(HashMap::new()),
            private_key,
            public_key,
        }
    }
}

impl Default for InMemoryPoVService {
    fn default() -> Self {
        Self::new()
    }
}

impl PoVService for InMemoryPoVService {
    fn issue_token(&self, witness_uhid: &str, subject_uhid: &str, transport: PoVTransportType) -> PoVToken {
        let ticks = unix_ms_to_ticks(now_ms());
        let signable = build_signable_token_data(subject_uhid, ticks, transport);
        // REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node).
        let sig = Ed25519SigningService::sign(&self.private_key, &signable).expect("ed25519 sign");
        PoVToken {
            witness_uhid: witness_uhid.to_string(),
            subject_uhid: subject_uhid.to_string(),
            timestamp_ticks: ticks,
            transport_used: transport,
            witness_signature: Some(sig.clone()),
            subject_signature: Some(sig),
        }
    }

    fn accept_token(&self, token: &PoVToken) {
        // Record only a token that cryptographically verifies — both signatures valid + distinct parties.
        if !self.verify_token(token) {
            return;
        }
        self.tokens_by_subject
            .lock()
            .expect("tokens poisoned")
            .entry(token.subject_uhid.clone())
            .or_default()
            .push(token.clone());
    }

    fn get_score(&self, uhid: &str) -> PoVScore {
        let tokens_guard = self.tokens_by_subject.lock().expect("tokens poisoned");
        let override_score = self.score_overrides.lock().expect("overrides poisoned").get(uhid).copied();

        let tokens = tokens_guard.get(uhid);
        if tokens.map_or(true, |t| t.is_empty()) {
            // A UHID with no inbound tokens still surfaces a stored defection override.
            return PoVScore {
                uhid: uhid.to_string(),
                unique_witnesses: 0,
                weighted_score: override_score.unwrap_or(0.0),
            };
        }

        let unique = tokens
            .unwrap()
            .iter()
            .map(|t| t.witness_uhid.as_str())
            .collect::<HashSet<_>>()
            .len();
        // Sigmoid-ish: w / (w + 1).
        let mut score = unique as f64 / (unique as f64 + 1.0);
        if let Some(o) = override_score {
            score = o;
        }
        PoVScore {
            uhid: uhid.to_string(),
            unique_witnesses: unique,
            weighted_score: score,
        }
    }

    fn verify_token(&self, token: &PoVToken) -> bool {
        let ws = token.witness_signature.as_deref();
        let ss = token.subject_signature.as_deref();
        // Structural: both parties signed, both UHIDs present, and distinct.
        if ws.map_or(true, |s| s.is_empty())
            || ss.map_or(true, |s| s.is_empty())
            || token.witness_uhid.is_empty()
            || token.subject_uhid.is_empty()
            || token.witness_uhid == token.subject_uhid
        {
            return false;
        }
        // Cryptographic: BOTH signatures valid over the canonical body.
        let signable = token.signable_data();
        Ed25519SigningService::verify(&self.public_key, &signable, ws.unwrap())
            && Ed25519SigningService::verify(&self.public_key, &signable, ss.unwrap())
    }

    fn report_defection(&self, witness_uhid: &str, _defector_uhid: &str) {
        let score = self.get_score(witness_uhid);
        self.score_overrides
            .lock()
            .expect("overrides poisoned")
            .insert(witness_uhid.to_string(), score.weighted_score * 0.8);
    }
}
