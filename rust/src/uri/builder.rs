// SPDX-License-Identifier: MIT

//! Fluent builder for [`AetherUri`].
//!
//! Use when programmatically constructing an Aether URI from parts; for
//! parsing an existing string, use [`AetherUri::parse`].
//!
//! # Example
//!
//! ```
//! use aethernet_protocol::uri::AetherUriBuilder;
//!
//! let uri = AetherUriBuilder::new()
//!     .authority("KXJB7-MN2P4")
//!     .path("content/sha256-abc123")
//!     .query("codec", "opus")
//!     .fragment("t=1m30s")
//!     .build()
//!     .unwrap();
//!
//! assert_eq!(
//!     uri.to_string(),
//!     "aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s"
//! );
//! ```

use std::collections::BTreeMap;

use super::uri::{AetherUri, AetherUriError, SCHEME};

/// Fluent builder for [`AetherUri`]. Construct one with [`AetherUriBuilder::new`].
#[derive(Debug, Clone, Default)]
pub struct AetherUriBuilder {
    authority: Option<String>,
    path: String,
    query: BTreeMap<String, String>,
    fragment: String,
}

impl AetherUriBuilder {
    /// Creates a new, empty builder.
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets the authority from a raw string (AetherTag or 64-char hex UHID).
    ///
    /// The value is validated and canonicalised on [`build`](Self::build).
    pub fn authority(mut self, authority: &str) -> Self {
        self.authority = Some(authority.to_string());
        self
    }

    /// Sets the path component. A leading `/` is stripped if present.
    pub fn path(mut self, path: &str) -> Self {
        self.path = path.trim_start_matches('/').to_string();
        self
    }

    /// Appends a single segment to the path.
    ///
    /// Empty segments are ignored. Leading slashes on the segment are stripped.
    pub fn append_segment(mut self, segment: &str) -> Self {
        let trimmed = segment.trim_start_matches('/');
        if trimmed.is_empty() {
            return self;
        }
        if self.path.is_empty() {
            self.path = trimmed.to_string();
        } else {
            self.path.push('/');
            self.path.push_str(trimmed);
        }
        self
    }

    /// Adds or replaces a query parameter. Keys are stored case-insensitively
    /// (the lower-case form is used internally and on emission).
    pub fn query(mut self, key: &str, value: &str) -> Self {
        // Empty keys cannot be expressed in the wire format — silently no-op
        // here and let `build` surface the error via the parser round-trip.
        if key.is_empty() {
            return self;
        }
        self.query
            .insert(key.to_ascii_lowercase(), value.to_string());
        self
    }

    /// Removes a query parameter by key (case-insensitive).
    pub fn remove_query(mut self, key: &str) -> Self {
        self.query.remove(&key.to_ascii_lowercase());
        self
    }

    /// Sets the fragment. A leading `#` is stripped if present.
    pub fn fragment(mut self, fragment: &str) -> Self {
        self.fragment = fragment.trim_start_matches('#').to_string();
        self
    }

    /// Builds the final [`AetherUri`].
    ///
    /// The builder serialises its current state to an `aether://` string and
    /// re-parses it; this guarantees the result is canonical and validates
    /// every component the same way `AetherUri::parse` does.
    pub fn build(self) -> Result<AetherUri, AetherUriError> {
        if self.authority.as_ref().map_or(true, String::is_empty) {
            return Err(AetherUriError::AuthorityRequired);
        }
        let s = self.to_uri_string();
        AetherUri::parse(&s)
    }

    /// Returns the URI string this builder currently represents.
    ///
    /// Unlike [`build`](Self::build), this performs no validation. Used
    /// internally to feed the parser; exposed for debugging.
    pub fn to_uri_string(&self) -> String {
        let authority = match &self.authority {
            Some(a) => a.as_str(),
            None => return String::new(),
        };
        if authority.is_empty() {
            return String::new();
        }
        let mut s = String::with_capacity(64);
        s.push_str(SCHEME);
        s.push_str("://");
        s.push_str(authority);
        if !self.path.is_empty() {
            s.push('/');
            s.push_str(&self.path);
        }
        if !self.query.is_empty() {
            s.push('?');
            let mut first = true;
            for (k, v) in &self.query {
                if !first {
                    s.push('&');
                }
                first = false;
                s.push_str(k);
                if !v.is_empty() {
                    s.push('=');
                    s.push_str(v);
                }
            }
        }
        if !self.fragment.is_empty() {
            s.push('#');
            s.push_str(&self.fragment);
        }
        s
    }
}
