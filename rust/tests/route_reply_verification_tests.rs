// SPDX-License-Identifier: MIT
//! Security acceptance tests for fail-closed RREP verification (Gap 3).
//! Mirrors the C# canonical suite `RouteReplyVerificationTests`.
//!
//! Proves the properties of the hardened routing layer:
//!   (a) a RoutingService with NO verifier supplied REJECTS an RREP — no forward route installed
//!       (the fail-closed `RejectAllRouteReplyVerifier` default applies);
//!   (b) an `Ed25519RouteReplyVerifier` whose resolver returns the correct public key ACCEPTS a
//!       validly-signed RREP — forward route installed;
//!   (c) a forged RREP (signed by a DIFFERENT key), an unsigned RREP, and an unknown-signer RREP
//!       are ALL rejected.
//!
//! Signed RREPs are built with a real Ed25519 keypair via the production signing path
//! (`PacketSigningService::sign_packet`), so this exercises the actual signature verification,
//! not a stub. Assertions are on the observable side effect: presence/absence of the forward
//! route in the store.

#[path = "common.rs"]
mod common;

use std::collections::HashMap;
use std::sync::Arc;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{
        AcceptAllRouteReplyVerifier, Ed25519RouteReplyVerifier, InMemoryRouteStore,
        RouteReplyKeyResolver, RouteStore, RoutingService,
    },
    security::{Ed25519SigningService, PacketSigningService},
};
use common::FakeMeshSender;

const LOCAL: &str = "local-uhid";
const SOURCE: &str = "carol";

/// Builds an unsigned RREP claiming to originate from `source`, destined for `LOCAL` so that a
/// successful verification installs a forward route to `source`.
fn new_rrep(source: &str) -> MeshPacket {
    let mut p = MeshPacket::new(PacketType::RouteReply, source.to_string());
    p.destination_uhid = LOCAL.to_string();
    p.ttl = DEFAULT_TTL;
    p
}

/// Signs `rrep` with `private_key` via the production packet-signing path (fills nonce,
/// timestamp, and the Ed25519 signature over the canonical `signable_data()` bytes).
fn sign_rrep(rrep: &mut MeshPacket, private_key: &[u8]) {
    PacketSigningService::new()
        .sign_packet(rrep, private_key)
        .expect("sign rrep");
}

/// Minimal in-test UHID→public-key map for the routing verifier.
struct StubKeyResolver {
    keys: HashMap<String, Vec<u8>>,
}

impl StubKeyResolver {
    fn empty() -> Self {
        Self { keys: HashMap::new() }
    }
    fn with(uhid: &str, public_key: Vec<u8>) -> Self {
        let mut keys = HashMap::new();
        keys.insert(uhid.to_string(), public_key);
        Self { keys }
    }
}

impl RouteReplyKeyResolver for StubKeyResolver {
    fn resolve_public_key(&self, source_uhid: &str) -> Option<Vec<u8>> {
        self.keys.get(source_uhid).cloned()
    }
}

fn ed25519_svc(
    resolver: StubKeyResolver,
    sender: Arc<FakeMeshSender>,
    store: Arc<InMemoryRouteStore>,
) -> RoutingService {
    RoutingService::with_dependencies(
        sender,
        store,
        Arc::new(Ed25519RouteReplyVerifier::new(resolver)),
        Arc::new(NoopIncentiveProvider),
    )
}

// ─── (a) No verifier ⇒ fail-closed reject ────────────────────────────────

#[tokio::test]
async fn no_verifier_rejects_rrep_no_route_installed() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    // `RoutingService::new` supplies no verifier — the fail-closed default (RejectAll) applies.
    let svc = RoutingService::new(sender);

    svc.handle_route_reply(&mut new_rrep(SOURCE)).await;

    assert!(store.get(SOURCE).await.is_none(), "route rejected — not installed");
    assert!(svc.get_cached_route(SOURCE).await.is_none());
}

// ─── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ───────

#[tokio::test]
async fn ed25519_verifier_validly_signed_rrep_installs_forward_route() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());

    // The source node's real identity; its public key is registered with the resolver.
    let (source_private, source_public) = Ed25519SigningService::generate_keypair();
    let resolver = StubKeyResolver::with(SOURCE, source_public);
    let svc = ed25519_svc(resolver, sender, store.clone());

    let mut signed = new_rrep(SOURCE);
    sign_rrep(&mut signed, &source_private);
    svc.handle_route_reply(&mut signed).await;

    let route = store.get(SOURCE).await.expect("forward route installed");
    assert_eq!(route.next_hop_uhid, SOURCE);
}

// ─── (c) Forged (wrong-key) signature ⇒ reject ───────────────────────────

#[tokio::test]
async fn ed25519_verifier_forged_rrep_signed_by_different_key_is_rejected() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());

    // Resolver knows the LEGITIMATE source key...
    let (_legit_private, legit_public) = Ed25519SigningService::generate_keypair();
    let resolver = StubKeyResolver::with(SOURCE, legit_public);
    let svc = ed25519_svc(resolver, sender, store.clone());

    // ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
    let (attacker_private, _attacker_public) = Ed25519SigningService::generate_keypair();
    let mut forged = new_rrep(SOURCE);
    sign_rrep(&mut forged, &attacker_private);

    svc.handle_route_reply(&mut forged).await;

    assert!(
        store.get(SOURCE).await.is_none(),
        "forged signature rejected — no route"
    );
}

// ─── (c) Unsigned RREP ⇒ reject ──────────────────────────────────────────

#[tokio::test]
async fn ed25519_verifier_unsigned_rrep_is_rejected() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());

    let (_source_private, source_public) = Ed25519SigningService::generate_keypair();
    let resolver = StubKeyResolver::with(SOURCE, source_public);
    let svc = ed25519_svc(resolver, sender, store.clone());

    // RREP with an empty signature (the MeshPacket default) — must be rejected.
    svc.handle_route_reply(&mut new_rrep(SOURCE)).await;

    assert!(store.get(SOURCE).await.is_none());
}

// ─── (c') Unknown signer (resolver returns None) ⇒ reject ────────────────

#[tokio::test]
async fn ed25519_verifier_unknown_source_is_rejected() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());

    // Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
    let resolver = StubKeyResolver::empty();
    let svc = ed25519_svc(resolver, sender, store.clone());

    let (source_private, _source_public) = Ed25519SigningService::generate_keypair();
    let mut signed = new_rrep(SOURCE);
    sign_rrep(&mut signed, &source_private);

    svc.handle_route_reply(&mut signed).await;

    assert!(store.get(SOURCE).await.is_none());
}

// ─── Sanity: explicit AcceptAll still accepts (insecure opt-in intact) ────

#[tokio::test]
async fn accept_all_verifier_still_installs_route() {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let svc = RoutingService::with_dependencies(
        sender,
        store.clone(),
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    );

    // Unsigned RREP is accepted only because AcceptAll is explicitly, insecurely opted in.
    svc.handle_route_reply(&mut new_rrep(SOURCE)).await;

    assert!(store.get(SOURCE).await.is_some());
}
