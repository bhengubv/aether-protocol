// SPDX-License-Identifier: MIT

//! `aether://` URI scheme — canonical addressing for resources on the Aether mesh.
//!
//! This module ports the C# reference implementation in
//! `src/AetherNet.Core/Uri/` and is byte-equivalent on the wire-visible
//! components (authority, path, fragment, individual query keys/values).
//!
//! See `docs/aether-uri-scheme.md` for the full design and ABNF grammar.
//!
//! # Quick start
//!
//! ```
//! use aethernet_protocol::uri::{AetherUri, AetherUriBuilder};
//!
//! // Parse
//! let uri = AetherUri::parse("aether://KXJB7-MN2P4/content/abc?codec=opus").unwrap();
//! assert_eq!(uri.authority(), "KXJB7-MN2P4");
//! assert_eq!(uri.path(), "content/abc");
//! assert_eq!(uri.handler_name(), "content");
//! assert_eq!(uri.query().get("codec").map(String::as_str), Some("opus"));
//!
//! // Build
//! let built = AetherUriBuilder::new()
//!     .authority("KXJB7-MN2P4")
//!     .path("content/abc")
//!     .query("codec", "opus")
//!     .build()
//!     .unwrap();
//! assert_eq!(built, uri);
//! ```

pub mod builder;
pub mod manifest;
pub mod router;
pub mod uri;

pub use builder::AetherUriBuilder;
pub use manifest::{HandlerDescriptor, HandlerManifest};
pub use router::{DispatchContext, Router};
pub use uri::{AetherUri, AetherUriError, SCHEME};

#[cfg(test)]
mod tests;
