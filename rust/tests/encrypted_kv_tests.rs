// SPDX-License-Identifier: MIT

//! Integration tests for `EncryptedKeyValueStore` and the data-at-rest key
//! providers. Mirrors the C# `EncryptedKeyValueStoreTests` and
//! `PbkdfDataAtRestKeyProviderTests`.

use std::sync::Arc;

use aether_protocol::{
    DataAtRestKeyProvider, DerivedDataAtRestKeyProvider, EncryptedKeyValueStore,
    InMemoryKeyValueStore, KeyValueStore, StaticDataAtRestKeyProvider,
};

fn key_with_value(b: u8) -> [u8; 32] {
    let mut a = [0u8; 32];
    for x in a.iter_mut() {
        *x = b;
    }
    a
}

fn make_pair(
    key_byte: u8,
) -> (
    Arc<dyn KeyValueStore>,
    Arc<dyn DataAtRestKeyProvider>,
) {
    let inner: Arc<dyn KeyValueStore> = Arc::new(InMemoryKeyValueStore::new());
    let provider: Arc<dyn DataAtRestKeyProvider> = Arc::new(
        StaticDataAtRestKeyProvider::single(&key_with_value(key_byte)).unwrap(),
    );
    (inner, provider)
}

#[tokio::test]
async fn encrypted_roundtrip_returns_original_bytes() {
    let (inner, provider) = make_pair(0x42);
    let store = EncryptedKeyValueStore::new(inner.clone(), provider);
    store.put("k", b"hello world").await.unwrap();
    assert_eq!(
        store.get("k").await.unwrap().as_deref(),
        Some(&b"hello world"[..])
    );
}

#[tokio::test]
async fn ciphertext_is_actually_encrypted_in_inner() {
    let (inner, provider) = make_pair(0x42);
    let store = EncryptedKeyValueStore::new(inner.clone(), provider);
    let plaintext = b"sensitive material";
    store.put("k", plaintext).await.unwrap();
    let raw = inner.get("k").await.unwrap().expect("stored");
    // First byte is version, rest should be unrecognisable as plaintext.
    assert_eq!(raw[0], 1, "version byte should be 1");
    assert!(
        !raw[1..].windows(plaintext.len()).any(|w| w == plaintext),
        "plaintext must not appear in ciphertext"
    );
    assert!(
        raw.len() >= 1 + 12 + plaintext.len() + 16,
        "blob must contain version + nonce + ciphertext + tag (got {} bytes)",
        raw.len()
    );
}

#[tokio::test]
async fn wrong_key_returns_none() {
    let (inner, provider) = make_pair(0x42);
    let store_a = EncryptedKeyValueStore::new(inner.clone(), provider);
    store_a.put("k", b"secret").await.unwrap();

    let other: Arc<dyn DataAtRestKeyProvider> =
        Arc::new(StaticDataAtRestKeyProvider::single(&key_with_value(0x77)).unwrap());
    let store_b = EncryptedKeyValueStore::new(inner.clone(), other);
    assert!(
        store_b.get("k").await.unwrap().is_none(),
        "decryption with the wrong key must return None, not surface a panic"
    );
}

#[tokio::test]
async fn tampered_blob_returns_none() {
    let (inner, provider) = make_pair(0x42);
    let store = EncryptedKeyValueStore::new(inner.clone(), provider);
    store.put("k", b"secret").await.unwrap();

    // Mutate the ciphertext byte (skip the version + nonce header).
    let mut blob = inner.get("k").await.unwrap().unwrap();
    let last = blob.len() - 1;
    blob[last] ^= 0xff;
    inner.put("k", &blob).await.unwrap();

    assert!(store.get("k").await.unwrap().is_none());
}

#[tokio::test]
async fn version_rotation_keeps_old_blobs_decryptable() {
    let inner: Arc<dyn KeyValueStore> = Arc::new(InMemoryKeyValueStore::new());

    // Write under version 1.
    let p1: Arc<dyn DataAtRestKeyProvider> =
        Arc::new(StaticDataAtRestKeyProvider::single(&key_with_value(0x11)).unwrap());
    let store_v1 = EncryptedKeyValueStore::new(inner.clone(), p1);
    store_v1.put("a", b"under-v1").await.unwrap();
    drop(store_v1);

    // Now flip to a multi-version provider that knows v1 + v2 with v2 active.
    let p2: Arc<dyn DataAtRestKeyProvider> = Arc::new(
        StaticDataAtRestKeyProvider::multi(
            vec![(1u8, key_with_value(0x11).to_vec()), (2u8, key_with_value(0x22).to_vec())],
            2,
        )
        .unwrap(),
    );
    let store_v2 = EncryptedKeyValueStore::new(inner.clone(), p2);
    // v1 blobs still decrypt.
    assert_eq!(
        store_v2.get("a").await.unwrap().as_deref(),
        Some(&b"under-v1"[..])
    );
    // New writes go under v2.
    store_v2.put("b", b"under-v2").await.unwrap();
    let raw_b = inner.get("b").await.unwrap().unwrap();
    assert_eq!(raw_b[0], 2, "new writes use the current version byte");
}

#[tokio::test]
async fn rewrap_moves_all_values_to_current_version() {
    let inner: Arc<dyn KeyValueStore> = Arc::new(InMemoryKeyValueStore::new());

    // Write a few values under v1.
    let p1: Arc<dyn DataAtRestKeyProvider> =
        Arc::new(StaticDataAtRestKeyProvider::single(&key_with_value(0x11)).unwrap());
    let store_v1 = EncryptedKeyValueStore::new(inner.clone(), p1);
    store_v1.put("a", b"a-value").await.unwrap();
    store_v1.put("b", b"b-value").await.unwrap();
    drop(store_v1);

    // Rotate to v2 and rewrap.
    let p2: Arc<dyn DataAtRestKeyProvider> = Arc::new(
        StaticDataAtRestKeyProvider::multi(
            vec![(1u8, key_with_value(0x11).to_vec()), (2u8, key_with_value(0x22).to_vec())],
            2,
        )
        .unwrap(),
    );
    let store_v2 = EncryptedKeyValueStore::new(inner.clone(), p2);
    let count = store_v2.rewrap().await.unwrap();
    assert_eq!(count, 2);

    // After rewrap, every blob is on v2.
    let raw_a = inner.get("a").await.unwrap().unwrap();
    let raw_b = inner.get("b").await.unwrap().unwrap();
    assert_eq!(raw_a[0], 2);
    assert_eq!(raw_b[0], 2);

    // Plaintexts are still recoverable.
    assert_eq!(
        store_v2.get("a").await.unwrap().as_deref(),
        Some(&b"a-value"[..])
    );
    assert_eq!(
        store_v2.get("b").await.unwrap().as_deref(),
        Some(&b"b-value"[..])
    );
}

// ===== Derived (PBKDF2) key provider tests =====

#[test]
fn derived_provider_is_deterministic() {
    let salt = b"this-is-the-test-salt-16+";
    let p1 = DerivedDataAtRestKeyProvider::new("pass", salt, 1_000).unwrap();
    let p2 = DerivedDataAtRestKeyProvider::new("pass", salt, 1_000).unwrap();
    assert_eq!(p1.get_key(1), p2.get_key(1));
}

#[test]
fn derived_provider_differs_on_different_salt() {
    let p1 = DerivedDataAtRestKeyProvider::new("pass", b"the-first-test-salt-x", 1_000).unwrap();
    let p2 = DerivedDataAtRestKeyProvider::new("pass", b"another-test-salt-okay", 1_000).unwrap();
    assert_ne!(
        p1.get_key(1),
        p2.get_key(1),
        "different salts MUST produce different derived keys"
    );
}

#[test]
fn derived_provider_rejects_short_salt() {
    let r = DerivedDataAtRestKeyProvider::new("pass", b"short", 1_000);
    assert!(r.is_err());
}

#[test]
fn derived_provider_rejects_empty_passphrase() {
    let r = DerivedDataAtRestKeyProvider::new("", b"this-is-the-test-salt-16+", 1_000);
    assert!(r.is_err());
}

#[test]
fn derived_with_rotation_keeps_old_versions() {
    let p1 = DerivedDataAtRestKeyProvider::new("pass1", b"the-first-test-salt-x", 1_000).unwrap();
    let p2 = p1
        .with_rotation(2, "pass2", b"the-second-test-salt-y", Some(1_000))
        .unwrap();
    assert_eq!(p2.current_version(), 2);
    assert!(p2.get_key(1).is_some());
    assert!(p2.get_key(2).is_some());
    assert_ne!(p2.get_key(1), p2.get_key(2));
}
