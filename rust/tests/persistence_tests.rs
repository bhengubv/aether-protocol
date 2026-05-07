// SPDX-License-Identifier: MIT

//! Integration tests for the persistent SignalProtocol stores backed by
//! the on-disk filesystem KV adapter. Mirrors the C# `SignalSessionPersistenceTests`
//! and `PreKeyPersistenceTests`.
//!
//! These tests run on Linux/Mac CI. Windows MSVC has a `msvcrt.lib` link
//! issue with build scripts that doesn't affect lib-internal `#[cfg(test)]`
//! tests but does block integration tests; contributors hit that should
//! follow the troubleshooting steps in `README.md`.

use std::sync::Arc;
use tempfile::TempDir;

use aether_protocol::{
    FileSystemKeyValueStore, KvPreKeyStore, KvSignalSessionStore, KeyValueStore, PreKeyStore,
    SignalProtocolService, SignalSessionStore,
};

/// Fresh on-disk store + the temp dir that owns its lifetime.
struct DiskStore {
    _dir: TempDir,
    store: Arc<dyn KeyValueStore>,
}

fn fs_store() -> DiskStore {
    let dir = TempDir::new().expect("tempdir");
    let store: Arc<dyn KeyValueStore> = Arc::new(
        FileSystemKeyValueStore::new(dir.path(), Some("aether-test")).expect("fs kv"),
    );
    DiskStore { _dir: dir, store }
}

#[tokio::test]
async fn fs_kv_roundtrip() {
    // `..` in a destructuring pattern drops the ignored fields immediately,
    // which would delete the TempDir before the async ops run.  Bind `_dir`
    // explicitly so it lives for the whole test.
    let DiskStore { _dir, store } = fs_store();
    store.put("key", b"value").await.unwrap();
    assert_eq!(store.get("key").await.unwrap().as_deref(), Some(&b"value"[..]));
    let keys = store.list_keys(None).await.unwrap();
    assert_eq!(keys, vec!["key".to_string()]);
    store.remove("key").await.unwrap();
    assert!(store.get("key").await.unwrap().is_none());
}

#[tokio::test]
async fn fs_kv_list_with_prefix() {
    let DiskStore { _dir, store } = fs_store();
    store.put("signal:opk:1", b"a").await.unwrap();
    store.put("signal:opk:2", b"b").await.unwrap();
    store.put("other", b"c").await.unwrap();
    let mut opks = store.list_keys(Some("signal:opk:")).await.unwrap();
    opks.sort();
    assert_eq!(opks, vec!["signal:opk:1", "signal:opk:2"]);
}

/// End-to-end: alice ↔ bob exchange where bob is restarted between
/// messages and the session continues.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn session_persists_across_restart_via_filesystem() {
    let dir = TempDir::new().unwrap();
    let kv: Arc<dyn KeyValueStore> =
        Arc::new(FileSystemKeyValueStore::new(dir.path(), Some("bob")).unwrap());
    let session_store: Arc<dyn SignalSessionStore> =
        Arc::new(KvSignalSessionStore::new(kv.clone()));
    let pre_key_store: Arc<dyn PreKeyStore> = Arc::new(KvPreKeyStore::new(kv.clone()));

    let mut alice = SignalProtocolService::new();
    let mut bob = SignalProtocolService::builder()
        .with_session_store(session_store.clone())
        .with_prekey_store(pre_key_store.clone())
        .build();

    let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
    alice.generate_pre_key_bundle("alice").unwrap();
    alice.process_pre_key_bundle(&bob_bundle).unwrap();

    let m1 = alice.encrypt("bob", b"first").unwrap();
    bob.decrypt("alice", &m1).unwrap();
    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    drop(bob);

    // Rebuild bob from the same on-disk state.
    let mut bob2 = SignalProtocolService::builder()
        .with_session_store(session_store.clone())
        .with_prekey_store(pre_key_store.clone())
        .build();
    assert!(bob2.has_session("alice"));

    let m2 = alice.encrypt("bob", b"second").unwrap();
    let pt = bob2.decrypt("alice", &m2).unwrap();
    assert_eq!(pt, b"second");

    // And bob2 can reply.
    let m3 = bob2.encrypt("alice", b"reply").unwrap();
    let pt3 = alice.decrypt("bob", &m3).unwrap();
    assert_eq!(pt3, b"reply");
}

/// Identity + SPK history + OPK pool round-trip through the
/// FileSystemKeyValueStore preserves Bob's ability to receive a bundle
/// issued before a restart.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn pre_key_state_persists_across_restart_via_filesystem() {
    let dir = TempDir::new().unwrap();
    let kv: Arc<dyn KeyValueStore> =
        Arc::new(FileSystemKeyValueStore::new(dir.path(), Some("bob")).unwrap());
    let pre_key_store: Arc<dyn PreKeyStore> = Arc::new(KvPreKeyStore::new(kv.clone()));

    let mut bob = SignalProtocolService::builder()
        .with_prekey_store(pre_key_store.clone())
        .build();
    let pre_restart_bundle = bob.generate_pre_key_bundle("bob").unwrap();
    let active_spk_id = bob.active_signed_pre_key_id();
    let pool_status = bob.pre_key_pool_status();
    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    drop(bob);

    let mut bob2 = SignalProtocolService::builder()
        .with_prekey_store(pre_key_store.clone())
        .build();
    assert_eq!(bob2.active_signed_pre_key_id(), active_spk_id);
    assert_eq!(bob2.pre_key_pool_status().0, pool_status.0);

    let mut alice = SignalProtocolService::new();
    alice.generate_pre_key_bundle("alice").unwrap();
    alice.process_pre_key_bundle(&pre_restart_bundle).unwrap();
    let m = alice.encrypt("bob", b"resumed").unwrap();
    let pt = bob2.decrypt("alice", &m).unwrap();
    assert_eq!(pt, b"resumed");
}
