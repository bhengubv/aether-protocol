# SPDX-License-Identifier: MIT

"""Security acceptance tests for fail-closed RREP verification (Gap 3).

Mirror of tests/AetherNet.Core.Tests/RouteReplyVerificationTests.cs.

Proves the properties of the hardened routing layer:
  (a) a RoutingService with NO verifier supplied REJECTS an RREP — no forward
      route installed (fail-closed default);
  (b) an Ed25519RouteReplyVerifier whose resolver returns the correct public key
      ACCEPTS a validly-signed RREP — forward route installed;
  (c) a forged RREP (signed by a DIFFERENT key), an unsigned RREP, and an
      unknown-signer RREP are ALL rejected.

Signed RREPs are built with a real Ed25519 keypair via the production signing
path (PacketSigningService.sign_packet), so this exercises the actual signature
verification, not a stub. Assertions are on the observable side effect:
presence / absence of the forward route in the store.

Run with: python -m pytest tests/test_route_reply_verification.py -v
"""

from __future__ import annotations

from typing import Dict, Optional

import pytest

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing import (
    Ed25519RouteReplyVerifier,
    InMemoryRouteStore,
    RouteReplyKeyResolver,
    RoutingService,
)
from aethernet.security.ed25519_service import Ed25519SigningService
from aethernet.security.packet_signing import PacketSigningService

from tests.fakes import FakeMeshSender

LOCAL = "local-uhid"
SOURCE = "carol"


def _new_rrep(
    source: str = SOURCE, destination: str = LOCAL, ttl: int = constants.DEFAULT_TTL
) -> MeshPacket:
    return MeshPacket(
        type=PacketType.RouteReply,
        source_uhid=source,
        destination_uhid=destination,
        ttl=ttl,
    )


def _sign_rrep(rrep: MeshPacket, private_key: bytes) -> MeshPacket:
    """Sign an RREP with the given identity via the production signing path,
    filling packet.signature over the canonical signable bytes."""
    PacketSigningService().sign_packet(rrep, private_key)
    return rrep


class StubKeyResolver(RouteReplyKeyResolver):
    """Minimal in-test UHID->public-key map for the routing verifier."""

    def __init__(self, uhid: Optional[str] = None, public_key: Optional[bytes] = None) -> None:
        self._keys: Dict[str, bytes] = {}
        if uhid is not None and public_key is not None:
            self._keys[uhid] = public_key

    def resolve_public_key(self, source_uhid: str) -> Optional[bytes]:
        return self._keys.get(source_uhid)


# ─── (a) No verifier ⇒ fail-closed reject ────────────────────────────────


@pytest.mark.asyncio
async def test_no_verifier_rejects_rrep_no_route_installed():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()
    # No verifier argument at all — the fail-closed default (RejectAll) must apply.
    svc = RoutingService(sender, store)

    await svc.handle_route_reply(_new_rrep())

    assert await store.get(SOURCE) is None  # route rejected — not installed
    assert svc.get_cached_route(SOURCE) is None


# ─── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ───────


@pytest.mark.asyncio
async def test_ed25519_verifier_validly_signed_rrep_installs_forward_route():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()

    # The source node's real identity. Its public key is registered with the resolver.
    source_private, source_public = Ed25519SigningService.generate_keypair()
    resolver = StubKeyResolver(SOURCE, source_public)

    verifier = Ed25519RouteReplyVerifier(resolver)
    svc = RoutingService(sender, store, verifier)

    signed_rrep = _sign_rrep(_new_rrep(), source_private)
    await svc.handle_route_reply(signed_rrep)

    route = await store.get(SOURCE)
    assert route is not None
    assert route.next_hop_uhid == SOURCE


# ─── (c) Forged (wrong-key) signature ⇒ reject ───────────────────────────


@pytest.mark.asyncio
async def test_ed25519_verifier_forged_rrep_signed_by_different_key_is_rejected():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()

    # Resolver knows the LEGITIMATE source key...
    _legit_private, legit_public = Ed25519SigningService.generate_keypair()
    resolver = StubKeyResolver(SOURCE, legit_public)
    verifier = Ed25519RouteReplyVerifier(resolver)
    svc = RoutingService(sender, store, verifier)

    # ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
    attacker_private, _attacker_public = Ed25519SigningService.generate_keypair()
    forged_rrep = _sign_rrep(_new_rrep(), attacker_private)

    await svc.handle_route_reply(forged_rrep)

    assert await store.get(SOURCE) is None  # forged signature rejected — no route


# ─── (c) Unsigned RREP ⇒ reject ──────────────────────────────────────────


@pytest.mark.asyncio
async def test_ed25519_verifier_unsigned_rrep_is_rejected():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()

    _source_private, source_public = Ed25519SigningService.generate_keypair()
    resolver = StubKeyResolver(SOURCE, source_public)
    verifier = Ed25519RouteReplyVerifier(resolver)
    svc = RoutingService(sender, store, verifier)

    # RREP with an empty signature (the MeshPacket default) — must be rejected.
    await svc.handle_route_reply(_new_rrep())

    assert await store.get(SOURCE) is None


# ─── (c') Unknown signer (resolver returns None) ⇒ reject ────────────────


@pytest.mark.asyncio
async def test_ed25519_verifier_unknown_source_is_rejected():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()

    # Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
    resolver = StubKeyResolver()  # empty
    verifier = Ed25519RouteReplyVerifier(resolver)
    svc = RoutingService(sender, store, verifier)

    source_private, _source_public = Ed25519SigningService.generate_keypair()
    signed_rrep = _sign_rrep(_new_rrep(), source_private)

    await svc.handle_route_reply(signed_rrep)

    assert await store.get(SOURCE) is None
