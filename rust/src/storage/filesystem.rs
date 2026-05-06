// SPDX-License-Identifier: MIT

//! Durable [`KeyValueStore`] backed by one file per entry in a configurable
//! root directory. Writes are atomic on the local filesystem: bytes go to a
//! temp file inside the same directory and are then renamed over the target.
//!
//! Keys are sanitized to a hex SHA-256 hash (with the original key recoverable
//! from a sidecar manifest file) so arbitrary key strings — including paths,
//! slashes, and Unicode — round-trip safely on every host OS.
//!
//! This is a simple reference impl, not a database: it doesn't compact,
//! doesn't transact across multiple keys, and has no encryption-at-rest.
//! Hosts that want any of those wrap this with [`super::EncryptedKeyValueStore`]
//! or supply their own [`KeyValueStore`].

use async_trait::async_trait;
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use tokio::fs;

use super::kv::{KeyValueStore, Result};

const ENTRY_SUFFIX: &str = ".kv";
const TEMP_SUFFIX: &str = ".tmp";
const KEY_MANIFEST_SUFFIX: &str = ".key";

/// Filesystem-backed [`KeyValueStore`]. Multiple stores can share a parent
/// root with disjoint namespaces.
pub struct FileSystemKeyValueStore {
    root: PathBuf,
}

impl FileSystemKeyValueStore {
    /// Create a store rooted at `root_directory`. The directory is created
    /// if it does not exist. If `namespace` is supplied, it becomes a
    /// subdirectory under the root.
    pub fn new<P: AsRef<Path>>(root_directory: P, namespace: Option<&str>) -> Result<Self> {
        let mut root = root_directory.as_ref().to_path_buf();
        if let Some(ns) = namespace {
            if !ns.is_empty() {
                root.push(ns);
            }
        }
        std::fs::create_dir_all(&root)?;
        Ok(Self { root })
    }

    fn entry_path(&self, key: &str) -> PathBuf {
        let mut path = self.root.clone();
        path.push(format!("{}{}", hash_key(key), ENTRY_SUFFIX));
        path
    }

    fn manifest_path(entry: &Path) -> PathBuf {
        let mut p = entry.as_os_str().to_owned();
        p.push(KEY_MANIFEST_SUFFIX);
        PathBuf::from(p)
    }

    fn temp_path(entry: &Path) -> PathBuf {
        let mut p = entry.as_os_str().to_owned();
        p.push(TEMP_SUFFIX);
        PathBuf::from(p)
    }
}

#[async_trait]
impl KeyValueStore for FileSystemKeyValueStore {
    async fn get(&self, key: &str) -> Result<Option<Vec<u8>>> {
        let path = self.entry_path(key);
        match fs::read(&path).await {
            Ok(bytes) => Ok(Some(bytes)),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    async fn put(&self, key: &str, value: &[u8]) -> Result<()> {
        let entry = self.entry_path(key);
        let temp = Self::temp_path(&entry);

        // Write to temp file in the same directory, then atomic rename.
        fs::write(&temp, value).await?;
        // tokio::fs::rename overwrites on Unix; on Windows it errors if the
        // destination exists, so remove first if needed.
        if cfg!(windows) {
            let _ = fs::remove_file(&entry).await; // best-effort
        }
        fs::rename(&temp, &entry).await?;

        // Sidecar manifest: stash the original key so list_keys can recover
        // it. Only written on first put — subsequent puts overwrite the
        // entry but leave the manifest intact.
        let manifest = Self::manifest_path(&entry);
        if fs::try_exists(&manifest).await.unwrap_or(false) {
            return Ok(());
        }
        fs::write(&manifest, key.as_bytes()).await?;
        Ok(())
    }

    async fn remove(&self, key: &str) -> Result<()> {
        let entry = self.entry_path(key);
        // Best-effort: missing files aren't an error.
        match fs::remove_file(&entry).await {
            Ok(_) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(e) => return Err(e.into()),
        }
        let manifest = Self::manifest_path(&entry);
        match fs::remove_file(&manifest).await {
            Ok(_) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e.into()),
        }
    }

    async fn list_keys(&self, prefix: Option<&str>) -> Result<Vec<String>> {
        let mut keys = Vec::new();
        let mut read = match fs::read_dir(&self.root).await {
            Ok(r) => r,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(keys),
            Err(e) => return Err(e.into()),
        };

        let manifest_tail = format!("{}{}", ENTRY_SUFFIX, KEY_MANIFEST_SUFFIX);
        while let Some(entry) = read.next_entry().await? {
            let name = entry.file_name();
            let name_str = match name.to_str() {
                Some(s) => s,
                None => continue,
            };
            if !name_str.ends_with(&manifest_tail) {
                continue;
            }
            let bytes = match fs::read(entry.path()).await {
                Ok(b) => b,
                Err(_) => continue,
            };
            let key = match String::from_utf8(bytes) {
                Ok(s) => s,
                Err(_) => continue,
            };
            if let Some(p) = prefix {
                if !key.starts_with(p) {
                    continue;
                }
            }
            keys.push(key);
        }
        Ok(keys)
    }
}

/// SHA-256 → lowercase hex makes a filesystem-safe, fixed-length filename
/// for any input. Matches the C# `FileSystemKeyValueStore.HashKey`.
fn hash_key(key: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(key.as_bytes());
    let digest = hasher.finalize();
    let mut s = String::with_capacity(digest.len() * 2);
    for b in digest.iter() {
        s.push_str(&format!("{:02x}", b));
    }
    s
}
