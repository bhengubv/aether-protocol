// SPDX-License-Identifier: MIT

//! Persistent storage for Signal-Protocol session state. Each session is
//! keyed by the peer's UHID. Implementations are responsible for atomicity
//! and durability — the protocol layer hands an opaque [`SignalSession`]
//! in and trusts that [`SignalSessionStore::load`] later returns the exact
//! same state (or `None` if no session was previously stored).
//!
//! Mirrors the C# `ISignalSessionStore` interface and its in-memory + KV
//! adapters.

use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::Mutex;
use std::time::SystemTime;

use crate::models::SignalSession;
use crate::security::dtos::SignalSessionDto;
use crate::storage::kv::{KeyValueStore, Result as KvResult};

/// Result alias for store operations — same boxed-error type as
/// [`crate::storage::kv::Result`] so adapters can compose without a layer of
/// `?`-juggling.
pub type Result<T> = KvResult<T>;

/// Persistent storage for Signal-Protocol session state.
#[async_trait]
pub trait SignalSessionStore: Send + Sync {
    async fn load(&self, peer_uhid: &str) -> Result<Option<SignalSession>>;
    async fn save(&self, peer_uhid: &str, session: &SignalSession) -> Result<()>;
    async fn delete(&self, peer_uhid: &str) -> Result<()>;
    async fn list_peers(&self) -> Result<Vec<String>>;
}

/// Process-local, volatile [`SignalSessionStore`]. Stores the session
/// JSON-encoded under each peer's UHID. Suitable for tests and demos —
/// loses everything on process exit. Hosts that need durability wire up
/// [`KvSignalSessionStore`] over a [`crate::storage::FileSystemKeyValueStore`].
pub struct InMemorySignalSessionStore {
    sessions: Mutex<HashMap<String, Vec<u8>>>,
}

impl InMemorySignalSessionStore {
    pub fn new() -> Self {
        Self {
            sessions: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemorySignalSessionStore {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl SignalSessionStore for InMemorySignalSessionStore {
    async fn load(&self, peer_uhid: &str) -> Result<Option<SignalSession>> {
        let guard = self.sessions.lock().expect("session store mutex poisoned");
        match guard.get(peer_uhid) {
            Some(bytes) => Ok(Some(deserialize_session(bytes)?)),
            None => Ok(None),
        }
    }

    async fn save(&self, peer_uhid: &str, session: &SignalSession) -> Result<()> {
        let bytes = serialize_session(session)?;
        let mut guard = self.sessions.lock().expect("session store mutex poisoned");
        guard.insert(peer_uhid.to_string(), bytes);
        Ok(())
    }

    async fn delete(&self, peer_uhid: &str) -> Result<()> {
        let mut guard = self.sessions.lock().expect("session store mutex poisoned");
        guard.remove(peer_uhid);
        Ok(())
    }

    async fn list_peers(&self) -> Result<Vec<String>> {
        let guard = self.sessions.lock().expect("session store mutex poisoned");
        Ok(guard.keys().cloned().collect())
    }
}

/// [`SignalSessionStore`] adapter over an arbitrary [`KeyValueStore`].
/// Sessions are JSON-encoded under `signal:session:<peerUhid>`.
///
/// Mirrors `Aether.Storage.KeyValueSignalSessionStore`.
pub struct KvSignalSessionStore {
    kv: Arc<dyn KeyValueStore>,
}

impl KvSignalSessionStore {
    pub const PREFIX: &'static str = "signal:session:";

    pub fn new(kv: Arc<dyn KeyValueStore>) -> Self {
        Self { kv }
    }

    fn key(peer_uhid: &str) -> String {
        format!("{}{}", Self::PREFIX, peer_uhid)
    }
}

#[async_trait]
impl SignalSessionStore for KvSignalSessionStore {
    async fn load(&self, peer_uhid: &str) -> Result<Option<SignalSession>> {
        match self.kv.get(&Self::key(peer_uhid)).await? {
            Some(bytes) => Ok(Some(deserialize_session(&bytes)?)),
            None => Ok(None),
        }
    }

    async fn save(&self, peer_uhid: &str, session: &SignalSession) -> Result<()> {
        let bytes = serialize_session(session)?;
        self.kv.put(&Self::key(peer_uhid), &bytes).await
    }

    async fn delete(&self, peer_uhid: &str) -> Result<()> {
        self.kv.remove(&Self::key(peer_uhid)).await
    }

    async fn list_peers(&self) -> Result<Vec<String>> {
        let keys = self.kv.list_keys(Some(Self::PREFIX)).await?;
        Ok(keys
            .into_iter()
            .map(|k| k[Self::PREFIX.len()..].to_string())
            .collect())
    }
}

/// Serialise a [`SignalSession`] to JSON bytes.
pub(crate) fn serialize_session(session: &SignalSession) -> Result<Vec<u8>> {
    let dto = SignalSessionDto {
        root_key: session.root_key.clone(),
        send_chain_key: session.send_chain_key.clone(),
        recv_chain_key: session.recv_chain_key.clone(),
        send_counter: session.send_counter,
        recv_counter: session.recv_counter,
        previous_chain_count: session.previous_chain_count,
        my_ephemeral_priv: session.my_ephemeral_priv.clone(),
        my_ephemeral_pub: session.my_ephemeral_pub.clone(),
        remote_ephemeral_pub: session.remote_ephemeral_pub.clone(),
        skipped_message_keys: session.skipped_message_keys.clone(),
        pending_pre_key_message: session.pending_pre_key_message,
        initiator_identity_key_x25519: session.initiator_identity_key_x25519.clone(),
        used_signed_pre_key_id: session.used_signed_pre_key_id,
        used_one_time_pre_key_id: session.used_one_time_pre_key_id,
    };
    let bytes = serde_json::to_vec(&dto)?;
    Ok(bytes)
}

/// Deserialise [`SignalSession`] state previously written by
/// [`serialize_session`]. Fields not preserved on the wire (e.g.
/// `peer_uhid`, `created_at`, `remote_public_key`) are reset to safe
/// defaults — the session ID is implicit in the storage key.
pub(crate) fn deserialize_session(bytes: &[u8]) -> Result<SignalSession> {
    let dto: SignalSessionDto = serde_json::from_slice(bytes)?;
    let now = SystemTime::now();
    let mut session = SignalSession::new(String::new(), Vec::new());
    session.root_key = dto.root_key;
    session.send_chain_key = dto.send_chain_key;
    session.recv_chain_key = dto.recv_chain_key;
    session.send_counter = dto.send_counter;
    session.recv_counter = dto.recv_counter;
    session.previous_chain_count = dto.previous_chain_count;
    session.my_ephemeral_priv = dto.my_ephemeral_priv;
    session.my_ephemeral_pub = dto.my_ephemeral_pub;
    session.remote_ephemeral_pub = dto.remote_ephemeral_pub;
    session.skipped_message_keys = dto.skipped_message_keys;
    session.pending_pre_key_message = dto.pending_pre_key_message;
    session.initiator_identity_key_x25519 = dto.initiator_identity_key_x25519;
    session.used_signed_pre_key_id = dto.used_signed_pre_key_id;
    session.used_one_time_pre_key_id = dto.used_one_time_pre_key_id;
    session.created_at = now;
    session.updated_at = now;
    Ok(session)
}
