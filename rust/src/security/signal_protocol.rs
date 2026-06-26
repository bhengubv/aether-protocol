// SPDX-License-Identifier: MIT

//! Signal Protocol: X3DH session establishment + full Double Ratchet (Signal §5).
//!
//! **Key agreement (X3DH, Signal §3)** over X25519 (RFC 7748). Four DHs:
//!   * DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
//!   * DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
//!   * DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
//!   * DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key
//! Initial root key: HKDF-SHA256 over `concat(DH1||DH2||DH3||DH4)`, info `aether-x3dh-root-v1`.
//!
//! **Double Ratchet (Signal §5)**: each side maintains a current X25519
//! ratchet keypair (DHs). Whenever a peer message arrives bearing a new
//! ratchet public key (DHr), the receiver runs a DH-ratchet step:
//!   * derive a new receiving chain via `KDF_RK(RK, DH(DHs_priv, new DHr))`,
//!   * generate a fresh DHs,
//!   * derive a new sending chain via `KDF_RK(RK, DH(new DHs_priv, new DHr))`.
//! Signal-canonical X3DH↔Double-Ratchet integration: the initiator's X3DH
//! ephemeral becomes its first DHs; the responder adopts SPK as initial DHs
//! and runs a DH-ratchet step on the first received message.
//!
//! `KDF_RK` is HKDF-SHA256 with `salt = root_key`, `ikm = DH_output`, `info =
//! aether-ratchet-rk-v1`, output 64 bytes split into `(new root, new chain)`.
//!
//! **Symmetric ratchet (§5.1)**: HMAC-SHA256, single-byte domain separation
//! (0x01 -> message key, 0x02 -> next chain key).
//!
//! **Encryption**: AES-256-GCM, 12-byte nonce, 16-byte tag.
//!
//! **Identity signing**: Ed25519.

use crate::constants::{AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE, AES_KEY_SIZE};
use crate::models::{EncryptedPayload, PreKeyBundle, SignalSession};
use crate::security::dtos::{
    StoredIdentityKeys, StoredOneTimePreKey, StoredSignedPreKey, StoredSignedPreKeyHistory,
};
use crate::security::prekey_store::PreKeyStore;
use crate::security::session_store::SignalSessionStore;
use aes_gcm::{aead::Aead, Aes256Gcm, Key, KeyInit, Nonce};
use chrono::{DateTime, Utc};
use hkdf::Hkdf;
use hmac::{Hmac, Mac};
use rand::Rng;
use sha2::Sha256;
use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use x25519_dalek::{PublicKey as X25519PublicKey, StaticSecret};

type HmacSha256 = Hmac<Sha256>;

pub const MAX_SKIPPED_KEYS: usize = 1000;

pub const MESSAGE_TYPE_NORMAL: i32 = 0;
pub const MESSAGE_TYPE_PRE_KEY: i32 = 1;

const X25519_PUBLIC_KEY_SIZE: usize = 32;

/// Default size of the one-time pre-key pool. Mirrors Signal's published
/// guidance and the C# reference (`SignalProtocolService.DefaultOpkPoolSize`):
/// ~100 OPKs per device so realistic concurrent-initiator loads don't
/// collide on a single shared id.
pub const DEFAULT_OPK_POOL_SIZE: usize = 100;

/// HKDF info strings — these MUST match the C# reference exactly. Any drift
/// breaks cross-language interop (verified by
/// `fixtures/signal/expected/x3dh_basic.json`).
const HKDF_X3DH_ROOT_INFO: &[u8] = b"aether-x3dh-root-v1";
/// HKDF info string for the DH-ratchet step (Signal §5: KDF_RK). Each
/// DH-ratchet step derives a 64-byte block, split into the new root key
/// (first 32 bytes) and the new chain key (second 32 bytes).
const HKDF_RATCHET_INFO: &[u8] = b"aether-ratchet-rk-v1";

/// One signed-pre-key in the history (active or retained-prior). The
/// private half is held so that responder-side X3DH can still complete
/// when a peer presents a slightly-stale SPK during the rotation window.
#[derive(Clone)]
struct SignedPreKeyEntry {
    id: i32,
    private_key: Vec<u8>,
    public_key: Vec<u8>,
    signature: Vec<u8>,
    generated_at: DateTime<Utc>,
}

/// Configuration for periodic signed-pre-key rotation (Signal §3.3 — keys
/// SHOULD be rotated periodically).
///
/// On every `generate_pre_key_bundle` call the service checks whether the
/// active SPK is older than [`Self::rotation_interval`]; if it is, a fresh
/// SPK is generated and the old one is appended to the history. The
/// history is then trimmed to keep at most [`Self::retained_history_count`]
/// prior entries (plus the new active one). Messages signed under any
/// retained SPK still decrypt; messages signed under a pruned SPK fail.
#[derive(Clone, Copy, Debug)]
pub struct SignedPreKeyRotationOptions {
    pub rotation_interval: chrono::Duration,
    pub retained_history_count: usize,
}

impl SignedPreKeyRotationOptions {
    pub const DEFAULT_RETAINED_HISTORY_COUNT: usize = 3;

    pub fn default_options() -> Self {
        Self {
            rotation_interval: chrono::Duration::days(7),
            retained_history_count: Self::DEFAULT_RETAINED_HISTORY_COUNT,
        }
    }
}

impl Default for SignedPreKeyRotationOptions {
    fn default() -> Self {
        Self::default_options()
    }
}

/// Responder-side pre-key state. Holds the private halves of the signed
/// pre-key (current + retained-prior history) and one-time pre-keys so
/// X3DH can be computed when an initiator's PreKey message arrives.
///
/// `signed_pre_key_history` is oldest-first; the last entry is the active
/// SPK. The flat `signed_pre_key_*` denormalised fields mirror the
/// active entry and are kept in sync with `signed_pre_key_history.last()`.
///
/// One-time pre-keys are managed as a pool of `target_opk_pool_size` entries.
/// Bundle generation hands out the next-unused id from `available_opk_ids`
/// (FIFO); the OPK private + public stay in `one_time_pre_keys` until a
/// responder consumes it via X3DH, at which point it is zeroed and removed.
/// Top-up runs each time a bundle is generated so the available queue never
/// empties under steady load. This mirrors the C# reference's
/// `PreKeyState.AvailableOpkIds` queue + `OneTimePreKeys` map.
#[derive(Default)]
struct PreKeyState {
    signed_pre_key_id: i32,
    signed_pre_key_priv: Vec<u8>,
    signed_pre_key_pub: Vec<u8>,
    signed_pre_key_signature: Vec<u8>,
    /// SPK history, oldest first. The last entry is the active SPK.
    signed_pre_key_history: Vec<SignedPreKeyEntry>,
    /// id -> (priv, pub). Removed and zeroed on consumption (X3DH responder side).
    one_time_pre_keys: HashMap<i32, (Vec<u8>, Vec<u8>)>,
    /// IDs of OPKs that exist in `one_time_pre_keys` and have NOT yet been
    /// issued in any bundle. Bundle generation pops from the front (FIFO).
    /// Top-up generates new OPKs and enqueues them here.
    available_opk_ids: VecDeque<i32>,
}

/// Function returning the current time. Pluggable so tests can drive a
/// synthetic clock through SPK rotation without sleeping.
pub type NowProvider = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

pub struct SignalProtocolService {
    // Long-term identity keys — two distinct keypairs per node.
    identity_x25519_priv: [u8; 32],
    identity_x25519_pub: [u8; 32],
    ed25519_private_key: Vec<u8>,
    ed25519_public_key: Vec<u8>,

    /// Local UHID — captured when generate_pre_key_bundle is called or via
    /// set_local_uhid. Stamped on outbound EncryptedPayloads.
    local_uhid: Option<String>,

    /// Pre-key state held for responder-side X3DH. Wrapped in a Mutex so
    /// concurrent initiators (each calling `generate_pre_key_bundle` and
    /// later receiving each other's PreKey messages on this single
    /// responder) can't collide on OPK consumption. Mirrors the C#
    /// reference's `_preKeyLock` semantics.
    pre_keys: Mutex<PreKeyState>,

    /// Target size of the one-time pre-key pool. The pool is topped up to
    /// this many available (un-issued) keys on every bundle generation.
    /// Mirrors C# `SignalProtocolService.OpkPoolSize`.
    target_opk_pool_size: usize,

    sessions: HashMap<String, SignalSession>,

    /// Optional persistent session store. When non-None, hydrated on
    /// construction and saved to (best-effort) after every session
    /// mutation. Wrapped in `Arc` so a single store can be shared across
    /// services / tasks.
    session_store: Option<Arc<dyn SignalSessionStore>>,

    /// Optional persistent identity / pre-key store. When non-None,
    /// identity keys are hydrated (or generated and saved) on
    /// construction; SPK history and OPK pool are hydrated; and every
    /// rotation / bundle generation persists the new state best-effort.
    pre_key_store: Option<Arc<dyn PreKeyStore>>,

    /// Signed-pre-key rotation policy.
    rotation_options: SignedPreKeyRotationOptions,

    /// Pluggable clock — defaults to `chrono::Utc::now`.
    now_provider: NowProvider,
}

impl SignalProtocolService {
    /// Construct a Signal Protocol service with the default OPK pool size
    /// ([`DEFAULT_OPK_POOL_SIZE`] = 100) and no persistent stores.
    pub fn new() -> Self {
        Self::with_opk_pool_size(DEFAULT_OPK_POOL_SIZE)
    }

    /// Construct a Signal Protocol service with a configurable OPK pool size.
    /// The pool is topped up to `target_opk_pool_size` available (un-issued)
    /// keys on every bundle generation.
    ///
    /// Panics if `target_opk_pool_size == 0`.
    pub fn with_opk_pool_size(target_opk_pool_size: usize) -> Self {
        Self::build(target_opk_pool_size, None, None, None, None)
    }

    /// Builder entry point that wires the full set of optional dependencies
    /// — persistence stores, rotation policy, and clock. Mirrors the C#
    /// `SignalProtocolService` internal constructor that takes every
    /// optional parameter.
    pub fn builder() -> SignalProtocolServiceBuilder {
        SignalProtocolServiceBuilder::default()
    }

    fn build(
        target_opk_pool_size: usize,
        session_store: Option<Arc<dyn SignalSessionStore>>,
        pre_key_store: Option<Arc<dyn PreKeyStore>>,
        rotation_options: Option<SignedPreKeyRotationOptions>,
        now_provider: Option<NowProvider>,
    ) -> Self {
        assert!(
            target_opk_pool_size >= 1,
            "target_opk_pool_size must be >= 1 (got {})",
            target_opk_pool_size
        );

        let rotation_options = rotation_options.unwrap_or_default();
        assert!(
            rotation_options.rotation_interval > chrono::Duration::zero(),
            "rotation_interval must be > 0"
        );

        let (ed_priv, ed_pub) = crate::security::Ed25519SigningService::generate_keypair();

        // X25519 long-term identity for X3DH ECDH.
        let mut rng = rand::thread_rng();
        let x_priv_secret = StaticSecret::random_from_rng(&mut rng);
        let x_priv: [u8; 32] = x_priv_secret.to_bytes();
        let x_pub: [u8; 32] = X25519PublicKey::from(&x_priv_secret).to_bytes();

        let now_provider: NowProvider = now_provider.unwrap_or_else(|| Arc::new(Utc::now));

        let mut svc = SignalProtocolService {
            identity_x25519_priv: x_priv,
            identity_x25519_pub: x_pub,
            ed25519_private_key: ed_priv,
            ed25519_public_key: ed_pub,
            local_uhid: None,
            pre_keys: Mutex::new(PreKeyState::default()),
            target_opk_pool_size,
            sessions: HashMap::new(),
            session_store,
            pre_key_store,
            rotation_options,
            now_provider,
        };

        // Hydrate from stores if configured. Best-effort: a failure logs
        // (via eprintln in lieu of a logger trait) and continues with
        // freshly-generated material — matches the C# reference behaviour.
        svc.hydrate_from_pre_key_store();
        svc.hydrate_from_session_store();
        svc
    }

    /// Loads identity keys, SPK history, and OPK pool from `pre_key_store`
    /// if configured. If no identity is on disk, the freshly-generated one
    /// is saved. Best-effort: failures log and continue with freshly-
    /// generated material.
    fn hydrate_from_pre_key_store(&mut self) {
        let store = match self.pre_key_store.as_ref() {
            Some(s) => s.clone(),
            None => return,
        };

        // Identity.
        match futures_block_on(store.load_identity()) {
            Ok(Some(stored)) => {
                if stored.ed25519_private_key.len() == 32
                    && stored.ed25519_public_key.len() == 32
                    && stored.x25519_private_key.len() == 32
                    && stored.x25519_public_key.len() == 32
                {
                    self.ed25519_private_key = stored.ed25519_private_key;
                    self.ed25519_public_key = stored.ed25519_public_key;
                    self.identity_x25519_priv =
                        try_into_32(&stored.x25519_private_key).unwrap_or(self.identity_x25519_priv);
                    self.identity_x25519_pub =
                        try_into_32(&stored.x25519_public_key).unwrap_or(self.identity_x25519_pub);
                    if let Some(uhid) = stored.local_uhid {
                        if !uhid.is_empty() {
                            self.local_uhid = Some(uhid);
                        }
                    }
                } else {
                    eprintln!("[SignalProtocolService] stored identity has malformed key sizes; ignoring.");
                }
            }
            Ok(None) => {
                let snap = StoredIdentityKeys {
                    ed25519_private_key: self.ed25519_private_key.clone(),
                    ed25519_public_key: self.ed25519_public_key.clone(),
                    x25519_private_key: self.identity_x25519_priv.to_vec(),
                    x25519_public_key: self.identity_x25519_pub.to_vec(),
                    local_uhid: self.local_uhid.clone(),
                };
                if let Err(e) = futures_block_on(store.save_identity(&snap)) {
                    eprintln!("[SignalProtocolService] failed to save fresh identity: {}", e);
                }
            }
            Err(e) => eprintln!("[SignalProtocolService] failed to load identity: {}", e),
        }

        // SPK history.
        let history = match futures_block_on(store.load_signed_pre_keys()) {
            Ok(h) => h,
            Err(e) => {
                eprintln!("[SignalProtocolService] failed to load SPK history: {}", e);
                StoredSignedPreKeyHistory::default()
            }
        };
        if !history.entries.is_empty() {
            let mut pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
            pk.signed_pre_key_history.clear();
            let mut ordered = history.entries.clone();
            ordered.sort_by_key(|e| e.generated_at_unix_ms);
            for e in ordered {
                pk.signed_pre_key_history.push(SignedPreKeyEntry {
                    id: e.id,
                    private_key: e.private_key,
                    public_key: e.public_key,
                    signature: e.signature,
                    generated_at: from_unix_ms(e.generated_at_unix_ms),
                });
            }
            let active_snapshot = pk.signed_pre_key_history.last().cloned();
            if let Some(active) = active_snapshot {
                pk.signed_pre_key_id = active.id;
                pk.signed_pre_key_priv = active.private_key;
                pk.signed_pre_key_pub = active.public_key;
                pk.signed_pre_key_signature = active.signature;
            }
        }

        // OPK pool.
        match futures_block_on(store.load_one_time_pre_keys()) {
            Ok(opks) => {
                let mut pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
                pk.one_time_pre_keys.clear();
                pk.available_opk_ids.clear();
                let mut id_ordered: Vec<&i32> = opks.keys().collect();
                id_ordered.sort();
                for id in id_ordered {
                    let opk = &opks[id];
                    pk.one_time_pre_keys
                        .insert(opk.id, (opk.private_key.clone(), opk.public_key.clone()));
                    if !opk.issued {
                        pk.available_opk_ids.push_back(opk.id);
                    }
                }
            }
            Err(e) => eprintln!("[SignalProtocolService] failed to load OPK pool: {}", e),
        }
    }

    fn hydrate_from_session_store(&mut self) {
        let store = match self.session_store.as_ref() {
            Some(s) => s.clone(),
            None => return,
        };
        let peers = match futures_block_on(store.list_peers()) {
            Ok(p) => p,
            Err(e) => {
                eprintln!("[SignalProtocolService] failed to list peers: {}", e);
                return;
            }
        };
        for peer in peers {
            match futures_block_on(store.load(&peer)) {
                Ok(Some(mut session)) => {
                    // session.peer_uhid is empty in deserialised form — fill it.
                    session.peer_uhid = peer.clone();
                    self.sessions.insert(peer, session);
                }
                Ok(None) => {}
                Err(e) => eprintln!(
                    "[SignalProtocolService] failed to load session for {}: {}",
                    peer, e
                ),
            }
        }
    }

    /// Best-effort persist of one session. Spawned to avoid blocking the
    /// hot path; errors are logged and swallowed.
    fn try_persist_session(&self, peer_uhid: &str, session: &SignalSession) {
        let Some(store) = self.session_store.as_ref().cloned() else {
            return;
        };
        let peer = peer_uhid.to_string();
        let session = session.clone();
        spawn_persist(async move {
            if let Err(e) = store.save(&peer, &session).await {
                eprintln!("[SignalProtocolService] persist session({}): {}", peer, e);
            }
        });
    }

    fn try_persist_identity(&self) {
        let Some(store) = self.pre_key_store.as_ref().cloned() else {
            return;
        };
        let snap = StoredIdentityKeys {
            ed25519_private_key: self.ed25519_private_key.clone(),
            ed25519_public_key: self.ed25519_public_key.clone(),
            x25519_private_key: self.identity_x25519_priv.to_vec(),
            x25519_public_key: self.identity_x25519_pub.to_vec(),
            local_uhid: self.local_uhid.clone(),
        };
        spawn_persist(async move {
            if let Err(e) = store.save_identity(&snap).await {
                eprintln!("[SignalProtocolService] persist identity: {}", e);
            }
        });
    }

    /// Caller MUST hold the pre-key mutex (we snapshot under it then save
    /// off-thread so there's no risk of an awaiting `MutexGuard`).
    fn try_persist_signed_pre_keys_snapshot(&self, pk: &PreKeyState) {
        let Some(store) = self.pre_key_store.as_ref().cloned() else {
            return;
        };
        let snap = StoredSignedPreKeyHistory {
            entries: pk
                .signed_pre_key_history
                .iter()
                .map(|e| StoredSignedPreKey {
                    id: e.id,
                    private_key: e.private_key.clone(),
                    public_key: e.public_key.clone(),
                    signature: e.signature.clone(),
                    generated_at_unix_ms: to_unix_ms(e.generated_at),
                })
                .collect(),
        };
        spawn_persist(async move {
            if let Err(e) = store.save_signed_pre_keys(&snap).await {
                eprintln!("[SignalProtocolService] persist SPK history: {}", e);
            }
        });
    }

    fn try_persist_one_time_pre_keys_snapshot(&self, pk: &PreKeyState) {
        let Some(store) = self.pre_key_store.as_ref().cloned() else {
            return;
        };
        let issued: std::collections::HashSet<i32> = pk
            .one_time_pre_keys
            .keys()
            .filter(|id| !pk.available_opk_ids.contains(id))
            .copied()
            .collect();
        let snap: HashMap<i32, StoredOneTimePreKey> = pk
            .one_time_pre_keys
            .iter()
            .map(|(id, (priv_, pub_))| {
                (
                    *id,
                    StoredOneTimePreKey {
                        id: *id,
                        private_key: priv_.clone(),
                        public_key: pub_.clone(),
                        issued: issued.contains(id),
                    },
                )
            })
            .collect();
        spawn_persist(async move {
            if let Err(e) = store.save_one_time_pre_keys(&snap).await {
                eprintln!("[SignalProtocolService] persist OPK pool: {}", e);
            }
        });
    }

    fn try_consume_one_time_pre_key_persist(&self, id: i32) {
        let Some(store) = self.pre_key_store.as_ref().cloned() else {
            return;
        };
        spawn_persist(async move {
            if let Err(e) = store.consume_one_time_pre_key(id).await {
                eprintln!("[SignalProtocolService] persist OPK consume({}): {}", id, e);
            }
        });
    }

    /// Active signed-pre-key id. Test/observability hook.
    pub fn active_signed_pre_key_id(&self) -> i32 {
        self.pre_keys.lock().expect("pre-key mutex poisoned").signed_pre_key_id
    }

    /// Number of signed-pre-keys held — active + retained prior.
    pub fn signed_pre_key_history_count(&self) -> usize {
        self.pre_keys
            .lock()
            .expect("pre-key mutex poisoned")
            .signed_pre_key_history
            .len()
    }

    /// Forces a signed-pre-key rotation if the active SPK is older than
    /// [`SignedPreKeyRotationOptions::rotation_interval`]. Returns
    /// `true` iff a new SPK was generated and persisted.
    pub async fn rotate_signed_pre_key(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let now = (self.now_provider)();
        let mut rotated = false;
        let snapshot_for_save = {
            let mut pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
            let should_rotate = pk.signed_pre_key_history.is_empty()
                || now - pk.signed_pre_key_history.last().unwrap().generated_at
                    >= self.rotation_options.rotation_interval;
            if should_rotate {
                let mut rng = rand::thread_rng();
                self.append_new_signed_pre_key_no_lock(&mut pk, &mut rng, now)?;
                rotated = true;
                Some(snapshot_history(&pk))
            } else {
                None
            }
        };
        if rotated {
            self.persist_history_snapshot(snapshot_for_save);
        }
        Ok(rotated)
    }

    fn persist_history_snapshot(&self, snapshot: Option<StoredSignedPreKeyHistory>) {
        if let (Some(store), Some(snap)) = (self.pre_key_store.as_ref().cloned(), snapshot) {
            spawn_persist(async move {
                if let Err(e) = store.save_signed_pre_keys(&snap).await {
                    eprintln!("[SignalProtocolService] persist SPK history: {}", e);
                }
            });
        }
    }

    /// Generates a fresh SPK, appends it to the history as the new active
    /// entry, and trims the history to the retained-count budget. Caller
    /// MUST hold the pre-key mutex (passed in as `pk`).
    fn append_new_signed_pre_key_no_lock<R: rand::Rng + rand::CryptoRng>(
        &self,
        pk: &mut PreKeyState,
        rng: &mut R,
        now: DateTime<Utc>,
    ) -> Result<(), Box<dyn std::error::Error>> {
        let spk_secret = StaticSecret::random_from_rng(&mut *rng);
        let spk_priv: [u8; 32] = spk_secret.to_bytes();
        let spk_pub: [u8; 32] = X25519PublicKey::from(&spk_secret).to_bytes();
        let id = rng.gen_range(1..i32::MAX);
        let signature =
            crate::security::Ed25519SigningService::sign(&self.ed25519_private_key, &spk_pub)?;

        pk.signed_pre_key_history.push(SignedPreKeyEntry {
            id,
            private_key: spk_priv.to_vec(),
            public_key: spk_pub.to_vec(),
            signature: signature.clone(),
            generated_at: now,
        });

        let max_entries = 1 + self.rotation_options.retained_history_count;
        while pk.signed_pre_key_history.len() > max_entries {
            // Best-effort scrub of the pruned private half before drop.
            let mut pruned = pk.signed_pre_key_history.remove(0);
            zero(&mut pruned.private_key);
        }

        let active = pk
            .signed_pre_key_history
            .last()
            .expect("just pushed an entry");
        pk.signed_pre_key_id = active.id;
        pk.signed_pre_key_priv = active.private_key.clone();
        pk.signed_pre_key_pub = active.public_key.clone();
        pk.signed_pre_key_signature = active.signature.clone();
        Ok(())
    }

    /// Configured target size of the OPK pool.
    pub fn opk_pool_size(&self) -> usize {
        self.target_opk_pool_size
    }

    /// Snapshot of the OPK pool. Returns `(held, available)` where:
    ///   * `held` is the total number of OPKs (issued + un-issued) currently
    ///     resident on this responder. Drops as responders consume issued
    ///     OPKs via X3DH.
    ///   * `available` is the number of OPKs in `available_opk_ids` —
    ///     un-issued, ready to be handed out in the next bundle. Drops as
    ///     bundles are issued; tops back up on the next bundle generation.
    ///
    /// Mirrors C# `HeldOneTimePreKeyCount` + `AvailableOneTimePreKeyCount`.
    pub fn pre_key_pool_status(&self) -> (usize, usize) {
        let pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
        (pk.one_time_pre_keys.len(), pk.available_opk_ids.len())
    }

    /// Sets the local node's UHID. Required before any encrypt() call.
    pub fn set_local_uhid(&mut self, uhid: &str) {
        let changed = self.local_uhid.as_deref() != Some(uhid);
        self.local_uhid = Some(uhid.to_string());
        if changed {
            self.try_persist_identity();
        }
    }

    pub fn has_session(&self, peer_uhid: &str) -> bool {
        self.sessions.contains_key(peer_uhid)
    }

    pub fn get_public_key(&self) -> Vec<u8> {
        self.ed25519_public_key.clone()
    }

    pub fn get_x25519_public_key(&self) -> Vec<u8> {
        self.identity_x25519_pub.to_vec()
    }

    /// Generates a pre-key bundle. Retains the SPK + OPK private halves for
    /// responder-side X3DH on this node.
    ///
    /// OPK pool semantics (mirroring C#):
    ///   * The first call seeds the pool to `target_opk_pool_size` un-issued
    ///     OPKs and dequeues one for the bundle.
    ///   * Subsequent calls top the pool back up to `target_opk_pool_size`
    ///     un-issued OPKs (replacing keys consumed by responders since the
    ///     last call), then dequeue one.
    /// The SPK is generated lazily on the first bundle call and rotated when
    /// its age exceeds [`SignedPreKeyRotationOptions::rotation_interval`].
    /// The retained-history budget keeps prior SPKs available so in-flight
    /// messages signed under a recently-rotated SPK still complete X3DH.
    pub fn generate_pre_key_bundle(
        &mut self,
        local_uhid: &str,
    ) -> Result<PreKeyBundle, Box<dyn std::error::Error>> {
        let uhid_changed = self.local_uhid.as_deref() != Some(local_uhid);
        self.local_uhid = Some(local_uhid.to_string());
        if uhid_changed {
            self.try_persist_identity();
        }

        let mut rng = rand::thread_rng();
        let now = (self.now_provider)();

        let (
            signed_pre_key_id,
            spk_pub_bytes,
            signature,
            pre_key_id,
            otpk_pub,
            history_mutated,
        ) = {
            let mut pk = self.pre_keys.lock().expect("pre-key mutex poisoned");

            // SPK: lazy init or rotation when active SPK age exceeds
            // rotation_interval. Append-and-prune updates the history list
            // and the denormalised active fields.
            let mut history_mutated = false;
            if pk.signed_pre_key_history.is_empty() {
                self.append_new_signed_pre_key_no_lock(&mut pk, &mut rng, now)?;
                history_mutated = true;
            } else {
                let age =
                    now - pk.signed_pre_key_history.last().unwrap().generated_at;
                if age >= self.rotation_options.rotation_interval {
                    self.append_new_signed_pre_key_no_lock(&mut pk, &mut rng, now)?;
                    history_mutated = true;
                }
            }
            let active = pk
                .signed_pre_key_history
                .last()
                .expect("active SPK present after init/rotate");
            let signed_pre_key_id = active.id;
            let spk_pub = active.public_key.clone();
            let signature = active.signature.clone();

            // OPK: top up + dequeue under same mutex hold to keep the
            // original concurrency guarantee.
            top_up_opk_pool(&mut pk, self.target_opk_pool_size, &mut rng)?;
            let id = pk
                .available_opk_ids
                .pop_front()
                .expect("pool top-up guarantees at least one available id");
            let pub_bytes = pk
                .one_time_pre_keys
                .get(&id)
                .expect("available id MUST have a corresponding entry")
                .1
                .clone();
            (
                signed_pre_key_id,
                spk_pub,
                signature,
                id,
                pub_bytes,
                history_mutated,
            )
        };

        // Persistence: snapshot under a brief re-acquired lock then
        // fire-and-forget save.
        {
            let pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
            if history_mutated {
                self.try_persist_signed_pre_keys_snapshot(&pk);
            }
            self.try_persist_one_time_pre_keys_snapshot(&pk);
        }

        Ok(PreKeyBundle::new(
            local_uhid.to_string(),
            self.ed25519_public_key.clone(),
            self.identity_x25519_pub.to_vec(),
            pre_key_id,
            otpk_pub,
            signed_pre_key_id,
            spk_pub_bytes,
            signature,
        ))
    }

    /// Establishes initiator-side session via X3DH (Signal §3.3): generates
    /// a fresh ephemeral X25519 keypair, runs the four DHs, derives the root
    /// key, and primes the Double Ratchet by adopting the X3DH ephemeral as
    /// the initiator's first DHs. The peer's signed pre-key becomes the
    /// initial DHr. CKs is computed lazily on first send.
    pub fn process_pre_key_bundle(
        &mut self,
        bundle: &PreKeyBundle,
    ) -> Result<(), Box<dyn std::error::Error>> {
        if !crate::security::Ed25519SigningService::verify(
            &bundle.identity_key,
            &bundle.signed_pre_key,
            &bundle.signed_pre_key_signature,
        ) {
            return Err("Signed pre-key signature verification failed".into());
        }
        if bundle.identity_key_x25519.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed X25519 identity key (length {})",
                bundle.identity_key_x25519.len()
            )
            .into());
        }
        if bundle.signed_pre_key.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed signed pre-key (length {})",
                bundle.signed_pre_key.len()
            )
            .into());
        }
        if bundle.pre_key.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed one-time pre-key (length {})",
                bundle.pre_key.len()
            )
            .into());
        }

        // Fresh ephemeral X25519 keypair, generated per-session.
        let mut rng = rand::thread_rng();
        let ek_secret = StaticSecret::random_from_rng(&mut rng);
        let ek_priv: [u8; 32] = ek_secret.to_bytes();
        let ek_pub: [u8; 32] = X25519PublicKey::from(&ek_secret).to_bytes();

        // X3DH 4-DH key agreement (initiator side).
        let dh1 = x25519_agree(&self.identity_x25519_priv, &bundle.signed_pre_key)?;
        let dh2 = x25519_agree(&ek_priv, &bundle.identity_key_x25519)?;
        let dh3 = x25519_agree(&ek_priv, &bundle.signed_pre_key)?;
        let dh4 = x25519_agree(&ek_priv, &bundle.pre_key)?;

        let mut shared = Vec::with_capacity(128);
        shared.extend_from_slice(&dh1);
        shared.extend_from_slice(&dh2);
        shared.extend_from_slice(&dh3);
        shared.extend_from_slice(&dh4);

        let root_key = hkdf32(&shared, HKDF_X3DH_ROOT_INFO)?;

        // Signal-canonical X3DH↔Double-Ratchet integration: the initiator's
        // X3DH ephemeral becomes its first DHs. The peer's signed pre-key is
        // the initial DHr. CKs is computed lazily on first send
        // (dh_ratchet_send_only).
        let mut session = SignalSession::new(bundle.uhid.clone(), bundle.identity_key.clone());
        session.root_key = root_key;
        session.send_chain_key = None; // computed on first send
        session.recv_chain_key = None; // computed on first DH-ratchet receive
        session.my_ephemeral_priv = ek_priv.to_vec();
        session.my_ephemeral_pub = ek_pub.to_vec();
        session.remote_ephemeral_pub = Some(bundle.signed_pre_key.clone());
        session.pending_pre_key_message = true;
        session.initiator_identity_key_x25519 = self.identity_x25519_pub.to_vec();
        session.initiator_ephemeral_key_x25519 = ek_pub.to_vec();
        session.used_signed_pre_key_id = bundle.signed_pre_key_id;
        session.used_one_time_pre_key_id = bundle.pre_key_id;

        // Persist before insert so the post-restart hydrated state matches.
        self.try_persist_session(&bundle.uhid, &session);
        self.sessions.insert(bundle.uhid.clone(), session);

        // Best-effort scrubbing.
        zero(&mut shared);
        Ok(())
    }

    pub fn encrypt(
        &mut self,
        peer_uhid: &str,
        plaintext: &[u8],
    ) -> Result<EncryptedPayload, Box<dyn std::error::Error>> {
        let sender = self
            .local_uhid
            .clone()
            .ok_or("Local UHID is not set. Call generate_pre_key_bundle or set_local_uhid first.")?;

        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        // Lazy CKs initialization for the initiator's first send: X3DH placed
        // DHs and DHr but did not derive CKs. Defer until first send so a
        // never-used session doesn't cost an extra KDF.
        if session.send_chain_key.is_none() {
            let remote_pub = session
                .remote_ephemeral_pub
                .as_ref()
                .ok_or("Cannot derive sending chain: peer's ratchet public key is unknown.")?
                .clone();
            dh_ratchet_send_only(session, &remote_pub)?;
        }

        let send_ck = session
            .send_chain_key
            .as_ref()
            .expect("send_chain_key is Some after lazy init");
        let (new_chain, message_key) = ratchet_chain_key(send_ck);
        session.send_chain_key = Some(new_chain);

        let mut rng = rand::thread_rng();
        let mut nonce_bytes = [0u8; AES_GCM_NONCE_SIZE];
        rng.fill(&mut nonce_bytes);

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&nonce_bytes);
        let ciphertext = cipher
            .encrypt(nonce, plaintext)
            .map_err(|e| format!("Encryption failed: {}", e))?;

        let counter = session.send_counter;
        session.send_counter += 1;

        let ratchet_pub = session.my_ephemeral_pub.clone();
        let previous_chain_count = session.previous_chain_count;

        let mut payload = EncryptedPayload::new(
            ciphertext,
            nonce_bytes.to_vec(),
            MESSAGE_TYPE_NORMAL,
            sender,
            counter,
        );
        // Double-Ratchet header on EVERY message — drives the receiver-side
        // DH-ratchet step when the value changes.
        payload.sender_ephemeral_key_x25519 = Some(ratchet_pub.clone());
        payload.previous_chain_count = previous_chain_count;

        if session.pending_pre_key_message {
            payload.message_type = MESSAGE_TYPE_PRE_KEY;
            payload.initiator_identity_key_x25519 =
                Some(session.initiator_identity_key_x25519.clone());
            // Backward-compat alias: equals sender_ephemeral_key_x25519 on the
            // first message because the initiator's X3DH ephemeral is also
            // its first DH-ratchet pubkey.
            payload.initiator_ephemeral_key_x25519 = Some(ratchet_pub);
            payload.used_signed_pre_key_id = session.used_signed_pre_key_id;
            payload.used_one_time_pre_key_id = session.used_one_time_pre_key_id;
            session.pending_pre_key_message = false;
        }

        let mut mk_copy = message_key;
        zero(&mut mk_copy);

        // Persist the post-mutation session — fire-and-forget so the
        // encrypt hot path is not blocked.
        let session_snapshot = self
            .sessions
            .get(peer_uhid)
            .expect("session present after encrypt mutation")
            .clone();
        self.try_persist_session(peer_uhid, &session_snapshot);

        Ok(payload)
    }

    pub fn decrypt(
        &mut self,
        peer_uhid: &str,
        payload: &EncryptedPayload,
    ) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        // Backward-compat: every Double-Ratchet message carries the sender's
        // current ratchet public key. Fall back to
        // initiator_ephemeral_key_x25519 for legacy PreKey messages.
        let sender_ratchet_pub = payload
            .sender_ephemeral_key_x25519
            .as_ref()
            .or(payload.initiator_ephemeral_key_x25519.as_ref())
            .ok_or("Message missing sender_ephemeral_key_x25519 — required for the Double Ratchet.")?
            .clone();

        if payload.message_type == MESSAGE_TYPE_PRE_KEY {
            let ik = payload
                .initiator_identity_key_x25519
                .as_ref()
                .ok_or("PreKey message missing initiator identity key")?
                .clone();
            // The initiator's X3DH ephemeral key is the same value carried
            // in sender_ephemeral_key_x25519 (and initiator_ephemeral_key_x25519
            // for backward compatibility) — Signal-canonical integration.
            self.establish_responder_session_full(
                peer_uhid,
                &ik,
                &sender_ratchet_pub,
                payload.used_signed_pre_key_id,
                payload.used_one_time_pre_key_id,
            )?;
        }

        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        if payload.ciphertext.len() < AES_GCM_TAG_SIZE {
            return Err("Ciphertext too short".into());
        }

        // DH-ratchet step? Triggered when the peer's ratchet public key
        // changes (or is being seen for the first time after responder X3DH).
        let needs_dh_ratchet = match session.remote_ephemeral_pub.as_ref() {
            None => true,
            Some(current) => !constant_time_eq(&sender_ratchet_pub, current),
        };
        if needs_dh_ratchet {
            // Save any unread keys on the OLD receive chain (keyed by the
            // OLD remote_ephemeral_pub) before swapping to the new chain.
            skip_message_keys(session, payload.previous_chain_count)?;
            dh_ratchet_receive(session, &sender_ratchet_pub)?;
        }

        // Skipped key cached for this (DHr_pub, counter)?
        let skipped_key_id = skipped_key(&sender_ratchet_pub, payload.counter);
        let message_key = if let Some(cached) = session.skipped_message_keys.remove(&skipped_key_id)
        {
            cached
        } else {
            if session.recv_chain_key.is_none() {
                return Err("Receive chain not initialized (DH-ratchet step missing).".into());
            }

            let gap = payload.counter as i64 - session.recv_counter as i64;
            if gap > MAX_SKIPPED_KEYS as i64 {
                return Err(format!(
                    "Message counter gap ({}) exceeds maximum ({}). Session must be re-established.",
                    gap, MAX_SKIPPED_KEYS
                )
                .into());
            }

            // Skip ahead, caching intermediate keys keyed by (DHr_pub, counter).
            while session.recv_counter < payload.counter {
                let recv_ck = session
                    .recv_chain_key
                    .as_ref()
                    .expect("recv_chain_key remains Some inside the skip loop");
                let (nc, sk) = ratchet_chain_key(recv_ck);
                session.recv_chain_key = Some(nc);
                let key = skipped_key(&sender_ratchet_pub, session.recv_counter);
                session.skipped_message_keys.insert(key, sk);
                session.recv_counter += 1;
            }

            let recv_ck = session
                .recv_chain_key
                .as_ref()
                .expect("recv_chain_key remains Some after skip loop");
            let (nc, mk) = ratchet_chain_key(recv_ck);
            session.recv_chain_key = Some(nc);
            session.recv_counter += 1;
            mk
        };

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&payload.nonce);
        let plaintext = cipher
            .decrypt(nonce, payload.ciphertext.as_ref())
            .map_err(|e| format!("Decryption failed: {}", e))?;

        let mut mk_copy = message_key;
        zero(&mut mk_copy);

        // Persist the post-mutation session.
        let session_snapshot = self
            .sessions
            .get(peer_uhid)
            .expect("session present after decrypt mutation")
            .clone();
        self.try_persist_session(peer_uhid, &session_snapshot);

        Ok(plaintext)
    }

    /// Mirrors the initiator's 4 X3DH DHs to derive the same root key,
    /// using `initiator_ek` (which on the first PreKey message is the
    /// initiator's X3DH ephemeral, also its first DH-ratchet pubkey).
    /// Adopts SPK as the responder's initial DHs and leaves DHr=None so the
    /// caller's subsequent `dh_ratchet_receive` step re-keys both chains.
    /// Consumes (and zeros) the one-time pre-key.
    fn establish_responder_session_full(
        &mut self,
        peer_uhid: &str,
        initiator_ik: &[u8],
        initiator_ek: &[u8],
        used_signed_pre_key_id: i32,
        used_one_time_pre_key_id: i32,
    ) -> Result<(), Box<dyn std::error::Error>> {
        if initiator_ik.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!("Initiator IK_X25519 wrong size: {}", initiator_ik.len()).into());
        }
        if initiator_ek.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!("Initiator EK_X25519 wrong size: {}", initiator_ek.len()).into());
        }

        // Atomically validate SPK id, consume the OPK, and snapshot the
        // private halves we need for the four DHs. Holding the mutex for the
        // full snapshot is what guarantees two concurrent initiators racing
        // on the same OPK id can never both succeed: the second `.remove()`
        // returns None and the second initiator gets a clean error.
        //
        // SPK lookup walks the FULL retained history so messages signed
        // under a recently-rotated SPK still complete X3DH during the
        // rotation window. A pruned SPK fails outright.
        let (spk_priv, spk_pub, otpk_priv) = {
            let mut pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
            let spk_entry = pk
                .signed_pre_key_history
                .iter()
                .rev()
                .find(|e| e.id == used_signed_pre_key_id)
                .cloned()
                .ok_or_else(|| {
                    format!(
                        "PreKey message references signed pre-key id {} which is not held \
                         (rotated out or never generated)",
                        used_signed_pre_key_id
                    )
                })?;
            let otpk = pk
                .one_time_pre_keys
                .remove(&used_one_time_pre_key_id)
                .ok_or_else(|| {
                    format!(
                    "PreKey message references one-time pre-key id {} which is not held (already consumed?)",
                    used_one_time_pre_key_id
                )
                })?;
            // Drop the id from the available queue too if it somehow lingered
            // there (defensive — `.remove` on `one_time_pre_keys` is the
            // canonical consume signal but a stale entry in `available_opk_ids`
            // would re-issue a defunct id on the next bundle).
            pk.available_opk_ids
                .retain(|&id| id != used_one_time_pre_key_id);
            (spk_entry.private_key, spk_entry.public_key, otpk.0)
        };

        // Mirror of initiator's 4 DHs (X25519 ECDH is commutative). Note: spk
        // and otpk priv halves are now local snapshots — the mutex was
        // released after the snapshot above so concurrent bundle generation
        // can proceed.
        let dh1 = x25519_agree(&spk_priv, initiator_ik)?;
        let dh2 = x25519_agree(&self.identity_x25519_priv, initiator_ek)?;
        let dh3 = x25519_agree(&spk_priv, initiator_ek)?;
        let dh4 = x25519_agree(&otpk_priv, initiator_ek)?;

        let mut shared = Vec::with_capacity(128);
        shared.extend_from_slice(&dh1);
        shared.extend_from_slice(&dh2);
        shared.extend_from_slice(&dh3);
        shared.extend_from_slice(&dh4);

        let root_key = hkdf32(&shared, HKDF_X3DH_ROOT_INFO)?;

        // Adopt the *consumed* SPK (priv+pub) as initial DHs — must match
        // the SPK the initiator referenced, which may not be the active
        // SPK any longer. The DH-ratchet step in the decrypt() caller will
        // rotate it to a fresh keypair.
        let mut session = SignalSession::new(peer_uhid.to_string(), Vec::new());
        session.root_key = root_key;
        session.send_chain_key = None; // derived by DH-ratchet
        session.recv_chain_key = None; // derived by DH-ratchet
        session.my_ephemeral_priv = spk_priv.clone();
        session.my_ephemeral_pub = spk_pub;
        session.remote_ephemeral_pub = None; // forces DH-ratchet on first decrypt
        session.pending_pre_key_message = false;

        // Persist before inserting so the post-restart hydrated session
        // reflects the just-consumed OPK.
        self.try_persist_session(peer_uhid, &session);
        self.sessions.insert(peer_uhid.to_string(), session);
        // Persist OPK consumption + the post-consume pool snapshot.
        self.try_consume_one_time_pre_key_persist(used_one_time_pre_key_id);
        {
            let pk = self.pre_keys.lock().expect("pre-key mutex poisoned");
            self.try_persist_one_time_pre_keys_snapshot(&pk);
        }

        // Consume one-time pre-key — already removed under mutex above; zero
        // the local priv copy. Also zero the snapshotted SPK priv (the
        // canonical SPK priv is still held in self.pre_keys until rotation).
        let mut otpk_copy = otpk_priv;
        zero(&mut otpk_copy);
        let mut spk_priv_copy = spk_priv;
        zero(&mut spk_priv_copy);
        zero(&mut shared);
        Ok(())
    }
}

/// Builder for [`SignalProtocolService`]. Wires the optional persistence
/// stores, rotation policy, and clock without making the bare constructor
/// signature explode.
#[derive(Default)]
pub struct SignalProtocolServiceBuilder {
    target_opk_pool_size: Option<usize>,
    session_store: Option<Arc<dyn SignalSessionStore>>,
    pre_key_store: Option<Arc<dyn PreKeyStore>>,
    rotation_options: Option<SignedPreKeyRotationOptions>,
    now_provider: Option<NowProvider>,
}

impl SignalProtocolServiceBuilder {
    pub fn with_opk_pool_size(mut self, size: usize) -> Self {
        self.target_opk_pool_size = Some(size);
        self
    }

    pub fn with_session_store(mut self, store: Arc<dyn SignalSessionStore>) -> Self {
        self.session_store = Some(store);
        self
    }

    pub fn with_prekey_store(mut self, store: Arc<dyn PreKeyStore>) -> Self {
        self.pre_key_store = Some(store);
        self
    }

    pub fn with_rotation_options(mut self, options: SignedPreKeyRotationOptions) -> Self {
        self.rotation_options = Some(options);
        self
    }

    pub fn with_now_provider(mut self, provider: NowProvider) -> Self {
        self.now_provider = Some(provider);
        self
    }

    pub fn build(self) -> SignalProtocolService {
        SignalProtocolService::build(
            self.target_opk_pool_size.unwrap_or(DEFAULT_OPK_POOL_SIZE),
            self.session_store,
            self.pre_key_store,
            self.rotation_options,
            self.now_provider,
        )
    }
}

/// Synchronously block on a future. The hydration path runs at construction
/// time on whatever thread the caller holds, which may or may not be inside
/// a tokio runtime. We try the current runtime handle first; if there isn't
/// one, we spin up a temporary single-threaded runtime.
///
/// This is only used for store hydration during construction — the
/// hot-path persistence calls dispatch via [`spawn_persist`] instead.
fn futures_block_on<F: std::future::Future>(future: F) -> F::Output {
    match tokio::runtime::Handle::try_current() {
        Ok(handle) => {
            // Inside an async context — block_on directly is unsafe (it
            // would deadlock the executor). Use `block_in_place` to move
            // the call onto a blocking thread, OR fall back to spawning a
            // temporary runtime when block_in_place isn't available
            // (current_thread runtimes).
            tokio::task::block_in_place(|| handle.block_on(future))
        }
        Err(_) => {
            // No tokio context — spin up a tiny one for this call.
            let rt = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .expect("failed to construct ad-hoc tokio runtime for hydration");
            rt.block_on(future)
        }
    }
}

/// Spawn a fire-and-forget persistence future. Uses the ambient tokio
/// runtime if one is present; otherwise runs the future to completion
/// synchronously on the calling thread (tests + simple non-async hosts).
fn spawn_persist<F>(future: F)
where
    F: std::future::Future<Output = ()> + Send + 'static,
{
    match tokio::runtime::Handle::try_current() {
        Ok(handle) => {
            handle.spawn(future);
        }
        Err(_) => {
            // No runtime — run synchronously. Slower but always correct.
            let rt = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .expect("failed to construct ad-hoc tokio runtime for persist");
            rt.block_on(future);
        }
    }
}

fn snapshot_history(pk: &PreKeyState) -> StoredSignedPreKeyHistory {
    StoredSignedPreKeyHistory {
        entries: pk
            .signed_pre_key_history
            .iter()
            .map(|e| StoredSignedPreKey {
                id: e.id,
                private_key: e.private_key.clone(),
                public_key: e.public_key.clone(),
                signature: e.signature.clone(),
                generated_at_unix_ms: to_unix_ms(e.generated_at),
            })
            .collect(),
    }
}

fn to_unix_ms(t: DateTime<Utc>) -> i64 {
    t.timestamp_millis()
}

fn from_unix_ms(ms: i64) -> DateTime<Utc> {
    DateTime::<Utc>::from_timestamp_millis(ms).unwrap_or_else(Utc::now)
}

fn try_into_32(v: &[u8]) -> Option<[u8; 32]> {
    if v.len() != 32 {
        return None;
    }
    let mut a = [0u8; 32];
    a.copy_from_slice(v);
    Some(a)
}

/// Tops the OPK pool up to `target_size` available (un-issued) keys. Caller
/// MUST hold the pre-key mutex (i.e., `pk` is borrowed from a held lock).
///
/// Generates a fresh X25519 keypair per missing slot, assigns it a random
/// non-colliding id, and enqueues the id in `available_opk_ids`. Idempotent
/// — safe to call repeatedly.
fn top_up_opk_pool<R: rand::Rng + rand::CryptoRng>(
    pk: &mut PreKeyState,
    target_size: usize,
    rng: &mut R,
) -> Result<(), Box<dyn std::error::Error>> {
    while pk.available_opk_ids.len() < target_size {
        let secret = StaticSecret::random_from_rng(&mut *rng);
        let priv_bytes: [u8; 32] = secret.to_bytes();
        let pub_bytes: [u8; 32] = X25519PublicKey::from(&secret).to_bytes();

        // Choose a non-colliding id. RandomNumberGenerator has a 2^31 range;
        // collisions in a 100-element pool are statistically negligible but
        // we still guard explicitly to match the C# reference.
        let mut attempts = 0;
        let id = loop {
            let candidate = rng.gen_range(1..i32::MAX);
            if !pk.one_time_pre_keys.contains_key(&candidate) {
                break candidate;
            }
            attempts += 1;
            if attempts > 64 {
                return Err(
                    "Could not allocate a non-colliding OPK id after 64 attempts. \
                     Pool exhaustion or RNG failure."
                        .into(),
                );
            }
        };

        pk.one_time_pre_keys
            .insert(id, (priv_bytes.to_vec(), pub_bytes.to_vec()));
        pk.available_opk_ids.push_back(id);
    }
    Ok(())
}

impl Default for SignalProtocolService {
    fn default() -> Self {
        Self::new()
    }
}

/// X25519 ECDH. Returns 32 raw shared-secret bytes.
///
/// RFC 7748 §6.1: detect the all-zero output (small-subgroup attack via a
/// low-order remote public key).
fn x25519_agree(local_priv: &[u8], remote_pub: &[u8]) -> Result<[u8; 32], Box<dyn std::error::Error>> {
    if local_priv.len() != X25519_PUBLIC_KEY_SIZE {
        return Err(format!("X25519 private key must be 32 bytes, got {}", local_priv.len()).into());
    }
    if remote_pub.len() != X25519_PUBLIC_KEY_SIZE {
        return Err(format!("X25519 public key must be 32 bytes, got {}", remote_pub.len()).into());
    }
    let mut priv_arr = [0u8; 32];
    priv_arr.copy_from_slice(local_priv);
    let mut pub_arr = [0u8; 32];
    pub_arr.copy_from_slice(remote_pub);

    let secret = StaticSecret::from(priv_arr);
    let pub_key = X25519PublicKey::from(pub_arr);
    let shared = secret.diffie_hellman(&pub_key);
    let bytes = shared.to_bytes();

    let mut acc: u8 = 0;
    for &b in &bytes {
        acc |= b;
    }
    if acc == 0 {
        return Err("X25519 produced an all-zero shared secret (low-order point)".into());
    }
    Ok(bytes)
}

/// HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# `HKDF.DeriveKey`.
fn hkdf32(ikm: &[u8], info: &[u8]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let hk = Hkdf::<Sha256>::new(None, ikm);
    let mut out = vec![0u8; AES_KEY_SIZE];
    hk.expand(info, &mut out)
        .map_err(|e| format!("HKDF expand failed: {}", e))?;
    Ok(out)
}

/// KDF_RK per Signal §5.2: derives a new root key + new chain key from the
/// current root key and a fresh DH output. HKDF-SHA256 over 64 bytes;
/// `salt = root_key`, `ikm = dh_output`, `info = aether-ratchet-rk-v1`. First
/// 32 = new root, second 32 = new chain key.
fn kdf_rk(
    root_key: &[u8],
    dh_output: &[u8],
) -> Result<(Vec<u8>, Vec<u8>), Box<dyn std::error::Error>> {
    let hk = Hkdf::<Sha256>::new(Some(root_key), dh_output);
    let mut out = vec![0u8; 64];
    hk.expand(HKDF_RATCHET_INFO, &mut out)
        .map_err(|e| format!("KDF_RK HKDF expand failed: {}", e))?;
    let new_root = out[0..32].to_vec();
    let new_chain = out[32..64].to_vec();
    zero(&mut out);
    Ok((new_root, new_chain))
}

/// Single Double-Ratchet symmetric step (Signal §5.1).
///
///   message_key   = HMAC-SHA256(chain_key, 0x01)
///   new_chain_key = HMAC-SHA256(chain_key, 0x02)
fn ratchet_chain_key(chain_key: &[u8]) -> (Vec<u8>, Vec<u8>) {
    // hmac 0.13 moved `new_from_slice` to the `KeyInit` trait (from `digest`).
    use hmac::digest::KeyInit;
    let mut mac1 = <HmacSha256 as KeyInit>::new_from_slice(chain_key)
        .expect("HMAC keys are arbitrary-length");
    mac1.update(&[0x01]);
    let message_key = mac1.finalize().into_bytes().to_vec();

    let mut mac2 = <HmacSha256 as KeyInit>::new_from_slice(chain_key)
        .expect("HMAC keys are arbitrary-length");
    mac2.update(&[0x02]);
    let new_chain = mac2.finalize().into_bytes().to_vec();
    (new_chain, message_key)
}

/// Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
/// derives a new receiving chain via `KDF_RK(RK, DH(DHs, DHr))`, generates a
/// fresh DHs, and derives a new sending chain via
/// `KDF_RK(RK, DH(new DHs, DHr))`.
fn dh_ratchet_receive(
    session: &mut SignalSession,
    new_remote_ephemeral_pub: &[u8],
) -> Result<(), Box<dyn std::error::Error>> {
    // Save send-counter as PN so the peer can compute skipped keys across
    // the ratchet boundary on subsequent decrypts.
    session.previous_chain_count = session.send_counter;
    session.send_counter = 0;
    session.recv_counter = 0;
    session.remote_ephemeral_pub = Some(new_remote_ephemeral_pub.to_vec());

    // Step 1: derive new receiving chain from current DHs · new DHr.
    let dh1 = x25519_agree(&session.my_ephemeral_priv, new_remote_ephemeral_pub)?;
    let (new_root, new_ckr) = kdf_rk(&session.root_key, &dh1)?;
    session.root_key = new_root;
    session.recv_chain_key = Some(new_ckr);

    // Step 2: rotate DHs to a fresh keypair, derive new sending chain from
    // new DHs · new DHr.
    zero(&mut session.my_ephemeral_priv);
    let mut rng = rand::thread_rng();
    let new_secret = StaticSecret::random_from_rng(&mut rng);
    let new_priv: [u8; 32] = new_secret.to_bytes();
    let new_pub: [u8; 32] = X25519PublicKey::from(&new_secret).to_bytes();
    session.my_ephemeral_priv = new_priv.to_vec();
    session.my_ephemeral_pub = new_pub.to_vec();

    let dh2 = x25519_agree(&session.my_ephemeral_priv, new_remote_ephemeral_pub)?;
    let (new_root2, new_cks) = kdf_rk(&session.root_key, &dh2)?;
    session.root_key = new_root2;
    session.send_chain_key = Some(new_cks);
    Ok(())
}

/// Lazy half-ratchet for the very first send on a freshly-established
/// initiator session. The initiator's DHs and DHr are already set (X3DH
/// placed them); we just need to derive the sending chain. Does NOT rotate
/// DHs — only on a true DH-ratchet (i.e., on receive).
fn dh_ratchet_send_only(
    session: &mut SignalSession,
    remote_pub: &[u8],
) -> Result<(), Box<dyn std::error::Error>> {
    let dh = x25519_agree(&session.my_ephemeral_priv, remote_pub)?;
    let (new_root, new_cks) = kdf_rk(&session.root_key, &dh)?;
    session.root_key = new_root;
    session.send_chain_key = Some(new_cks);
    Ok(())
}

/// Saves any unread message keys on the current receive chain up to `until`,
/// keyed by (current DHr, counter). Bounded by `MAX_SKIPPED_KEYS`.
fn skip_message_keys(
    session: &mut SignalSession,
    until: u32,
) -> Result<(), Box<dyn std::error::Error>> {
    let dhr_pub = match session.remote_ephemeral_pub.as_ref() {
        Some(p) => p.clone(),
        None => return Ok(()), // no chain to skip on yet
    };
    if session.recv_chain_key.is_none() {
        return Ok(());
    }
    if until <= session.recv_counter {
        return Ok(());
    }
    if (until - session.recv_counter) as usize > MAX_SKIPPED_KEYS {
        return Err(format!(
            "Skipped-key request exceeds maximum ({}). Session must be re-established.",
            MAX_SKIPPED_KEYS
        )
        .into());
    }

    while session.recv_counter < until {
        let recv_ck = session
            .recv_chain_key
            .as_ref()
            .expect("recv_chain_key Some inside skip loop");
        let (nc, sk) = ratchet_chain_key(recv_ck);
        session.recv_chain_key = Some(nc);
        let key = skipped_key(&dhr_pub, session.recv_counter);
        session.skipped_message_keys.insert(key, sk);
        session.recv_counter += 1;
    }
    Ok(())
}

/// Composite key for the skipped-message-key cache: `Hex(DHr_pub):counter`.
/// Matches the C# reference exactly so (in principle) sessions could be
/// serialized between languages.
fn skipped_key(dhr_pub: &[u8], counter: u32) -> String {
    let mut s = String::with_capacity(dhr_pub.len() * 2 + 12);
    for b in dhr_pub {
        s.push_str(&format!("{:02X}", b));
    }
    s.push(':');
    s.push_str(&counter.to_string());
    s
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut acc: u8 = 0;
    for (x, y) in a.iter().zip(b.iter()) {
        acc |= x ^ y;
    }
    acc == 0
}

fn zero(buf: &mut [u8]) {
    for b in buf.iter_mut() {
        *b = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_service_creation() {
        let service = SignalProtocolService::new();
        assert_eq!(service.get_public_key().len(), 32);
        assert_eq!(service.get_x25519_public_key().len(), 32);
    }

    #[test]
    fn test_pre_key_bundle_has_both_identity_keys() {
        let mut service = SignalProtocolService::new();
        let bundle = service.generate_pre_key_bundle("alice").unwrap();
        assert_eq!(bundle.identity_key.len(), 32); // Ed25519
        assert_eq!(bundle.identity_key_x25519.len(), 32); // X25519
        assert_ne!(bundle.identity_key, bundle.identity_key_x25519);
        assert_eq!(bundle.pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key_signature.len(), 64);
    }

    #[test]
    fn test_x3dh_first_message_round_trips() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();

        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let encrypted = alice.encrypt("bob", b"the mesh is alive").unwrap();
        assert_eq!(encrypted.message_type, MESSAGE_TYPE_PRE_KEY);
        assert_eq!(encrypted.sender_uhid, "alice");
        assert!(encrypted.initiator_identity_key_x25519.is_some());
        assert!(encrypted.sender_ephemeral_key_x25519.is_some());
        // Backward-compat: PreKey messages still carry initiator_ephemeral_key_x25519.
        assert!(encrypted.initiator_ephemeral_key_x25519.is_some());
        assert_eq!(
            encrypted.sender_ephemeral_key_x25519,
            encrypted.initiator_ephemeral_key_x25519
        );

        let plaintext = bob.decrypt("alice", &encrypted).unwrap();
        assert_eq!(plaintext, b"the mesh is alive");
        assert!(bob.has_session("alice"));
    }

    #[test]
    fn test_subsequent_message_is_normal() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let first = alice.encrypt("bob", b"a").unwrap();
        bob.decrypt("alice", &first).unwrap();

        let second = alice.encrypt("bob", b"b").unwrap();
        assert_eq!(second.message_type, MESSAGE_TYPE_NORMAL);
        assert!(second.initiator_identity_key_x25519.is_none());
        // Normal messages still carry the Double-Ratchet header.
        assert!(second.sender_ephemeral_key_x25519.is_some());

        let out = bob.decrypt("alice", &second).unwrap();
        assert_eq!(out, b"b");
    }

    #[test]
    fn test_bidirectional_after_first_message() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let a = alice.encrypt("bob", b"ping").unwrap();
        assert_eq!(bob.decrypt("alice", &a).unwrap(), b"ping");

        let b = bob.encrypt("alice", b"pong").unwrap();
        assert_eq!(b.message_type, MESSAGE_TYPE_NORMAL);
        assert_eq!(alice.decrypt("bob", &b).unwrap(), b"pong");
    }

    #[test]
    fn test_five_sequential_messages_ratchet_forward() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        for i in 0..5u8 {
            let enc = alice.encrypt("bob", &[i]).unwrap();
            assert_eq!(enc.counter, i as u32);
            let dec = bob.decrypt("alice", &enc).unwrap();
            assert_eq!(dec, [i]);
        }
    }

    #[test]
    fn test_one_time_pre_key_consumed() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let first = alice.encrypt("bob", b"first").unwrap();
        bob.decrypt("alice", &first).unwrap();

        // Replay using the same bundle should fail.
        let mut alice2 = SignalProtocolService::new();
        alice2.generate_pre_key_bundle("alice2").unwrap();
        alice2.process_pre_key_bundle(&bob_bundle).unwrap();
        let replay = alice2.encrypt("bob", b"replay").unwrap();
        assert!(bob.decrypt("alice2", &replay).is_err());
    }

    #[test]
    fn test_encrypt_without_local_uhid_errors() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        // Note: no generate_pre_key_bundle / set_local_uhid on Alice.
        alice.process_pre_key_bundle(&bob_bundle).unwrap();
        assert!(alice.encrypt("bob", b"x").is_err());
    }

    // ===== Double-Ratchet (Signal §5) tests =====

    /// On every reply, Bob's ratchet public key MUST change relative to
    /// Alice's view, because every receive triggers a DH-ratchet step that
    /// rotates DHs. Specifically: Bob's first send (after receiving Alice's
    /// initial PreKey message) carries Bob's freshly-rotated ratchet pub.
    #[test]
    fn test_dh_ratchet_rotates_on_reply() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let a = alice.encrypt("bob", b"hello").unwrap();
        let alice_first_pub = a.sender_ephemeral_key_x25519.clone().unwrap();
        bob.decrypt("alice", &a).unwrap();

        // Bob's reply: should carry Bob's rotated ratchet pub (NOT the SPK).
        let b = bob.encrypt("alice", b"hi").unwrap();
        let bob_reply_pub = b.sender_ephemeral_key_x25519.clone().unwrap();
        assert_eq!(bob_reply_pub.len(), 32);
        assert_ne!(bob_reply_pub, bob_bundle.signed_pre_key,
            "Bob's reply must use a rotated DHs, not the SPK that was adopted as initial DHs.");

        alice.decrypt("bob", &b).unwrap();

        // Alice's next message: should ALSO carry a rotated pub, different
        // from her first message's pub (because receiving Bob's reply
        // triggered her own DH-ratchet step).
        let a2 = alice.encrypt("bob", b"how are you").unwrap();
        let alice_second_pub = a2.sender_ephemeral_key_x25519.clone().unwrap();
        assert_ne!(alice_second_pub, alice_first_pub,
            "Alice's ratchet pub MUST rotate after she receives a new DHr from Bob.");
        assert_eq!(bob.decrypt("alice", &a2).unwrap(), b"how are you");
    }

    /// previous_chain_count (PN) MUST equal the number of messages sent in
    /// the previous sending chain (Signal §5).
    #[test]
    fn test_previous_chain_count_carries_across_ratchet() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        // Alice sends 3 on her first chain.
        for i in 0..3u8 {
            let m = alice.encrypt("bob", &[i]).unwrap();
            assert_eq!(m.previous_chain_count, 0, "First-chain messages have PN=0");
            bob.decrypt("alice", &m).unwrap();
        }

        // Bob replies — DH-ratchet rotates DHs on Bob's side. PN==0 for Bob too
        // because Bob hasn't sent yet.
        let b = bob.encrypt("alice", b"r").unwrap();
        assert_eq!(b.previous_chain_count, 0);
        alice.decrypt("bob", &b).unwrap();

        // Alice sends again. She has rotated DHs (from receiving Bob's reply).
        // Her previous chain had 3 messages — PN MUST == 3.
        let a = alice.encrypt("bob", b"after").unwrap();
        assert_eq!(a.previous_chain_count, 3,
            "PN MUST equal previous-chain message count after a DH-ratchet step.");
        assert_eq!(a.counter, 0, "Counter resets to 0 on each new sending chain.");
        assert_eq!(bob.decrypt("alice", &a).unwrap(), b"after");
    }

    /// Out-of-order delivery WITHIN a single chain: counter=2 arrives before
    /// counter=1; both decrypt correctly; counter=0 already decrypted.
    #[test]
    fn test_out_of_order_within_chain() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let m0 = alice.encrypt("bob", b"zero").unwrap();
        let m1 = alice.encrypt("bob", b"one").unwrap();
        let m2 = alice.encrypt("bob", b"two").unwrap();

        // Deliver 0, 2, 1.
        assert_eq!(bob.decrypt("alice", &m0).unwrap(), b"zero");
        assert_eq!(bob.decrypt("alice", &m2).unwrap(), b"two");
        assert_eq!(bob.decrypt("alice", &m1).unwrap(), b"one");
    }

    /// Out-of-order delivery ACROSS a DH-ratchet boundary: a message from
    /// chain N arrives AFTER a chain N+1 message has already been decrypted.
    /// The skipped-keys cache (keyed by old DHr_pub) MUST still resolve it.
    #[test]
    fn test_out_of_order_across_dh_ratchet_boundary() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        // Alice's first chain: send 2 messages.
        let a0 = alice.encrypt("bob", b"alice-0").unwrap();
        let a1 = alice.encrypt("bob", b"alice-1").unwrap();
        bob.decrypt("alice", &a0).unwrap();
        // Withhold a1 deliberately — simulating reorder.

        // Bob replies → rotates Bob's DHs.
        let b0 = bob.encrypt("alice", b"bob-0").unwrap();
        alice.decrypt("bob", &b0).unwrap();

        // Alice's next message → her DHs has rotated (chain N+1).
        let a2 = alice.encrypt("bob", b"alice-2").unwrap();
        bob.decrypt("alice", &a2).unwrap();

        // NOW the delayed a1 arrives. Must still decrypt (skipped-keys cache).
        assert_eq!(bob.decrypt("alice", &a1).unwrap(), b"alice-1",
            "Delayed message from previous chain MUST decrypt via skipped-keys cache.");
    }

    /// Long bidirectional ping-pong — many DH-ratchet steps per direction.
    /// Each rotation re-keys both chains. Verifies post-compromise security
    /// machinery doesn't accumulate state errors.
    #[test]
    fn test_long_ping_pong_many_dh_ratchets() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        for round in 0..10u8 {
            let a = alice.encrypt("bob", &[round, b'a']).unwrap();
            assert_eq!(bob.decrypt("alice", &a).unwrap(), &[round, b'a']);
            let b = bob.encrypt("alice", &[round, b'b']).unwrap();
            assert_eq!(alice.decrypt("bob", &b).unwrap(), &[round, b'b']);
        }
    }

    /// Replaying the same message MUST fail. AES-GCM's authentication tag
    /// catches it because the message key was already consumed (one-time use).
    #[test]
    fn test_replay_detection() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let m = alice.encrypt("bob", b"once").unwrap();
        bob.decrypt("alice", &m).unwrap();
        assert!(bob.decrypt("alice", &m).is_err(),
            "Replaying a consumed message must fail.");
    }

    /// Counter-gap larger than MAX_SKIPPED_KEYS must abort, NOT allocate
    /// unbounded memory.
    #[test]
    fn test_huge_gap_rejected() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        // First message establishes Bob's session and its counter.
        let m0 = alice.encrypt("bob", b"x").unwrap();
        bob.decrypt("alice", &m0).unwrap();

        // Forge a message claiming counter = MAX_SKIPPED_KEYS + 5 on the same chain.
        let mut fake = alice.encrypt("bob", b"y").unwrap();
        fake.counter = (MAX_SKIPPED_KEYS as u32) + 5;
        assert!(bob.decrypt("alice", &fake).is_err(),
            "Counter gap exceeding MAX_SKIPPED_KEYS must be rejected.");
    }

    /// KDF_RK output is deterministic for fixed inputs and matches the
    /// HKDF construction `salt=root_key, ikm=dh_output, info=aether-ratchet-rk-v1`,
    /// 64 bytes split into (root, chain).
    #[test]
    fn test_kdf_rk_deterministic_and_split_correctly() {
        let root = [0x42u8; 32];
        let dh = [0x37u8; 32];
        let (r1, c1) = kdf_rk(&root, &dh).unwrap();
        let (r2, c2) = kdf_rk(&root, &dh).unwrap();
        assert_eq!(r1.len(), 32);
        assert_eq!(c1.len(), 32);
        assert_eq!(r1, r2);
        assert_eq!(c1, c2);
        assert_ne!(r1, c1, "Root and chain halves must differ.");

        // Confirm against the literal HKDF expand.
        let hk = Hkdf::<Sha256>::new(Some(&root), &dh);
        let mut expected = [0u8; 64];
        hk.expand(b"aether-ratchet-rk-v1", &mut expected).unwrap();
        assert_eq!(&r1[..], &expected[0..32]);
        assert_eq!(&c1[..], &expected[32..64]);
    }

    /// Cross-language KDF_RK fixture (`fixtures/signal/expected/kdf_rk_basic.json`).
    /// Inputs: root_key=0xAA*32, dh_output=0xBB*32, info='aether-ratchet-rk-v1'.
    /// These exact bytes must match every other language implementation —
    /// drift here breaks Rust↔C# Double-Ratchet interop.
    #[test]
    fn test_kdf_rk_matches_cross_language_fixture() {
        let root = [0xAAu8; 32];
        let dh = [0xBBu8; 32];
        let (new_root, new_chain) = kdf_rk(&root, &dh).unwrap();
        let new_root_hex: String = new_root.iter().map(|b| format!("{:02x}", b)).collect();
        let new_chain_hex: String = new_chain.iter().map(|b| format!("{:02x}", b)).collect();
        assert_eq!(
            new_root_hex,
            "8f894048bc850a5ce9281af47d06e2281832636c87c3f891ef4e7a9489bab4d1",
            "KDF_RK new root MUST match fixtures/signal/expected/kdf_rk_basic.json"
        );
        assert_eq!(
            new_chain_hex,
            "08cba1060cf4e54e8a80598313e6ed32c78029bfd1668689386044aaf4b74af0",
            "KDF_RK new chain MUST match fixtures/signal/expected/kdf_rk_basic.json"
        );
    }

    /// Skipped-key composite key matches the C# `SkippedKey` formatter
    /// (`HEX_UPPER:counter`).
    #[test]
    fn test_skipped_key_format_matches_csharp() {
        let pk = [0xAB, 0xCD, 0x12, 0x34];
        let s = skipped_key(&pk, 7);
        assert_eq!(s, "ABCD1234:7");
    }

    // ===== OPK pool tests =====

    /// Default OPK pool size matches the C# reference (100).
    #[test]
    fn test_default_opk_pool_size_matches_csharp() {
        let svc = SignalProtocolService::new();
        assert_eq!(svc.opk_pool_size(), 100);
        assert_eq!(svc.opk_pool_size(), DEFAULT_OPK_POOL_SIZE);
    }

    /// Constructor with custom pool size honours the configured target.
    #[test]
    fn test_with_opk_pool_size_honours_target() {
        let svc = SignalProtocolService::with_opk_pool_size(7);
        assert_eq!(svc.opk_pool_size(), 7);
    }

    /// Pool size 0 must panic — matches C# `ArgumentOutOfRangeException`.
    #[test]
    #[should_panic(expected = "target_opk_pool_size must be >= 1")]
    fn test_opk_pool_size_zero_panics() {
        let _ = SignalProtocolService::with_opk_pool_size(0);
    }

    /// Before the first bundle, the pool is empty. After the first bundle:
    ///   * `held` == pool target size (the seed batch — minus none, because
    ///     no responder has consumed yet)
    ///   * `available` == pool target size - 1 (one was just dequeued for
    ///     the bundle)
    #[test]
    fn test_pool_seeded_to_target_after_first_bundle() {
        let mut svc = SignalProtocolService::with_opk_pool_size(10);
        let (held_before, avail_before) = svc.pre_key_pool_status();
        assert_eq!((held_before, avail_before), (0, 0));

        let _ = svc.generate_pre_key_bundle("alice").unwrap();
        let (held, available) = svc.pre_key_pool_status();
        assert_eq!(held, 10, "pool seeded to target size");
        assert_eq!(available, 9, "one dequeued for the bundle");
    }

    /// 100 sequential bundles → 100 distinct OPK ids (no reuse), pool stays
    /// topped up to ~target size.
    #[test]
    fn test_distinct_opk_ids_over_100_sequential_bundles() {
        let mut svc = SignalProtocolService::with_opk_pool_size(100);
        let mut seen: std::collections::HashSet<i32> = std::collections::HashSet::new();
        for _ in 0..100 {
            let bundle = svc.generate_pre_key_bundle("alice").unwrap();
            assert!(
                seen.insert(bundle.pre_key_id),
                "OPK id {} reused — pool issued the same id twice",
                bundle.pre_key_id
            );
        }
        assert_eq!(seen.len(), 100, "100 bundles must yield 100 distinct OPK ids");

        // Pool top-up keeps available at target size after each dequeue.
        // No responder consumed, so the held count grows by one per call:
        // held == target_size + (calls - 1) = 100 + 99 = 199.
        let (held, available) = svc.pre_key_pool_status();
        assert_eq!(
            available, 99,
            "available should be (target - 1) after the last dequeue"
        );
        assert_eq!(
            held, 199,
            "no responder consumed, so each top-up adds one — 100 seed + 99 top-ups"
        );
    }

    /// Responder-side X3DH consumption removes the OPK from the pool.
    #[test]
    fn test_consumption_removes_opk_from_pool() {
        let mut alice = SignalProtocolService::with_opk_pool_size(5);
        let mut bob = SignalProtocolService::with_opk_pool_size(5);

        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        let bob_pool_before = bob.pre_key_pool_status();
        assert_eq!(bob_pool_before, (5, 4));

        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();
        let m = alice.encrypt("bob", b"hi").unwrap();
        bob.decrypt("alice", &m).unwrap();

        // After Bob runs responder-side X3DH, the OPK Alice consumed is gone.
        let bob_pool_after = bob.pre_key_pool_status();
        assert_eq!(
            bob_pool_after.0, 4,
            "responder X3DH consumed one OPK — held drops from 5 to 4"
        );
        // available_opk_ids never contained the issued id anyway, so it stays at 4.
        assert_eq!(bob_pool_after.1, 4);
    }

    /// Top-up runs on the next bundle generation after a responder consumes
    /// an issued OPK — the held count rises back to the target.
    #[test]
    fn test_top_up_replenishes_after_consumption() {
        let mut alice = SignalProtocolService::with_opk_pool_size(5);
        let mut bob = SignalProtocolService::with_opk_pool_size(5);

        let bob_bundle1 = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle1).unwrap();
        bob.decrypt("alice", &alice.encrypt("bob", b"x").unwrap())
            .unwrap();

        // Bob held should be 4 now (5 seeded, 1 consumed).
        assert_eq!(bob.pre_key_pool_status(), (4, 4));

        // Generate another bundle — top-up must restore to target=5 available.
        let _bob_bundle2 = bob.generate_pre_key_bundle("bob").unwrap();
        let (held, available) = bob.pre_key_pool_status();
        // Held = 5 (4 leftover + 1 freshly minted to top up). Available = 4
        // (5 - 1 dequeued for this bundle).
        assert_eq!(held, 5, "top-up restored held count to target");
        assert_eq!(available, 4, "one dequeued for the new bundle");
    }

    /// Concurrent initiators against the same responder MUST NOT collide on
    /// a shared OPK id — the OPK pool's mutex guarantees atomic dequeue.
    /// Each bundle MUST carry a distinct OPK id so each responder consume
    /// resolves a unique stored entry.
    #[test]
    fn test_concurrent_initiators_get_distinct_opks() {
        use std::sync::{Arc, Mutex as StdMutex};
        use std::thread;

        let svc = Arc::new(StdMutex::new(SignalProtocolService::with_opk_pool_size(50)));

        let mut handles = Vec::new();
        for i in 0..20 {
            let svc = Arc::clone(&svc);
            let handle = thread::spawn(move || {
                let mut guard = svc.lock().unwrap();
                guard
                    .generate_pre_key_bundle(&format!("user-{}", i))
                    .unwrap()
                    .pre_key_id
            });
            handles.push(handle);
        }

        let ids: Vec<i32> = handles.into_iter().map(|h| h.join().unwrap()).collect();
        let unique: std::collections::HashSet<i32> = ids.iter().cloned().collect();
        assert_eq!(
            unique.len(),
            20,
            "20 concurrent bundles must yield 20 distinct OPK ids"
        );
    }

    /// Replaying a bundle against the same responder must fail on the second
    /// initiator: the responder's first decrypt consumes the OPK, the second
    /// initiator's PreKey message references an id that is no longer held.
    /// (This was already covered by `test_one_time_pre_key_consumed`; this
    /// test re-asserts under the new pool-backed layout.)
    #[test]
    fn test_pool_consumption_blocks_replay() {
        let mut alice = SignalProtocolService::with_opk_pool_size(5);
        let mut bob = SignalProtocolService::with_opk_pool_size(5);
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();
        let first = alice.encrypt("bob", b"first").unwrap();
        bob.decrypt("alice", &first).unwrap();

        let mut alice2 = SignalProtocolService::with_opk_pool_size(5);
        alice2.generate_pre_key_bundle("alice2").unwrap();
        alice2.process_pre_key_bundle(&bob_bundle).unwrap();
        let replay = alice2.encrypt("bob", b"replay").unwrap();
        assert!(
            bob.decrypt("alice2", &replay).is_err(),
            "consumed OPK must NOT be re-usable"
        );
    }

    /// Signal-canonical X3DH↔Double-Ratchet integration check: the initiator's
    /// X3DH ephemeral pub MUST equal her first DH-ratchet pub (and thus the
    /// PreKey message's `sender_ephemeral_key_x25519`).
    #[test]
    fn test_initiator_x3dh_ephemeral_is_first_ratchet_pub() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let m = alice.encrypt("bob", b"first").unwrap();
        let sender_pub = m.sender_ephemeral_key_x25519.clone().unwrap();
        let initiator_pub = m.initiator_ephemeral_key_x25519.clone().unwrap();
        assert_eq!(sender_pub, initiator_pub,
            "First PreKey message: sender_ephemeral_key_x25519 == initiator_ephemeral_key_x25519");
    }

    // ===== Persistence (Tier-2 parity with C# 1beb4a7) =====

    use crate::security::prekey_store::InMemoryPreKeyStore;
    use crate::security::session_store::InMemorySignalSessionStore;
    use std::sync::Arc as StdArc;

    /// After a PreKey-then-Normal exchange, restarting the responder against
    /// the same persistent stores must hydrate the SignalSession so the next
    /// peer message decrypts cleanly without re-running X3DH.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn persistent_session_survives_responder_restart() {
        let bob_session_store: StdArc<dyn SignalSessionStore> =
            StdArc::new(InMemorySignalSessionStore::new());
        let bob_pre_key_store: StdArc<dyn PreKeyStore> = StdArc::new(InMemoryPreKeyStore::new());

        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::builder()
            .with_session_store(bob_session_store.clone())
            .with_prekey_store(bob_pre_key_store.clone())
            .build();

        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let m1 = alice.encrypt("bob", b"hello").unwrap();
        bob.decrypt("alice", &m1).unwrap();

        // Allow fire-and-forget persistence to complete.
        tokio::task::yield_now().await;
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;

        // Drop bob and rebuild from the same stores.
        drop(bob);
        let mut bob2 = SignalProtocolService::builder()
            .with_session_store(bob_session_store.clone())
            .with_prekey_store(bob_pre_key_store.clone())
            .build();

        // Hydrated? alice's UHID should be in the rebuilt service.
        assert!(
            bob2.has_session("alice"),
            "session for 'alice' must hydrate from the persistent store"
        );

        let m2 = alice.encrypt("bob", b"after-restart").unwrap();
        let plaintext = bob2
            .decrypt("alice", &m2)
            .expect("hydrated session must continue the Double Ratchet");
        assert_eq!(plaintext, b"after-restart");
    }

    /// Identity keys (Ed25519 + X25519 long-term) must survive a restart so
    /// the local UHID can encrypt without redoing X3DH from scratch.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn identity_keys_survive_restart() {
        let store: StdArc<dyn PreKeyStore> = StdArc::new(InMemoryPreKeyStore::new());

        let mut alice = SignalProtocolService::builder()
            .with_prekey_store(store.clone())
            .build();
        alice.set_local_uhid("alice");
        let ed_pub_before = alice.get_public_key();
        let x_pub_before = alice.get_x25519_public_key();
        // Generate at least one bundle so SPK + OPK pool persist.
        alice.generate_pre_key_bundle("alice").unwrap();
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        drop(alice);

        let alice2 = SignalProtocolService::builder()
            .with_prekey_store(store.clone())
            .build();
        assert_eq!(alice2.get_public_key(), ed_pub_before, "Ed25519 pub key must hydrate");
        assert_eq!(
            alice2.get_x25519_public_key(),
            x_pub_before,
            "X25519 long-term pub key must hydrate"
        );
    }

    /// SPK + OPK pool survive a restart: a peer that received a bundle
    /// before the restart can still establish a session against the same
    /// SPK id (and the OPK pool stays hydrated).
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn pre_key_state_survives_restart() {
        let bob_pre_key_store: StdArc<dyn PreKeyStore> =
            StdArc::new(InMemoryPreKeyStore::new());

        let mut bob = SignalProtocolService::builder()
            .with_prekey_store(bob_pre_key_store.clone())
            .build();
        let bundle1 = bob.generate_pre_key_bundle("bob").unwrap();
        let bob_pool_before = bob.pre_key_pool_status();
        let active_spk_before = bob.active_signed_pre_key_id();
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        drop(bob);

        let bob2 = SignalProtocolService::builder()
            .with_prekey_store(bob_pre_key_store.clone())
            .build();
        assert_eq!(
            bob2.active_signed_pre_key_id(),
            active_spk_before,
            "active SPK id must hydrate"
        );
        let bob_pool_after = bob2.pre_key_pool_status();
        assert_eq!(
            bob_pool_after.0, bob_pool_before.0,
            "OPK pool held count must hydrate"
        );

        // The bundle issued before the restart should still resolve on the
        // hydrated responder: do an X3DH against bob2.
        let mut alice = SignalProtocolService::new();
        alice.generate_pre_key_bundle("alice").unwrap();
        let mut bob2 = bob2;
        alice.process_pre_key_bundle(&bundle1).unwrap();
        let m = alice.encrypt("bob", b"after-restart-spk").unwrap();
        let pt = bob2
            .decrypt("alice", &m)
            .expect("pre-restart bundle must still resolve against hydrated responder");
        assert_eq!(pt, b"after-restart-spk");
    }

    /// Without persistent stores configured, behaviour must match the old
    /// constructor exactly — no surprise mutations of the in-memory state.
    #[test]
    fn no_stores_means_baseline_behaviour() {
        let mut svc = SignalProtocolService::new();
        let pub_before = svc.get_public_key();
        let _bundle = svc.generate_pre_key_bundle("alice").unwrap();
        assert_eq!(svc.get_public_key(), pub_before);
    }

    // ===== Signed pre-key rotation (Tier-2 parity with C# 13e9d3b) =====
    //
    // The rotation infrastructure (history, append/prune, SPK-id lookup
    // across the retained window) shipped in the persistence commit because
    // it shares structures with the persistent SPK history; this block
    // adds the time-driven coverage that exercises it end-to-end.

    use chrono::{Duration as ChronoDuration, TimeZone};

    /// Helper: a synthetic clock that returns a fixed-time pointer behind a
    /// shared cell. The test mutates the cell to drive rotation.
    struct TestClock {
        now: std::sync::Arc<std::sync::Mutex<chrono::DateTime<chrono::Utc>>>,
    }
    impl TestClock {
        fn at(dt: chrono::DateTime<chrono::Utc>) -> (Self, NowProvider) {
            let now = std::sync::Arc::new(std::sync::Mutex::new(dt));
            let now_clone = now.clone();
            let provider: NowProvider =
                StdArc::new(move || *now_clone.lock().expect("clock mutex"));
            (Self { now }, provider)
        }
        fn set(&self, dt: chrono::DateTime<chrono::Utc>) {
            *self.now.lock().expect("clock mutex") = dt;
        }
    }

    /// On first bundle generation the SPK history starts at length 1 (no
    /// rotation yet — there's nothing to rotate from).
    #[test]
    fn initial_spk_history_size_is_one() {
        let mut svc = SignalProtocolService::new();
        let _ = svc.generate_pre_key_bundle("alice").unwrap();
        assert_eq!(svc.signed_pre_key_history_count(), 1);
    }

    /// Bundles generated within rotation_interval reuse the same SPK id.
    #[test]
    fn no_rotation_within_interval() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_rotation_options(SignedPreKeyRotationOptions {
                rotation_interval: ChronoDuration::days(7),
                retained_history_count: 3,
            })
            .build();
        let b1 = svc.generate_pre_key_bundle("alice").unwrap();
        // Move the clock 1 day forward — well within the 7-day window.
        clock.set(t0 + ChronoDuration::days(1));
        let b2 = svc.generate_pre_key_bundle("alice").unwrap();
        assert_eq!(b1.signed_pre_key_id, b2.signed_pre_key_id);
        assert_eq!(svc.signed_pre_key_history_count(), 1);
    }

    /// Rotation triggers when the active SPK age exceeds rotation_interval.
    /// History grows up to `1 + retained_history_count`.
    #[test]
    fn rotation_after_interval_grows_history() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_rotation_options(SignedPreKeyRotationOptions {
                rotation_interval: ChronoDuration::days(7),
                retained_history_count: 3,
            })
            .build();
        let b1 = svc.generate_pre_key_bundle("alice").unwrap();
        clock.set(t0 + ChronoDuration::days(7) + ChronoDuration::seconds(1));
        let b2 = svc.generate_pre_key_bundle("alice").unwrap();
        assert_ne!(b1.signed_pre_key_id, b2.signed_pre_key_id);
        assert_eq!(svc.signed_pre_key_history_count(), 2);
    }

    /// Retention prunes the oldest entries beyond
    /// `1 + retained_history_count`. With retention=2 + 1 active = 3 max,
    /// after 5 rotations we keep 3 entries.
    #[test]
    fn retention_prunes_oldest() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_rotation_options(SignedPreKeyRotationOptions {
                rotation_interval: ChronoDuration::days(7),
                retained_history_count: 2,
            })
            .build();
        for i in 0..5 {
            clock.set(t0 + ChronoDuration::days(7 * i + 1));
            svc.generate_pre_key_bundle("alice").unwrap();
        }
        // Max = 1 + retained = 3.
        assert_eq!(svc.signed_pre_key_history_count(), 3);
    }

    /// X3DH against a retained-but-not-active SPK still completes — i.e. a
    /// peer that received an older bundle and presents that older
    /// signed_pre_key_id can still establish a session.
    #[test]
    fn x3dh_against_retained_spk_succeeds() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut bob = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_rotation_options(SignedPreKeyRotationOptions {
                rotation_interval: ChronoDuration::days(7),
                retained_history_count: 3,
            })
            .build();
        // bob issues bundle with old SPK
        let bundle_old = bob.generate_pre_key_bundle("bob").unwrap();
        let old_spk_id = bundle_old.signed_pre_key_id;
        // Time passes — bob rotates SPK on next bundle.
        clock.set(t0 + ChronoDuration::days(8));
        let bundle_new = bob.generate_pre_key_bundle("bob").unwrap();
        assert_ne!(bundle_new.signed_pre_key_id, old_spk_id);

        // Alice processed the OLD bundle. X3DH must still work.
        let mut alice = SignalProtocolService::new();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bundle_old).unwrap();
        let m = alice.encrypt("bob", b"retained").unwrap();
        let pt = bob.decrypt("alice", &m).unwrap();
        assert_eq!(pt, b"retained");
    }

    /// A bundle that references a *pruned* SPK fails outright. With
    /// retention=0, the second bundle (post-rotation) prunes the prior SPK
    /// immediately.
    #[test]
    fn x3dh_against_pruned_spk_fails() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut bob = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_rotation_options(SignedPreKeyRotationOptions {
                rotation_interval: ChronoDuration::days(7),
                retained_history_count: 0,
            })
            .build();
        let bundle_old = bob.generate_pre_key_bundle("bob").unwrap();
        clock.set(t0 + ChronoDuration::days(8));
        let _bundle_new = bob.generate_pre_key_bundle("bob").unwrap();
        assert_eq!(bob.signed_pre_key_history_count(), 1);

        let mut alice = SignalProtocolService::new();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bundle_old).unwrap();
        let m = alice.encrypt("bob", b"pruned").unwrap();
        assert!(
            bob.decrypt("alice", &m).is_err(),
            "Pruned SPK reference must fail X3DH"
        );
    }

    /// Explicit rotate_signed_pre_key only rotates when interval has
    /// elapsed; otherwise it's a no-op.
    #[tokio::test]
    async fn explicit_rotate_no_op_within_interval() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .build();
        let _ = svc.generate_pre_key_bundle("alice").unwrap();
        clock.set(t0 + ChronoDuration::days(1));
        let rotated = svc.rotate_signed_pre_key().await.unwrap();
        assert!(!rotated, "rotate within interval must be a no-op");
        assert_eq!(svc.signed_pre_key_history_count(), 1);
    }

    /// Explicit rotate_signed_pre_key past interval mints a fresh entry.
    #[tokio::test]
    async fn explicit_rotate_after_interval_appends() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .build();
        let _ = svc.generate_pre_key_bundle("alice").unwrap();
        clock.set(t0 + ChronoDuration::days(8));
        let rotated = svc.rotate_signed_pre_key().await.unwrap();
        assert!(rotated);
        assert_eq!(svc.signed_pre_key_history_count(), 2);
    }

    /// SPK rotation also persists the new history.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn rotation_persists_to_pre_key_store() {
        let t0 = chrono::Utc.with_ymd_and_hms(2025, 1, 1, 0, 0, 0).unwrap();
        let (clock, provider) = TestClock::at(t0);
        let store: StdArc<dyn PreKeyStore> = StdArc::new(InMemoryPreKeyStore::new());
        let mut svc = SignalProtocolService::builder()
            .with_now_provider(provider)
            .with_prekey_store(store.clone())
            .build();
        let _ = svc.generate_pre_key_bundle("alice").unwrap();
        clock.set(t0 + ChronoDuration::days(8));
        let rotated = svc.rotate_signed_pre_key().await.unwrap();
        assert!(rotated);
        // Persistence is fire-and-forget; poll until the second entry lands rather than racing a
        // fixed sleep (which flakes under heavy parallel load, e.g. `cargo test --features webrtc`).
        let mut history = store.load_signed_pre_keys().await.unwrap();
        for _ in 0..200 {
            if history.entries.len() >= 2 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            history = store.load_signed_pre_keys().await.unwrap();
        }
        assert_eq!(history.entries.len(), 2);
    }
}
