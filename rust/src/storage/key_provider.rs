// SPDX-License-Identifier: MIT

//! AES-256 master-key providers for [`super::EncryptedKeyValueStore`].
//!
//! This file is the interface + the trivial static provider; the
//! PBKDF2-derived provider lives in the same module to keep the rotation
//! window logic together. The encryption-at-rest wrapper that consumes them
//! is in [`super::encrypted_kv`].

use sha2::Sha256;
use std::collections::HashMap;

/// AES-256 key length in bytes.
pub const AES_KEY_LEN: usize = 32;

/// Minimum salt length accepted by [`DerivedDataAtRestKeyProvider`].
pub const MIN_SALT_LEN: usize = 16;

/// OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.
pub const DEFAULT_PBKDF2_ITERATIONS: u32 = 600_000;

/// Supplies the AES-256 master key(s) used by
/// [`super::EncryptedKeyValueStore`] to encrypt and decrypt persisted values
/// at rest.
///
/// Two responsibilities:
///   * [`DataAtRestKeyProvider::current_version`] tells the wrapper which key
///     version to stamp onto every newly-written blob.
///   * [`DataAtRestKeyProvider::get_key`] hands back the 32-byte AES-256 key
///     for a given version on read; during a rotation window the provider
///     keeps both the old and new keys so previously-written blobs continue
///     to decrypt.
///
/// All keys returned by `get_key` MUST be exactly 32 bytes (AES-256).
pub trait DataAtRestKeyProvider: Send + Sync {
    /// The key version stamped onto every blob written via this provider.
    /// Must be in `[1, 255]` to fit the single-byte version header.
    fn current_version(&self) -> u8;

    /// Returns the 32-byte AES-256 key for the given `version`, or `None`
    /// if the provider has no key for that version.
    fn get_key(&self, version: u8) -> Option<[u8; AES_KEY_LEN]>;
}

/// Static provider backed by one or more pre-derived 32-byte AES-256 keys
/// supplied directly by the host.
///
/// Useful for tests, demos, and deployments that derive their key material
/// out of band (e.g. from the OS keychain, a hardware enclave, or a remote
/// KMS).
pub struct StaticDataAtRestKeyProvider {
    keys: HashMap<u8, [u8; AES_KEY_LEN]>,
    current_version: u8,
}

impl StaticDataAtRestKeyProvider {
    /// Single-version provider where `key` is the AES-256 master key and
    /// `current_version` defaults to 1.
    pub fn single(key: &[u8]) -> Result<Self, KeyProviderError> {
        let mut keys = HashMap::new();
        keys.insert(1u8, validate_key(key)?);
        Ok(Self {
            keys,
            current_version: 1,
        })
    }

    /// Multi-version provider for key-rotation deployments. Every value
    /// must be 32 bytes; `current_version` must reference a key that is
    /// present in the map and be in `[1, 255]`.
    pub fn multi<I>(
        keys_by_version: I,
        current_version: u8,
    ) -> Result<Self, KeyProviderError>
    where
        I: IntoIterator<Item = (u8, Vec<u8>)>,
    {
        if current_version == 0 {
            return Err(KeyProviderError::VersionOutOfRange(current_version));
        }
        let mut map = HashMap::new();
        for (v, k) in keys_by_version {
            if v == 0 {
                return Err(KeyProviderError::VersionOutOfRange(v));
            }
            map.insert(v, validate_key(&k)?);
        }
        if !map.contains_key(&current_version) {
            return Err(KeyProviderError::CurrentVersionMissing(current_version));
        }
        Ok(Self {
            keys: map,
            current_version,
        })
    }
}

impl DataAtRestKeyProvider for StaticDataAtRestKeyProvider {
    fn current_version(&self) -> u8 {
        self.current_version
    }

    fn get_key(&self, version: u8) -> Option<[u8; AES_KEY_LEN]> {
        self.keys.get(&version).copied()
    }
}

/// PBKDF2-HMAC-SHA256-derived provider. Caches the derived bytes for the
/// lifetime of the provider so the (relatively expensive) PBKDF2
/// computation runs exactly once per passphrase/version pair.
///
/// Production iteration count is [`DEFAULT_PBKDF2_ITERATIONS`] (600,000 —
/// OWASP 2023). Tests may pass a smaller value; never lower the default in
/// production code.
///
/// The salt MUST be at least [`MIN_SALT_LEN`] bytes and SHOULD be unique to
/// this device — reusing the same passphrase + salt across devices lets an
/// attacker who recovers the salt from one device decrypt blobs from another.
pub struct DerivedDataAtRestKeyProvider {
    derived: HashMap<u8, [u8; AES_KEY_LEN]>,
    current_version: u8,
    iterations: u32,
}

impl DerivedDataAtRestKeyProvider {
    /// Single-version provider that derives version 1 from `passphrase` and
    /// `salt` using PBKDF2-HMAC-SHA256 with `iterations` rounds.
    pub fn new(
        passphrase: &str,
        salt: &[u8],
        iterations: u32,
    ) -> Result<Self, KeyProviderError> {
        validate_inputs(passphrase, salt, iterations)?;
        let key = derive(passphrase, salt, iterations)?;
        let mut map = HashMap::new();
        map.insert(1u8, key);
        Ok(Self {
            derived: map,
            current_version: 1,
            iterations,
        })
    }

    /// Convenience constructor that uses the OWASP 2023 default of
    /// 600,000 iterations.
    pub fn with_default_iterations(
        passphrase: &str,
        salt: &[u8],
    ) -> Result<Self, KeyProviderError> {
        Self::new(passphrase, salt, DEFAULT_PBKDF2_ITERATIONS)
    }

    /// PBKDF2 iteration count this provider was constructed with.
    pub fn iterations(&self) -> u32 {
        self.iterations
    }

    /// Returns a new provider that adds a freshly derived key under
    /// `new_version` (which becomes [`Self::current_version`]) while keeping
    /// every existing version available for decryption. Use during a
    /// rotation window: hosts swap the registered provider, run
    /// [`super::EncryptedKeyValueStore::rewrap`] across the store in the
    /// background, then drop the old key on the next deploy by constructing
    /// a single-version provider on the new passphrase.
    pub fn with_rotation(
        &self,
        new_version: u8,
        new_passphrase: &str,
        new_salt: &[u8],
        iterations: Option<u32>,
    ) -> Result<Self, KeyProviderError> {
        if new_version == 0 {
            return Err(KeyProviderError::VersionOutOfRange(new_version));
        }
        if self.derived.contains_key(&new_version) {
            return Err(KeyProviderError::VersionAlreadyExists(new_version));
        }
        let iters = iterations.unwrap_or(self.iterations);
        validate_inputs(new_passphrase, new_salt, iters)?;
        let key = derive(new_passphrase, new_salt, iters)?;

        let mut next = HashMap::with_capacity(self.derived.len() + 1);
        for (v, k) in &self.derived {
            next.insert(*v, *k);
        }
        next.insert(new_version, key);
        Ok(Self {
            derived: next,
            current_version: new_version,
            iterations: iters,
        })
    }
}

impl DataAtRestKeyProvider for DerivedDataAtRestKeyProvider {
    fn current_version(&self) -> u8 {
        self.current_version
    }

    fn get_key(&self, version: u8) -> Option<[u8; AES_KEY_LEN]> {
        self.derived.get(&version).copied()
    }
}

#[derive(Debug, thiserror::Error)]
pub enum KeyProviderError {
    #[error("passphrase must not be empty")]
    EmptyPassphrase,
    #[error("salt must be at least {} bytes (got {0})", MIN_SALT_LEN)]
    SaltTooShort(usize),
    #[error("PBKDF2 iteration count must be positive (got {0})")]
    NonPositiveIterations(u32),
    #[error("data-at-rest key must be exactly 32 bytes (got {0})")]
    InvalidKeyLength(usize),
    #[error("key version {0} is outside the supported [1, 255] range")]
    VersionOutOfRange(u8),
    #[error("keys map does not contain an entry for currentVersion={0}")]
    CurrentVersionMissing(u8),
    #[error("version {0} already exists in this provider")]
    VersionAlreadyExists(u8),
    #[error("PBKDF2 derivation failed: {0}")]
    DerivationFailure(String),
}

fn validate_key(key: &[u8]) -> Result<[u8; AES_KEY_LEN], KeyProviderError> {
    if key.len() != AES_KEY_LEN {
        return Err(KeyProviderError::InvalidKeyLength(key.len()));
    }
    let mut copy = [0u8; AES_KEY_LEN];
    copy.copy_from_slice(key);
    Ok(copy)
}

fn validate_inputs(passphrase: &str, salt: &[u8], iterations: u32) -> Result<(), KeyProviderError> {
    if passphrase.is_empty() {
        return Err(KeyProviderError::EmptyPassphrase);
    }
    if salt.len() < MIN_SALT_LEN {
        return Err(KeyProviderError::SaltTooShort(salt.len()));
    }
    if iterations == 0 {
        return Err(KeyProviderError::NonPositiveIterations(iterations));
    }
    Ok(())
}

fn derive(
    passphrase: &str,
    salt: &[u8],
    iterations: u32,
) -> Result<[u8; AES_KEY_LEN], KeyProviderError> {
    let mut out = [0u8; AES_KEY_LEN];
    pbkdf2::pbkdf2_hmac::<Sha256>(passphrase.as_bytes(), salt, iterations, &mut out);
    Ok(out)
}
