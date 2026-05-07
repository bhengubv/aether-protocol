// SPDX-License-Identifier: MIT

//! Serialisable DTOs for persisting Signal-protocol state across process
//! restarts. The on-disk format is part of the persistence contract — once
//! shipped, existing fields cannot change shape. New fields can be added at
//! the end (serde defaults missing fields).
//!
//! Mirrors the C# `StoredIdentityKeys`, `StoredSignedPreKey`,
//! `StoredSignedPreKeyHistory`, `StoredOneTimePreKey`, and the
//! `SignalSessionDto` JSON envelope.

use serde::{Deserialize, Serialize};

/// Long-term identity keys that survive across process restarts. The
/// Ed25519 keypair signs pre-key bundles; the X25519 keypair participates
/// in X3DH agreement. Both private halves stay on the node and are never
/// transmitted.
///
/// `local_uhid` is persisted alongside the keys so that `encrypt()` still
/// works after a restart without the host having to call
/// `set_local_uhid` again.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct StoredIdentityKeys {
    #[serde(rename = "ed_pk")]
    pub ed25519_private_key: Vec<u8>,
    #[serde(rename = "ed_pub")]
    pub ed25519_public_key: Vec<u8>,
    #[serde(rename = "x_pk")]
    pub x25519_private_key: Vec<u8>,
    #[serde(rename = "x_pub")]
    pub x25519_public_key: Vec<u8>,
    #[serde(rename = "uhid", default, skip_serializing_if = "Option::is_none")]
    pub local_uhid: Option<String>,
}

/// One signed pre-key entry as stored in the SPK history. Each rotation
/// generates a new entry; the active entry is the most-recently-generated
/// one (last in the history vector). Older entries are retained for the
/// configured rotation window so messages signed under a recently-rotated
/// SPK can still complete X3DH.
///
/// `generated_at_unix_ms` is serialised as Unix epoch milliseconds rather
/// than a chrono `DateTime` to keep the JSON round-trip identical to the
/// C# format and avoid pulling chrono's serde feature into the persistence
/// contract.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct StoredSignedPreKey {
    #[serde(rename = "id")]
    pub id: i32,
    #[serde(rename = "priv")]
    pub private_key: Vec<u8>,
    #[serde(rename = "pub")]
    pub public_key: Vec<u8>,
    #[serde(rename = "sig")]
    pub signature: Vec<u8>,
    #[serde(rename = "at")]
    pub generated_at_unix_ms: i64,
}

/// Full signed-pre-key history: the active SPK plus retained prior entries
/// in generation order (oldest first). Empty until the first call to
/// `generate_pre_key_bundle`.
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq, Eq)]
pub struct StoredSignedPreKeyHistory {
    #[serde(rename = "entries", default)]
    pub entries: Vec<StoredSignedPreKey>,
}

/// One one-time pre-key in the pool. Removed from the store on consumption
/// (Signal §3.3 — each OPK is consumed exactly once).
///
/// `issued` tracks whether the OPK has been handed out in a bundle but not
/// yet consumed by a responder; on hydration we use it to repopulate the
/// `available_opk_ids` queue with un-issued OPKs.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct StoredOneTimePreKey {
    #[serde(rename = "id")]
    pub id: i32,
    #[serde(rename = "priv")]
    pub private_key: Vec<u8>,
    #[serde(rename = "pub")]
    pub public_key: Vec<u8>,
    #[serde(rename = "issued", default)]
    pub issued: bool,
}

/// Serialisable snapshot of `SignalSession`. Field names match the C#
/// reference exactly so the on-disk format is cross-language readable in
/// principle (interop fixtures live under `fixtures/signal/`).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SignalSessionDto {
    #[serde(rename = "rk")]
    pub root_key: Vec<u8>,
    #[serde(rename = "cks", default, skip_serializing_if = "Option::is_none")]
    pub send_chain_key: Option<Vec<u8>>,
    #[serde(rename = "ckr", default, skip_serializing_if = "Option::is_none")]
    pub recv_chain_key: Option<Vec<u8>>,
    #[serde(rename = "ns")]
    pub send_counter: u32,
    #[serde(rename = "nr")]
    pub recv_counter: u32,
    #[serde(rename = "pn")]
    pub previous_chain_count: u32,
    #[serde(rename = "dhs_priv")]
    pub my_ephemeral_priv: Vec<u8>,
    #[serde(rename = "dhs_pub")]
    pub my_ephemeral_pub: Vec<u8>,
    #[serde(rename = "dhr", default, skip_serializing_if = "Option::is_none")]
    pub remote_ephemeral_pub: Option<Vec<u8>>,
    #[serde(rename = "mkskipped", default)]
    pub skipped_message_keys: std::collections::HashMap<String, Vec<u8>>,
    #[serde(rename = "pending_pkmsg", default)]
    pub pending_pre_key_message: bool,
    #[serde(rename = "init_ik", default)]
    pub initiator_identity_key_x25519: Vec<u8>,
    #[serde(rename = "used_spk_id", default)]
    pub used_signed_pre_key_id: i32,
    #[serde(rename = "used_opk_id", default)]
    pub used_one_time_pre_key_id: i32,
}
