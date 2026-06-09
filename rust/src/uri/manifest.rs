// SPDX-License-Identifier: MIT

//! Handler manifest — the contract between an app and the URI router.
//!
//! Each app describes the `aether://` paths it accepts via a
//! [`HandlerManifest`] containing a list of [`HandlerDescriptor`]s. The
//! [`Router`](crate::uri::Router) matches an incoming URI against this
//! manifest and dispatches to the registered callback.
//!
//! # Path template syntax
//!
//! ```text
//! "content/{hash}"             // matches /content/abc
//! "watch/{sessionId}/join"     // matches /watch/123/join
//! "profile"                    // matches /profile exactly
//! "profile/avatar"             // matches /profile/avatar exactly
//! ""                           // matches /<handlerName> exactly
//! ```

use std::collections::BTreeMap;

use super::uri::AetherUri;

/// Describes a single handler an app exposes on its `aether://` URI surface.
#[derive(Clone, Debug)]
pub struct HandlerDescriptor {
    /// Handler name — the first path segment (e.g. `"content"`, `"stream"`).
    pub name: String,

    /// Path template (e.g. `"content/{hash}"`). Empty matches `/<name>` exactly.
    ///
    /// Note: the template does NOT include the handler-name prefix — that is
    /// implied by [`name`](Self::name). See the module-level docs for examples.
    pub path_template: String,

    /// Informational: query keys the handler expects. Not enforced by the
    /// router; surfaced for documentation and developer tooling.
    pub expected_query_keys: Vec<String>,

    /// Human-readable description for diagnostics and generated docs.
    pub description: String,
}

impl HandlerDescriptor {
    /// Convenience constructor.
    pub fn new(name: impl Into<String>, path_template: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            path_template: path_template.into(),
            expected_query_keys: Vec::new(),
            description: String::new(),
        }
    }

    /// Matches an incoming URI's path against this descriptor's template.
    ///
    /// Returns the captured route parameters on success, or `None` on no match.
    /// The descriptor's [`name`](Self::name) is prepended to the template
    /// before matching.
    pub fn match_path(&self, path: &str) -> Option<BTreeMap<String, String>> {
        // Build the full template (handler name + optional template suffix).
        let full_template: String = if self.path_template.is_empty() {
            self.name.clone()
        } else {
            let trimmed = self.path_template.trim_start_matches('/');
            format!("{}/{}", self.name, trimmed)
        };

        let template_segs: Vec<&str> = full_template.split('/').collect();
        let path_segs: Vec<&str> = path.split('/').collect();
        if template_segs.len() != path_segs.len() {
            return None;
        }

        let mut captures: BTreeMap<String, String> = BTreeMap::new();
        for (t, p) in template_segs.iter().zip(path_segs.iter()) {
            // {param} placeholders capture the path segment.
            if t.len() >= 2 && t.starts_with('{') && t.ends_with('}') {
                let key = &t[1..t.len() - 1];
                // Keys are case-insensitive — store lower-case to match the C# semantics.
                captures.insert(key.to_ascii_lowercase(), (*p).to_string());
            } else if t != p {
                return None;
            }
        }
        Some(captures)
    }
}

/// An app's complete `aether://` handler manifest.
///
/// Each app registers exactly one manifest at startup; the
/// [`Router`](crate::uri::Router) dispatches against it.
#[derive(Clone, Debug)]
pub struct HandlerManifest {
    /// The owning app's identifier (e.g. `"aether.media"`, `"aether.txtme"`).
    pub app_id: String,

    /// All registered handler descriptors. Order matters: the first match wins.
    pub handlers: Vec<HandlerDescriptor>,
}

impl HandlerManifest {
    /// Creates a new manifest.
    pub fn new(app_id: impl Into<String>, handlers: Vec<HandlerDescriptor>) -> Self {
        Self { app_id: app_id.into(), handlers }
    }

    /// Resolves an incoming URI against this manifest.
    ///
    /// Returns `(handler_index, captures)` if a descriptor matched, or `None`
    /// otherwise. The index is into the [`handlers`](Self::handlers) vector;
    /// returning an index rather than a reference keeps the API
    /// lifetime-free, which simplifies router state-management.
    pub fn resolve(&self, uri: &AetherUri) -> Option<(usize, BTreeMap<String, String>)> {
        for (i, h) in self.handlers.iter().enumerate() {
            if h.name != uri.handler_name() {
                continue;
            }
            if let Some(captures) = h.match_path(uri.path()) {
                return Some((i, captures));
            }
        }
        None
    }
}
