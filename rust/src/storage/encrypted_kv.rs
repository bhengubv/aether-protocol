// SPDX-License-Identifier: MIT

//! Transparent encryption-at-rest wrapper for an arbitrary
//! [`KeyValueStore`]. Encrypts every value on the way down and decrypts on
//! the way up using AES-256-GCM with a per-write random nonce. Keys are
//! passed through unchanged so list/range queries continue to work.
//!
//! **Threat model:** protects persisted bytes from an attacker who recovers
//! the underlying medium (stolen disk, recycled SD card, leaked backup)
//! without compromising the master-key material that the host hands to the
//! [`DataAtRestKeyProvider`]. The wrapper does NOT hide write patterns, key
//! names, or value sizes.
//!
//! **Wire format (per stored blob):**
//! ```text
//! version (1 byte) || nonce (12 bytes) || ciphertext (N bytes) || tag (16 bytes)
//! ```
//! The version byte names which key in the provider was used; the wrapper
//! looks it up on read so hosts can run a rotation window with both old and
//! new keys loaded. Tampered/wrong-key reads return `Ok(None)` (treated by
//! callers as "not present"), matching the C# reference behaviour.

use aes_gcm::{aead::Aead, Aes256Gcm, Key, KeyInit, Nonce};
use async_trait::async_trait;
use rand::RngCore;
use std::sync::Arc;

use super::key_provider::{DataAtRestKeyProvider, AES_KEY_LEN};
use super::kv::{KeyValueStore, Result};

/// AES-GCM nonce length in bytes.
pub const NONCE_LEN: usize = 12;

/// AES-GCM authentication tag length in bytes (handled by the aead crate
/// internally — included here only for the size budget calculation below).
pub const TAG_LEN: usize = 16;

/// Length of the version-byte header at the start of every blob.
pub const VERSION_HEADER_LEN: usize = 1;

/// Minimum byte count for any well-formed encrypted blob (header + nonce
/// + tag, with zero-length ciphertext).
pub const MIN_BLOB_LEN: usize = VERSION_HEADER_LEN + NONCE_LEN + TAG_LEN;

/// Wraps an inner [`KeyValueStore`] with transparent AES-256-GCM
/// encryption. Constructed once and shared (typically behind an `Arc`); the
/// inner store and the key provider are likewise shared.
pub struct EncryptedKeyValueStore {
    inner: Arc<dyn KeyValueStore>,
    key_provider: Arc<dyn DataAtRestKeyProvider>,
}

impl EncryptedKeyValueStore {
    pub fn new(
        inner: Arc<dyn KeyValueStore>,
        key_provider: Arc<dyn DataAtRestKeyProvider>,
    ) -> Self {
        Self {
            inner,
            key_provider,
        }
    }

    /// Re-encrypts every value in the underlying store under the provider's
    /// current key version. Use during a key-rotation window after the
    /// provider has been swapped to one that holds both the old and new
    /// keys: values written under the old version stay readable, and after
    /// the rewrap completes every blob is on the new version so the host
    /// can retire the old key on the next deploy.
    ///
    /// Returns the number of values successfully rewrapped. Skips values
    /// that fail to decrypt (e.g. blob written under a key the provider no
    /// longer holds).
    pub async fn rewrap(&self) -> Result<usize> {
        let keys = self.inner.list_keys(None).await?;
        let mut rewrapped = 0usize;
        for k in keys {
            match self.get(&k).await? {
                Some(plaintext) => {
                    self.put(&k, &plaintext).await?;
                    rewrapped += 1;
                }
                None => {
                    // Value couldn't be decrypted — skip silently; the host
                    // can scan the inner store directly to find these.
                }
            }
        }
        Ok(rewrapped)
    }
}

#[async_trait]
impl KeyValueStore for EncryptedKeyValueStore {
    async fn get(&self, key: &str) -> Result<Option<Vec<u8>>> {
        let blob = match self.inner.get(key).await? {
            Some(b) => b,
            None => return Ok(None),
        };
        if blob.len() < MIN_BLOB_LEN {
            // Tampered or truncated. Treat as missing.
            return Ok(None);
        }
        let version = blob[0];
        let key_bytes = match self.key_provider.get_key(version) {
            Some(k) => k,
            None => return Ok(None), // Unknown key version — can't decrypt.
        };

        let nonce = &blob[VERSION_HEADER_LEN..VERSION_HEADER_LEN + NONCE_LEN];
        let ct_and_tag = &blob[VERSION_HEADER_LEN + NONCE_LEN..];

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&key_bytes));
        match cipher.decrypt(Nonce::from_slice(nonce), ct_and_tag) {
            Ok(plaintext) => Ok(Some(plaintext)),
            Err(_) => Ok(None), // Authentication failed — treat as missing.
        }
    }

    async fn put(&self, key: &str, value: &[u8]) -> Result<()> {
        let version = self.key_provider.current_version();
        if version == 0 {
            return Err("DataAtRestKeyProvider.current_version is 0; must be in [1, 255]".into());
        }
        let key_bytes = self
            .key_provider
            .get_key(version)
            .ok_or_else(|| {
                format!(
                    "DataAtRestKeyProvider returned None for its own current_version={}",
                    version
                )
            })?;
        if key_bytes.len() != AES_KEY_LEN {
            return Err(format!(
                "DataAtRestKeyProvider returned a {}-byte key; AES-256 needs {}",
                key_bytes.len(),
                AES_KEY_LEN
            )
            .into());
        }

        let mut nonce_bytes = [0u8; NONCE_LEN];
        rand::thread_rng().fill_bytes(&mut nonce_bytes);

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&key_bytes));
        let ct_and_tag = cipher
            .encrypt(Nonce::from_slice(&nonce_bytes), value)
            .map_err(|e| format!("AES-GCM encrypt failed: {}", e))?;

        let mut blob = Vec::with_capacity(VERSION_HEADER_LEN + NONCE_LEN + ct_and_tag.len());
        blob.push(version);
        blob.extend_from_slice(&nonce_bytes);
        blob.extend_from_slice(&ct_and_tag);

        self.inner.put(key, &blob).await
    }

    async fn remove(&self, key: &str) -> Result<()> {
        self.inner.remove(key).await
    }

    async fn list_keys(&self, prefix: Option<&str>) -> Result<Vec<String>> {
        self.inner.list_keys(prefix).await
    }
}
