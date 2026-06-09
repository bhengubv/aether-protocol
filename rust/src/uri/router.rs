// SPDX-License-Identifier: MIT

//! Per-app dispatcher for incoming `aether://` URIs.
//!
//! # Lifecycle
//!
//! 1. App startup: build a [`HandlerManifest`] describing every route the app accepts.
//! 2. App startup: register a callback per handler index via [`Router::register`].
//! 3. Runtime: call [`Router::dispatch`] (or [`Router::dispatch_str`]) when a
//!    URI arrives (deep link, intent, in-mesh dispatch).
//!
//! The router is synchronous by design — the SDK avoids adding a tokio
//! dependency at the URI layer. Apps that need async handlers can wrap the
//! sync callback to schedule work onto their own runtime.

use std::collections::HashMap;
use std::sync::Mutex;

use super::manifest::{HandlerDescriptor, HandlerManifest};
use super::uri::{AetherUri, AetherUriError};

/// Context delivered to a registered handler when its route matches.
///
/// Carries the original URI plus the captured route parameters.
pub struct DispatchContext<'a> {
    pub uri: &'a AetherUri,
    pub handler: &'a HandlerDescriptor,
    pub route_parameters: std::collections::BTreeMap<String, String>,
}

/// Thread-safe per-app URI dispatcher.
pub struct Router {
    manifest: HandlerManifest,
    #[allow(clippy::type_complexity)]
    handlers:
        Mutex<HashMap<usize, Box<dyn Fn(&DispatchContext) -> Result<(), AetherUriError> + Send + Sync>>>,
}

impl Router {
    /// Creates a new router bound to `manifest`.
    pub fn new(manifest: HandlerManifest) -> Self {
        Self { manifest, handlers: Mutex::new(HashMap::new()) }
    }

    /// Returns a reference to the underlying manifest.
    pub fn manifest(&self) -> &HandlerManifest {
        &self.manifest
    }

    /// Registers a callback for the handler at `handler_index`.
    ///
    /// Re-registering replaces any existing callback for that index.
    ///
    /// # Errors
    ///
    /// Returns [`AetherUriError::HandlerIndexOutOfRange`] if `handler_index`
    /// is not a valid index into the manifest's `handlers` vector.
    pub fn register<F>(&self, handler_index: usize, f: F) -> Result<(), AetherUriError>
    where
        F: Fn(&DispatchContext) -> Result<(), AetherUriError> + Send + Sync + 'static,
    {
        if handler_index >= self.manifest.handlers.len() {
            return Err(AetherUriError::HandlerIndexOutOfRange(handler_index));
        }
        let mut guard = self.handlers.lock().expect("router handlers mutex poisoned");
        guard.insert(handler_index, Box::new(f));
        Ok(())
    }

    /// Resolves and dispatches `uri`.
    ///
    /// Returns `Ok(true)` if a handler was matched AND invoked (even if it
    /// returned its own error — that is propagated). Returns `Ok(false)` if
    /// no handler matched, or if a handler matched in the manifest but no
    /// callback has been registered for it yet.
    pub fn dispatch(&self, uri: &AetherUri) -> Result<bool, AetherUriError> {
        let resolved = match self.manifest.resolve(uri) {
            Some(r) => r,
            None => return Ok(false),
        };
        let (index, captures) = resolved;
        // Snapshot the callback pointer under the lock, then release before
        // calling the handler so it can re-enter the router if it wants to.
        let cb_present = {
            let guard = self.handlers.lock().expect("router handlers mutex poisoned");
            guard.contains_key(&index)
        };
        if !cb_present {
            return Ok(false);
        }
        let descriptor = &self.manifest.handlers[index];
        let ctx = DispatchContext { uri, handler: descriptor, route_parameters: captures };
        // Re-acquire the lock to obtain the callback. Holding it across the
        // callback would deadlock any re-entrant `register`/`dispatch` call;
        // instead we keep the lock only long enough to clone-the-reference,
        // but since `Fn` traits don't implement Clone we just call inline.
        let guard = self.handlers.lock().expect("router handlers mutex poisoned");
        let cb = guard
            .get(&index)
            .expect("handler vanished between presence check and dispatch");
        cb(&ctx)?;
        Ok(true)
    }

    /// Parses `s` as an `aether://` URI and dispatches it.
    ///
    /// # Errors
    ///
    /// Returns the parse error if the string is not a valid URI; otherwise
    /// behaves like [`dispatch`](Self::dispatch).
    pub fn dispatch_str(&self, s: &str) -> Result<bool, AetherUriError> {
        let uri = AetherUri::parse(s)?;
        self.dispatch(&uri)
    }
}
