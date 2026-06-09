// SPDX-License-Identifier: MIT

//! Unit tests for the `aether://` URI module.
//!
//! Covers parsing (valid + invalid), the builder, manifest resolution, and
//! the router. Mirrors the C# test surface at
//! `tests/AetherNet.Core.Tests/Uri/`.
//!
//! The cross-language byte-equal corpus is also driven from here via
//! `include_str!` so the test runs without any extra build steps.

use std::sync::{Arc, Mutex};

use super::builder::AetherUriBuilder;
use super::manifest::{HandlerDescriptor, HandlerManifest};
use super::router::Router;
use super::uri::{AetherUri, AetherUriError};

// ---------------------------------------------------------------------------
// Parse: valid forms
// ---------------------------------------------------------------------------

#[test]
fn parse_authority_only() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4").unwrap();
    assert_eq!(u.authority(), "KXJB7-MN2P4");
    assert_eq!(u.path(), "");
    assert_eq!(u.handler_name(), "");
    assert!(u.query().is_empty());
    assert_eq!(u.fragment(), "");
}

#[test]
fn parse_authority_without_dash_canonicalises() {
    let u = AetherUri::parse("aether://KXJB7MN2P4").unwrap();
    assert_eq!(u.authority(), "KXJB7-MN2P4");
}

#[test]
fn parse_authority_lowercase_canonicalises() {
    let u = AetherUri::parse("aether://kxjb7-mn2p4").unwrap();
    assert_eq!(u.authority(), "KXJB7-MN2P4");
}

#[test]
fn parse_single_segment_path() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/profile").unwrap();
    assert_eq!(u.path(), "profile");
    assert_eq!(u.handler_name(), "profile");
    assert_eq!(u.path_segments(), vec!["profile"]);
}

#[test]
fn parse_two_segment_path() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/content/sha256-abc123").unwrap();
    assert_eq!(u.path(), "content/sha256-abc123");
    assert_eq!(u.handler_name(), "content");
    assert_eq!(u.path_segments(), vec!["content", "sha256-abc123"]);
}

#[test]
fn parse_with_query() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128").unwrap();
    assert_eq!(u.query().get("codec").map(String::as_str), Some("opus"));
    assert_eq!(u.query().get("bitrate").map(String::as_str), Some("128"));
}

#[test]
fn parse_with_fragment() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/stream/live#t=1m30s").unwrap();
    assert_eq!(u.fragment(), "t=1m30s");
}

#[test]
fn parse_query_and_fragment() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/x?a=b#frag").unwrap();
    assert_eq!(u.query().get("a").map(String::as_str), Some("b"));
    assert_eq!(u.fragment(), "frag");
}

#[test]
fn parse_flag_query() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/x?flag").unwrap();
    assert_eq!(u.query().get("flag").map(String::as_str), Some(""));
}

#[test]
fn parse_uhid_64hex_upper() {
    let u = AetherUri::parse(
        "aether://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/inbox",
    )
    .unwrap();
    assert_eq!(
        u.authority(),
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    );
}

#[test]
fn parse_percent_encoded_query_space() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/inbox?title=hello%20world").unwrap();
    assert_eq!(u.query().get("title").map(String::as_str), Some("hello world"));
}

#[test]
fn parse_percent_encoded_path_segment() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/inbox/Hello%20World").unwrap();
    assert_eq!(u.path(), "inbox/Hello World");
    assert_eq!(u.path_segments(), vec!["inbox", "Hello World"]);
}

#[test]
fn parse_percent_encoded_utf8() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9").unwrap();
    assert_eq!(u.query().get("title").map(String::as_str), Some("café"));
}

#[test]
fn parse_scheme_case_insensitive() {
    let u = AetherUri::parse("AETHER://KXJB7-MN2P4/profile").unwrap();
    assert_eq!(u.path(), "profile");
    assert!(u.to_string().starts_with("aether://"));
}

#[test]
fn parse_fragment_with_equals() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/x#t=1m30s").unwrap();
    assert_eq!(u.fragment(), "t=1m30s");
}

// ---------------------------------------------------------------------------
// Parse: invalid forms
// ---------------------------------------------------------------------------

#[test]
fn parse_rejects_empty_string() {
    assert_eq!(AetherUri::parse(""), Err(AetherUriError::Empty));
}

#[test]
fn parse_rejects_wrong_scheme() {
    assert_eq!(
        AetherUri::parse("http://KXJB7-MN2P4/"),
        Err(AetherUriError::InvalidScheme)
    );
}

#[test]
fn parse_rejects_missing_slashslash() {
    assert_eq!(
        AetherUri::parse("aether:KXJB7-MN2P4"),
        Err(AetherUriError::InvalidScheme)
    );
}

#[test]
fn parse_rejects_single_slash() {
    assert_eq!(
        AetherUri::parse("aether:/KXJB7-MN2P4"),
        Err(AetherUriError::InvalidScheme)
    );
}

#[test]
fn parse_rejects_empty_authority() {
    assert_eq!(
        AetherUri::parse("aether:///profile"),
        Err(AetherUriError::MissingAuthority)
    );
}

#[test]
fn parse_rejects_non_crockford_authority() {
    // 'I' is not in the Crockford alphabet.
    let r = AetherUri::parse("aether://INVALID-AUTH1/x");
    assert!(matches!(r, Err(AetherUriError::InvalidAuthority(_))));
}

#[test]
fn parse_rejects_too_short_authority() {
    let r = AetherUri::parse("aether://ABC");
    assert!(matches!(r, Err(AetherUriError::InvalidAuthority(_))));
}

#[test]
fn parse_rejects_consecutive_slashes_in_path() {
    assert_eq!(
        AetherUri::parse("aether://KXJB7-MN2P4/a//b"),
        Err(AetherUriError::EmptyPathSegment)
    );
}

#[test]
fn parse_rejects_illegal_path_char_space() {
    let r = AetherUri::parse("aether://KXJB7-MN2P4/has space");
    assert!(matches!(r, Err(AetherUriError::IllegalPathChar { ch: ' ', .. })));
}

#[test]
fn parse_rejects_malformed_percent_encoding() {
    let r = AetherUri::parse("aether://KXJB7-MN2P4/inbox/%2");
    assert!(matches!(r, Err(AetherUriError::MalformedPercentEncoding { .. })));
}

#[test]
fn parse_rejects_empty_query_key() {
    assert_eq!(
        AetherUri::parse("aether://KXJB7-MN2P4/x?=value"),
        Err(AetherUriError::EmptyQueryKey)
    );
}

// ---------------------------------------------------------------------------
// Display (canonical encoder)
// ---------------------------------------------------------------------------

#[test]
fn display_round_trip_authority_only() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4");
}

#[test]
fn display_round_trip_with_path() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/content/abc").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4/content/abc");
}

#[test]
fn display_round_trip_with_fragment() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/x#frag").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4/x#frag");
}

#[test]
fn display_encodes_path_space() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/inbox/Hello%20World").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4/inbox/Hello%20World");
}

#[test]
fn display_encodes_query_utf8() {
    let u = AetherUri::parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4/inbox?title=caf%C3%A9");
}

#[test]
fn display_query_keys_are_lexicographically_ordered() {
    // BTreeMap → emission order is alphabetical regardless of insertion order.
    let u = AetherUri::parse("aether://KXJB7-MN2P4/x?z=1&a=2&m=3").unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4/x?a=2&m=3&z=1");
}

// ---------------------------------------------------------------------------
// Equality
// ---------------------------------------------------------------------------

#[test]
fn equality_ignores_query_order() {
    let a = AetherUri::parse("aether://KXJB7-MN2P4/x?a=1&b=2").unwrap();
    let b = AetherUri::parse("aether://KXJB7-MN2P4/x?b=2&a=1").unwrap();
    assert_eq!(a, b);
}

#[test]
fn equality_distinguishes_different_paths() {
    let a = AetherUri::parse("aether://KXJB7-MN2P4/x").unwrap();
    let b = AetherUri::parse("aether://KXJB7-MN2P4/y").unwrap();
    assert_ne!(a, b);
}

// ---------------------------------------------------------------------------
// Builder
// ---------------------------------------------------------------------------

#[test]
fn builder_basic_authority_only() {
    let u = AetherUriBuilder::new().authority("KXJB7-MN2P4").build().unwrap();
    assert_eq!(u.to_string(), "aether://KXJB7-MN2P4");
}

#[test]
fn builder_full_pipeline() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .path("content/sha256-abc")
        .query("codec", "opus")
        .fragment("t=1m30s")
        .build()
        .unwrap();
    assert_eq!(
        u.to_string(),
        "aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s"
    );
}

#[test]
fn builder_append_segment() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .append_segment("content")
        .append_segment("sha256-abc")
        .build()
        .unwrap();
    assert_eq!(u.path(), "content/sha256-abc");
}

#[test]
fn builder_remove_query_works() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .path("x")
        .query("a", "1")
        .query("b", "2")
        .remove_query("a")
        .build()
        .unwrap();
    assert!(!u.query().contains_key("a"));
    assert_eq!(u.query().get("b").map(String::as_str), Some("2"));
}

#[test]
fn builder_remove_query_case_insensitive() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .path("x")
        .query("CODEC", "opus")
        .remove_query("codec")
        .build()
        .unwrap();
    assert!(u.query().is_empty());
}

#[test]
fn builder_requires_authority() {
    let r = AetherUriBuilder::new().path("profile").build();
    assert_eq!(r, Err(AetherUriError::AuthorityRequired));
}

#[test]
fn builder_path_strips_leading_slash() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .path("/profile")
        .build()
        .unwrap();
    assert_eq!(u.path(), "profile");
}

#[test]
fn builder_fragment_strips_leading_hash() {
    let u = AetherUriBuilder::new()
        .authority("KXJB7-MN2P4")
        .path("x")
        .fragment("#frag")
        .build()
        .unwrap();
    assert_eq!(u.fragment(), "frag");
}

#[test]
fn builder_rejects_invalid_authority() {
    let r = AetherUriBuilder::new().authority("INVALID").build();
    assert!(matches!(r, Err(AetherUriError::InvalidAuthority(_))));
}

// ---------------------------------------------------------------------------
// Manifest
// ---------------------------------------------------------------------------

fn sample_manifest() -> HandlerManifest {
    HandlerManifest::new(
        "aether.media",
        vec![
            HandlerDescriptor::new("profile", ""),
            HandlerDescriptor::new("profile", "avatar"),
            HandlerDescriptor::new("content", "{hash}"),
            HandlerDescriptor::new("watch", "{sessionId}/join"),
        ],
    )
}

#[test]
fn manifest_resolves_root_profile() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/profile").unwrap();
    let (i, caps) = m.resolve(&u).unwrap();
    assert_eq!(i, 0);
    assert!(caps.is_empty());
}

#[test]
fn manifest_resolves_profile_avatar() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/profile/avatar").unwrap();
    let (i, caps) = m.resolve(&u).unwrap();
    assert_eq!(i, 1);
    assert!(caps.is_empty());
}

#[test]
fn manifest_captures_route_param() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/content/sha256-abc").unwrap();
    let (i, caps) = m.resolve(&u).unwrap();
    assert_eq!(i, 2);
    assert_eq!(caps.get("hash").map(String::as_str), Some("sha256-abc"));
}

#[test]
fn manifest_captures_multi_segment_template() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/watch/sess-99/join").unwrap();
    let (i, caps) = m.resolve(&u).unwrap();
    assert_eq!(i, 3);
    assert_eq!(caps.get("sessionid").map(String::as_str), Some("sess-99"));
}

#[test]
fn manifest_returns_none_for_unknown_handler() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/unknown").unwrap();
    assert!(m.resolve(&u).is_none());
}

#[test]
fn manifest_returns_none_for_partial_template_match() {
    let m = sample_manifest();
    let u = AetherUri::parse("aether://KXJB7-MN2P4/watch/sess-99").unwrap();
    // Template is "{sessionId}/join" — missing /join → no match.
    assert!(m.resolve(&u).is_none());
}

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

#[test]
fn router_dispatches_matching_uri() {
    let router = Router::new(sample_manifest());
    let captured: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let cap_clone = captured.clone();

    router
        .register(2, move |ctx| {
            *cap_clone.lock().unwrap() =
                ctx.route_parameters.get("hash").cloned();
            Ok(())
        })
        .unwrap();

    let invoked = router
        .dispatch_str("aether://KXJB7-MN2P4/content/sha256-xyz")
        .unwrap();
    assert!(invoked);
    assert_eq!(*captured.lock().unwrap(), Some("sha256-xyz".to_string()));
}

#[test]
fn router_returns_false_for_unmatched_uri() {
    let router = Router::new(sample_manifest());
    router.register(0, |_| Ok(())).unwrap();
    let invoked = router
        .dispatch_str("aether://KXJB7-MN2P4/unknown")
        .unwrap();
    assert!(!invoked);
}

#[test]
fn router_returns_false_when_no_handler_registered() {
    let router = Router::new(sample_manifest());
    // Don't register anything.
    let invoked = router
        .dispatch_str("aether://KXJB7-MN2P4/profile")
        .unwrap();
    assert!(!invoked);
}

#[test]
fn router_rejects_out_of_range_index() {
    let router = Router::new(sample_manifest());
    let r = router.register(99, |_| Ok(()));
    assert_eq!(r, Err(AetherUriError::HandlerIndexOutOfRange(99)));
}

#[test]
fn router_propagates_handler_error() {
    let router = Router::new(sample_manifest());
    router
        .register(0, |_| Err(AetherUriError::Dispatch("kaboom".to_string())))
        .unwrap();
    let r = router.dispatch_str("aether://KXJB7-MN2P4/profile");
    assert!(matches!(r, Err(AetherUriError::Dispatch(ref msg)) if msg == "kaboom"));
}

#[test]
fn router_re_registering_replaces_callback() {
    let router = Router::new(sample_manifest());
    let counter: Arc<Mutex<u32>> = Arc::new(Mutex::new(0));
    let c1 = counter.clone();
    router.register(0, move |_| { *c1.lock().unwrap() += 10; Ok(()) }).unwrap();
    let c2 = counter.clone();
    router.register(0, move |_| { *c2.lock().unwrap() += 1; Ok(()) }).unwrap();
    router.dispatch_str("aether://KXJB7-MN2P4/profile").unwrap();
    assert_eq!(*counter.lock().unwrap(), 1);
}

#[test]
fn router_dispatch_str_returns_parse_error() {
    let router = Router::new(sample_manifest());
    let r = router.dispatch_str("not-a-uri");
    assert!(matches!(r, Err(AetherUriError::InvalidScheme)));
}

// ---------------------------------------------------------------------------
// Cross-language fixture corpus
// ---------------------------------------------------------------------------

const FIXTURE_JSON: &str = include_str!(
    concat!(env!("CARGO_MANIFEST_DIR"), "/../tests/cross-language/uri-fixtures.json")
);

#[test]
fn cross_language_corpus_loads() {
    let v: serde_json::Value = serde_json::from_str(FIXTURE_JSON).expect("corpus must be valid JSON");
    assert!(v.get("valid").and_then(|x| x.as_array()).is_some());
    assert!(v.get("invalid").and_then(|x| x.as_array()).is_some());
    assert!(v.get("manifest").is_some());
}

#[test]
fn cross_language_valid_cases_parse_to_expected_components() {
    let v: serde_json::Value = serde_json::from_str(FIXTURE_JSON).unwrap();
    let cases = v["valid"].as_array().expect("'valid' array");
    assert!(!cases.is_empty(), "expected non-empty valid corpus");

    for case in cases {
        let name = case["name"].as_str().unwrap_or("<unnamed>");
        let input = case["input"].as_str().expect("input string");
        let expected_authority = case["authority"].as_str().unwrap_or("");
        let expected_path = case["path"].as_str().unwrap_or("");
        let expected_handler = case["handlerName"].as_str().unwrap_or("");
        let expected_fragment = case["fragment"].as_str().unwrap_or("");

        let u = AetherUri::parse(input).unwrap_or_else(|e| {
            panic!("case '{}' (input {input:?}) must parse but got {e:?}", name)
        });

        assert_eq!(u.authority(), expected_authority, "[{name}] authority");
        assert_eq!(u.path(), expected_path, "[{name}] path");
        assert_eq!(u.handler_name(), expected_handler, "[{name}] handler_name");
        assert_eq!(u.fragment(), expected_fragment, "[{name}] fragment");

        // Path segments.
        let expected_segs: Vec<&str> = case["pathSegments"]
            .as_array()
            .map(|a| a.iter().map(|x| x.as_str().unwrap_or("")).collect())
            .unwrap_or_default();
        assert_eq!(u.path_segments(), expected_segs, "[{name}] path segments");

        // Query map (component-wise).
        let expected_query = case["query"].as_object().expect("query object");
        assert_eq!(
            u.query().len(),
            expected_query.len(),
            "[{name}] query length"
        );
        for (k, v_expected) in expected_query {
            let expected_v = v_expected.as_str().unwrap_or("");
            let actual = u.query().get(k.as_str()).map(String::as_str).unwrap_or("<missing>");
            assert_eq!(actual, expected_v, "[{name}] query[{k}]");
        }

        // Canonical structural round-trip: emit, re-parse, components must match.
        // (Byte-equal canonical with C# would require Dictionary insertion-order
        // emission; the Rust port uses BTreeMap for stable ordering, so we
        // compare structurally instead.)
        let emitted = u.to_string();
        let reparsed = AetherUri::parse(&emitted)
            .unwrap_or_else(|e| panic!("[{name}] re-parse of canonical {emitted:?} failed: {e:?}"));
        assert_eq!(reparsed, u, "[{name}] canonical re-parse must equal original");
    }
}

#[test]
fn cross_language_invalid_cases_fail_to_parse() {
    let v: serde_json::Value = serde_json::from_str(FIXTURE_JSON).unwrap();
    let cases = v["invalid"].as_array().expect("'invalid' array");
    assert!(!cases.is_empty(), "expected non-empty invalid corpus");
    for case in cases {
        let name = case["name"].as_str().unwrap_or("<unnamed>");
        let input = case["input"].as_str().expect("input string");
        let r = AetherUri::parse(input);
        assert!(
            r.is_err(),
            "case '{name}' (input {input:?}) must fail to parse, got {r:?}"
        );
    }
}

#[test]
fn cross_language_manifest_matches() {
    let v: serde_json::Value = serde_json::from_str(FIXTURE_JSON).unwrap();
    let manifest_def = &v["manifest"];
    let app_id = manifest_def["appId"].as_str().expect("appId");
    let handlers_def = manifest_def["handlers"].as_array().expect("handlers");

    let mut handlers = Vec::with_capacity(handlers_def.len());
    for h in handlers_def {
        handlers.push(HandlerDescriptor::new(
            h["handlerName"].as_str().unwrap_or(""),
            h["pathTemplate"].as_str().unwrap_or(""),
        ));
    }
    let manifest = HandlerManifest::new(app_id, handlers);

    let cases = manifest_def["matches"].as_array().expect("matches array");
    for case in cases {
        let input = case["input"].as_str().expect("input");
        let expected_matched = case["matched"].as_bool().unwrap_or(false);
        let u = AetherUri::parse(input).expect("manifest fixture must parse");
        let resolved = manifest.resolve(&u);

        if !expected_matched {
            assert!(resolved.is_none(), "case '{input}' should not match");
            continue;
        }
        let (idx, caps) = resolved.expect("case must match");
        let expected_idx = case["handlerIndex"].as_u64().expect("handlerIndex") as usize;
        assert_eq!(idx, expected_idx, "case '{input}' handler index");

        let expected_caps = case["captures"].as_object().expect("captures object");
        // Compare case-insensitively to honour the case-insensitive key store.
        assert_eq!(caps.len(), expected_caps.len(), "case '{input}' capture count");
        for (k, v_expected) in expected_caps {
            let lower = k.to_ascii_lowercase();
            let expected_v = v_expected.as_str().unwrap_or("");
            let actual = caps.get(&lower).map(String::as_str).unwrap_or("<missing>");
            assert_eq!(actual, expected_v, "case '{input}' capture[{k}]");
        }
    }
}
