// SPDX-License-Identifier: MIT

//! Resolves rotating ERID ([`super::ephemeral_routing_id`]) wire addresses to and from
//! the stable peer identities behind them — the piece that lets an ESTABLISHED
//! relationship follow a peer's rotating address while an outsider cannot.
//!
//! A node derives its OWN secret routing key once (via [`super::derive_routing_key`]) and
//! shares it with a peer INSIDE the established Signal session — never on the wire. Each
//! side stores the other's routing key here, so either can compute the other's current
//! ERID for addressing, and reverse-resolve an inbound ERID back to the peer it belongs
//! to. An outsider holds no routing key and can do neither. Port of the C# reference
//! (`src/AetherNet.Core/Identity/EridDirectory.cs`).

use std::collections::HashMap;

use super::ephemeral_routing_id::{
    derive, EphemeralRoutingIdError, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH,
};

/// In-memory directory mapping peer UHIDs to their secret routing keys.
#[derive(Debug, Clone)]
pub struct EridDirectory {
    my_routing_key: Vec<u8>,
    epoch_seconds: i64,
    erid_length: usize,
    peer_keys: HashMap<String, Vec<u8>>,
}

impl EridDirectory {
    /// Create a directory for a node holding `my_routing_key`, using the default rotation
    /// window and ERID length.
    ///
    /// # Panics
    /// Panics if `my_routing_key` is empty — a programming error; derive it first via
    /// [`super::derive_routing_key`].
    #[must_use]
    pub fn new(my_routing_key: &[u8]) -> Self {
        Self::with_params(my_routing_key, DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH)
    }

    /// Create a directory with explicit rotation parameters.
    ///
    /// # Panics
    /// Panics if `my_routing_key` is empty or `epoch_seconds <= 0`.
    #[must_use]
    pub fn with_params(my_routing_key: &[u8], epoch_seconds: i64, erid_length: usize) -> Self {
        assert!(!my_routing_key.is_empty(), "my_routing_key cannot be empty");
        assert!(epoch_seconds > 0, "epoch_seconds must be positive");
        Self {
            my_routing_key: my_routing_key.to_vec(),
            epoch_seconds,
            erid_length,
            peer_keys: HashMap::new(),
        }
    }

    /// Our own current ERID for the epoch containing `unix_seconds` — the address we
    /// present on the wire this window.
    ///
    /// # Errors
    /// Propagates [`EphemeralRoutingIdError`] from the underlying derivation.
    pub fn my_erid(&self, unix_seconds: i64) -> Result<String, EphemeralRoutingIdError> {
        derive(&self.my_routing_key, unix_seconds, self.epoch_seconds, self.erid_length)
    }

    /// Store a peer's routing key, learned inside an established session. Idempotent; a
    /// later call replaces an earlier key for the same peer.
    ///
    /// # Panics
    /// Panics if `peer_uhid` or `peer_routing_key` is empty.
    pub fn remember_peer(&mut self, peer_uhid: &str, peer_routing_key: &[u8]) {
        assert!(!peer_uhid.is_empty(), "peer_uhid cannot be empty");
        assert!(!peer_routing_key.is_empty(), "peer_routing_key cannot be empty");
        self.peer_keys
            .insert(peer_uhid.to_string(), peer_routing_key.to_vec());
    }

    /// Forget a peer (session torn down / excommunicated). Returns `false` if unknown.
    pub fn forget_peer(&mut self, peer_uhid: &str) -> bool {
        self.peer_keys.remove(peer_uhid).is_some()
    }

    /// The current ERID a known peer presents this epoch, or `None` if we hold no key
    /// for them.
    ///
    /// # Errors
    /// Propagates [`EphemeralRoutingIdError`] from the underlying derivation.
    pub fn erid_for_peer(
        &self,
        peer_uhid: &str,
        unix_seconds: i64,
    ) -> Result<Option<String>, EphemeralRoutingIdError> {
        match self.peer_keys.get(peer_uhid) {
            Some(key) => Ok(Some(derive(
                key,
                unix_seconds,
                self.epoch_seconds,
                self.erid_length,
            )?)),
            None => Ok(None),
        }
    }

    /// Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the
    /// given epoch, or `None` if no known peer currently presents it. O(n) over known
    /// peers — a node's actual relationship count.
    ///
    /// # Errors
    /// Propagates [`EphemeralRoutingIdError`] from the underlying derivation.
    pub fn resolve_peer(
        &self,
        erid: &str,
        unix_seconds: i64,
    ) -> Result<Option<String>, EphemeralRoutingIdError> {
        if erid.is_empty() {
            return Ok(None);
        }
        for (uhid, key) in &self.peer_keys {
            let candidate = derive(key, unix_seconds, self.epoch_seconds, self.erid_length)?;
            if candidate == erid {
                return Ok(Some(uhid.clone()));
            }
        }
        Ok(None)
    }

    /// Number of peers whose routing key we currently hold.
    #[must_use]
    pub fn known_peer_count(&self) -> usize {
        self.peer_keys.len()
    }
}
