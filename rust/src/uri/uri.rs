// SPDX-License-Identifier: MIT

//! `AetherUri` — the canonical addressing format for resources on the Aether mesh.
//!
//! # Grammar (ABNF, RFC 5234)
//!
//! ```text
//! aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
//! authority    = aether-tag / uhid
//! aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
//! uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
//! path         = path-segment *( "/" path-segment )
//! path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
//! query        = query-param *( "&" query-param )
//! query-param  = key [ "=" value ]
//! key          = 1*( unreserved / pct-encoded )
//! value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
//! fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
//! ```
//!
//! See `docs/aether-uri-scheme.md` for the full design specification.
//!
//! # Component model
//!
//! - **Authority** — the destination, either an [`AetherNetTag`](crate::identity::AetherNetTag)
//!   (10 Crockford chars, dash optional, case-insensitive) or a 64-char hex UHID.
//!   Canonicalised to upper case on parse.
//! - **Path** — opaque to the protocol; first segment names a handler.
//!   Stored decoded; case-sensitive.
//! - **Query** — handler arguments. Keys are stored lower-case for case-insensitive
//!   lookup; values preserve case.
//! - **Fragment** — a client-side hint, stored decoded.
//!
//! # Departure from C# reference
//!
//! The query is backed by a [`BTreeMap`], so canonical emission is in
//! lexicographic key order rather than the C# `Dictionary` insertion order.
//! Two URIs with the same set of `(key, value)` pairs are byte-equal on
//! [`Display`] regardless of input order. Equality is order-insensitive in
//! both impls.

use std::collections::BTreeMap;
use std::fmt;

use crate::identity::AetherNetTag;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/// The fixed scheme name — `aether`.
pub const SCHEME: &str = "aether";

/// The full scheme prefix — `aether://`.
const SCHEME_PREFIX: &str = "aether://";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/// A parsed `aether://` URI.
///
/// All components are stored decoded and canonicalised; calling [`Display`]
/// re-encodes to RFC-3986-compliant percent-encoded form. The canonical form
/// is stable: `parse(s).to_string()` and `parse(parse(s).to_string()).to_string()`
/// produce byte-equal output.
#[derive(Clone, Debug)]
pub struct AetherUri {
    authority: String,
    path: String,
    query: BTreeMap<String, String>,
    fragment: String,
}

/// Errors that can occur when parsing, building, or dispatching an `AetherUri`.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum AetherUriError {
    /// Input was empty or `None`.
    #[error("input is null or empty")]
    Empty,

    /// Input does not begin with `aether://` (case-insensitive).
    #[error("scheme must be 'aether://'")]
    InvalidScheme,

    /// The authority slot was empty (e.g. `aether:///profile`).
    #[error("authority is missing")]
    MissingAuthority,

    /// The authority was neither a valid AetherTag nor a 64-char hex UHID.
    #[error("authority '{0}' is neither a valid AetherTag nor a 64-char hex UHID")]
    InvalidAuthority(String),

    /// The path contained an empty segment (consecutive slashes).
    #[error("empty path segment (consecutive slashes)")]
    EmptyPathSegment,

    /// The path contained a character outside the allowed set.
    #[error("illegal character '{ch}' in path segment '{segment}'")]
    IllegalPathChar { ch: char, segment: String },

    /// A `%` was not followed by two hex digits.
    #[error("malformed percent-encoding at position {pos} of segment '{segment}'")]
    MalformedPercentEncoding { pos: usize, segment: String },

    /// A query parameter had an empty key (e.g. `?=value`).
    #[error("empty query parameter key")]
    EmptyQueryKey,

    /// The builder was finalised without setting an authority.
    #[error("authority is required")]
    AuthorityRequired,

    /// A handler callback returned an error or the dispatcher encountered an
    /// internal failure. The wrapped string carries the underlying message.
    #[error("dispatch failure: {0}")]
    Dispatch(String),

    /// The descriptor passed to `Router::register` was not present in the
    /// manifest, or had an out-of-range index.
    #[error("handler index {0} is out of range for the manifest")]
    HandlerIndexOutOfRange(usize),
}

// ---------------------------------------------------------------------------
// Impl: AetherUri
// ---------------------------------------------------------------------------

impl AetherUri {
    // -----------------------------------------------------------------------
    // Constructors
    // -----------------------------------------------------------------------

    /// Parses an `aether://` URI string. Returns an [`AetherUriError`] on any
    /// syntactic violation.
    ///
    /// `parse` and `try_parse` are equivalent — both return `Result`. The two
    /// names exist for symmetry with the C# `Parse` / `TryParse` API surface.
    pub fn parse(input: &str) -> Result<Self, AetherUriError> {
        parse_internal(input)
    }

    /// Alias for [`parse`](Self::parse). Returns the same `Result`.
    pub fn try_parse(input: &str) -> Result<Self, AetherUriError> {
        parse_internal(input)
    }

    // -----------------------------------------------------------------------
    // Accessors
    // -----------------------------------------------------------------------

    /// The destination authority, canonicalised (upper-case AetherTag or
    /// upper-case 64-char hex UHID).
    pub fn authority(&self) -> &str {
        &self.authority
    }

    /// The path component without the leading slash. Empty string for root.
    pub fn path(&self) -> &str {
        &self.path
    }

    /// The fragment with the leading `#` stripped. Empty if none.
    pub fn fragment(&self) -> &str {
        &self.fragment
    }

    /// The decoded query parameters. Keys are lower-case.
    pub fn query(&self) -> &BTreeMap<String, String> {
        &self.query
    }

    /// The first path segment — i.e. the handler name. Empty for root.
    pub fn handler_name(&self) -> &str {
        if self.path.is_empty() {
            ""
        } else {
            match self.path.find('/') {
                Some(i) => &self.path[..i],
                None => &self.path,
            }
        }
    }

    /// The path split into segments (already decoded). Empty for root.
    pub fn path_segments(&self) -> Vec<&str> {
        if self.path.is_empty() {
            Vec::new()
        } else {
            self.path.split('/').collect()
        }
    }
}

// ---------------------------------------------------------------------------
// Impl: Display (canonical encoder)
// ---------------------------------------------------------------------------

impl fmt::Display for AetherUri {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(SCHEME_PREFIX)?;
        f.write_str(&self.authority)?;
        if !self.path.is_empty() {
            f.write_str("/")?;
            encode_path(f, &self.path)?;
        }
        if !self.query.is_empty() {
            f.write_str("?")?;
            let mut first = true;
            // BTreeMap iterates in lexicographic key order — stable, canonical.
            for (k, v) in &self.query {
                if !first {
                    f.write_str("&")?;
                }
                first = false;
                encode_component(f, k, EncodeKind::QueryKey)?;
                if !v.is_empty() {
                    f.write_str("=")?;
                    encode_component(f, v, EncodeKind::QueryValue)?;
                }
            }
        }
        if !self.fragment.is_empty() {
            f.write_str("#")?;
            encode_component(f, &self.fragment, EncodeKind::Fragment)?;
        }
        Ok(())
    }
}

// ---------------------------------------------------------------------------
// Impl: PartialEq / Eq (query-order-insensitive by virtue of BTreeMap)
// ---------------------------------------------------------------------------

impl PartialEq for AetherUri {
    fn eq(&self, other: &Self) -> bool {
        self.authority == other.authority
            && self.path == other.path
            && self.fragment == other.fragment
            && self.query == other.query
    }
}

impl Eq for AetherUri {}

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

fn parse_internal(input: &str) -> Result<AetherUri, AetherUriError> {
    if input.is_empty() {
        return Err(AetherUriError::Empty);
    }

    // Scheme prefix is case-insensitive per RFC 3986.
    if input.len() < SCHEME_PREFIX.len()
        || !input[..SCHEME_PREFIX.len()].eq_ignore_ascii_case(SCHEME_PREFIX)
    {
        return Err(AetherUriError::InvalidScheme);
    }

    let mut rest = &input[SCHEME_PREFIX.len()..];

    // Split on the first `#` (fragment) — only one is allowed.
    let fragment = match rest.find('#') {
        Some(i) => {
            let f = percent_decode(&rest[i + 1..]);
            rest = &rest[..i];
            f
        }
        None => String::new(),
    };

    // Then split on the first `?` (query).
    let mut query: BTreeMap<String, String> = BTreeMap::new();
    if let Some(i) = rest.find('?') {
        let query_raw = &rest[i + 1..];
        rest = &rest[..i];
        for pair in query_raw.split('&') {
            if pair.is_empty() {
                continue;
            }
            let (key_raw, value_raw) = match pair.find('=') {
                Some(j) => (&pair[..j], &pair[j + 1..]),
                None => (pair, ""),
            };
            let decoded_key = percent_decode(key_raw);
            if decoded_key.is_empty() {
                return Err(AetherUriError::EmptyQueryKey);
            }
            // Keys are case-insensitive — store lower-case.
            query.insert(decoded_key.to_ascii_lowercase(), percent_decode(value_raw));
        }
    }

    // Then split on the first `/` (path).
    let (authority_raw, path_raw) = match rest.find('/') {
        Some(i) => (&rest[..i], &rest[i + 1..]),
        None => (rest, ""),
    };

    if authority_raw.is_empty() {
        return Err(AetherUriError::MissingAuthority);
    }

    let authority = canonicalise_authority(authority_raw)?;
    validate_path(path_raw)?;
    let path = percent_decode_path(path_raw);

    Ok(AetherUri { authority, path, query, fragment })
}

fn canonicalise_authority(raw: &str) -> Result<String, AetherUriError> {
    // Try UHID (64 hex chars).
    if raw.len() == 64 && is_all_hex(raw) {
        return Ok(raw.to_ascii_uppercase());
    }
    // Try AetherTag (10 Crockford chars, dash optional, case-insensitive).
    if let Ok(tag) = AetherNetTag::parse(raw) {
        return Ok(tag.value().to_string()); // already in canonical XXXXX-XXXXX upper-case.
    }
    Err(AetherUriError::InvalidAuthority(raw.to_string()))
}

fn validate_path(path: &str) -> Result<(), AetherUriError> {
    if path.is_empty() {
        return Ok(());
    }
    for segment in path.split('/') {
        if segment.is_empty() {
            return Err(AetherUriError::EmptyPathSegment);
        }
        let bytes = segment.as_bytes();
        let mut i = 0;
        while i < bytes.len() {
            let b = bytes[i];
            let c = b as char;
            if is_unreserved(c) || is_sub_delim(c) || c == ':' || c == '@' {
                i += 1;
                continue;
            }
            if c == '%' {
                if i + 2 >= bytes.len() || !is_hex_char(bytes[i + 1] as char) || !is_hex_char(bytes[i + 2] as char) {
                    return Err(AetherUriError::MalformedPercentEncoding {
                        pos: i,
                        segment: segment.to_string(),
                    });
                }
                i += 3;
                continue;
            }
            return Err(AetherUriError::IllegalPathChar {
                ch: c,
                segment: segment.to_string(),
            });
        }
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Percent-encoding tables and codecs
// ---------------------------------------------------------------------------

/// Where a character is being encoded — determines the allowed-unencoded set.
#[derive(Clone, Copy)]
enum EncodeKind {
    PathSegment,
    QueryKey,
    QueryValue,
    Fragment,
}

fn is_unreserved(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '-' || c == '.' || c == '_' || c == '~'
}

fn is_sub_delim(c: char) -> bool {
    matches!(
        c,
        '!' | '$' | '&' | '\'' | '(' | ')' | '*' | '+' | ',' | ';' | '='
    )
}

fn is_hex_char(c: char) -> bool {
    c.is_ascii_hexdigit()
}

fn is_all_hex(s: &str) -> bool {
    s.bytes().all(|b| (b as char).is_ascii_hexdigit())
}

fn is_allowed_unencoded(c: char, kind: EncodeKind) -> bool {
    if is_unreserved(c) {
        return true;
    }
    match kind {
        EncodeKind::PathSegment => is_sub_delim(c) || c == ':' || c == '@',
        EncodeKind::QueryKey => matches!(
            c,
            ':' | '@' | '!' | '$' | '\'' | '(' | ')' | '*' | '+' | ',' | ';'
        ),
        EncodeKind::QueryValue => matches!(
            c,
            ':' | '@' | '/' | '?' | '!' | '$' | '\'' | '(' | ')' | '*' | '+' | ',' | ';' | '='
        ),
        EncodeKind::Fragment => is_sub_delim(c) || c == ':' || c == '@' || c == '/' || c == '?',
    }
}

fn encode_path(f: &mut fmt::Formatter<'_>, path: &str) -> fmt::Result {
    let mut first = true;
    for segment in path.split('/') {
        if !first {
            f.write_str("/")?;
        }
        first = false;
        encode_component(f, segment, EncodeKind::PathSegment)?;
    }
    Ok(())
}

fn encode_component(f: &mut fmt::Formatter<'_>, s: &str, kind: EncodeKind) -> fmt::Result {
    for c in s.chars() {
        if is_allowed_unencoded(c, kind) {
            // Write as one char.
            // SAFETY of correctness: char::encode_utf8 with a 4-byte buffer always fits.
            let mut buf = [0u8; 4];
            f.write_str(c.encode_utf8(&mut buf))?;
            continue;
        }
        // Percent-encode each UTF-8 byte as %XX (upper-case hex).
        let mut buf = [0u8; 4];
        let s = c.encode_utf8(&mut buf);
        for &b in s.as_bytes() {
            write!(f, "%{:02X}", b)?;
        }
    }
    Ok(())
}

fn percent_decode(input: &str) -> String {
    if !input.contains('%') {
        return input.to_string();
    }
    let bytes = input.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        let b = bytes[i];
        if b == b'%' && i + 2 < bytes.len() && is_hex_char(bytes[i + 1] as char) && is_hex_char(bytes[i + 2] as char) {
            out.push((hex_value(bytes[i + 1]) << 4) | hex_value(bytes[i + 2]));
            i += 3;
        } else {
            // Pass through. UTF-8 already in `bytes`.
            out.push(b);
            i += 1;
        }
    }
    // Bytes are valid UTF-8 by construction (every non-decoded byte was UTF-8
    // and decoded bytes came from a valid percent-encoded UTF-8 sequence). If
    // a malformed mix produced invalid UTF-8 the input was malformed; fall
    // back to a lossy conversion rather than panicking.
    match String::from_utf8(out) {
        Ok(s) => s,
        Err(e) => String::from_utf8_lossy(&e.into_bytes()).into_owned(),
    }
}

fn percent_decode_path(path: &str) -> String {
    if path.is_empty() {
        return String::new();
    }
    // Decode each segment independently so `/` isn't lost.
    path.split('/')
        .map(percent_decode)
        .collect::<Vec<_>>()
        .join("/")
}

fn hex_value(b: u8) -> u8 {
    match b {
        b'0'..=b'9' => b - b'0',
        b'A'..=b'F' => b - b'A' + 10,
        b'a'..=b'f' => b - b'a' + 10,
        _ => 0, // is_hex_char gate prevents reaching here in practice.
    }
}
