# SPDX-License-Identifier: MIT

"""RREP verifier interface and implementations (fail-closed by default).

Threat — RREP hijack. AODV-style reactive routing installs a forward route
straight from an RREP's ``source_uhid``. Any intermediate forwarder that sees a
route-request flood can fabricate an RREP claiming to be the destination, poison
every hop's route table, and pull the victim's traffic onto itself (blackhole /
man-in-the-middle). The only defence is to require a valid source signature on
the RREP before trusting it.

Fail-closed by default. The base :class:`RouteReplyVerifier` now REJECTS every
RREP: an absent or partial implementation must never silently trust unverified
route replies. A host that ships a real implementation (typically the Ed25519
verifier below) opts in to actually validating signatures; until it does, no
RREP is accepted and no forward route is installed.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional

from aethernet.protocol.mesh_packet import MeshPacket
from aethernet.security.ed25519_service import Ed25519SigningService
from aethernet.security.packet_signing import PacketSigningService


class RouteReplyVerifier(ABC):
    """Verifies that a received RREP was actually signed by the node it claims
    to come from.

    Fail-closed default: the base ``verify`` REJECTS every RREP (returns
    ``False``). This is deliberate — an unconfigured or half-built verifier must
    not be exploitable to hijack routes. Supply a real implementation (e.g.
    :class:`Ed25519RouteReplyVerifier`) to permit legitimate, signature-verified
    RREPs, or the explicit :class:`AcceptAllRouteReplyVerifier` to disable
    verification for routing-mechanics tests / trusted-fabric demos.
    """

    async def verify(self, route_reply: MeshPacket) -> bool:
        return False


class RejectAllRouteReplyVerifier(RouteReplyVerifier):
    """Fail-closed verifier: every RREP is REJECTED.

    This is the safe default the :class:`RoutingService` falls back to when no
    verifier is supplied — an unverified route reply is never trusted, so the
    RREP-hijack attack surface is closed until a host wires a real signature
    verifier. Route discovery for peers that would otherwise reply legitimately
    will simply not complete under this verifier; that is intentional
    (correctness over availability for an unconfigured node).
    """

    async def verify(self, route_reply: MeshPacket) -> bool:
        return False


class AcceptAllRouteReplyVerifier(RouteReplyVerifier):
    """INSECURE. Accepts every RREP without any signature check.

    Explicit opt-in escape hatch for unit tests that exercise routing
    *mechanics* (forwarding, caching, TTL) and for trust-the-fabric demos on a
    closed, fully-trusted network. It provides NO protection against RREP hijack
    and MUST NOT be used in production or on any open mesh — a single malicious
    forwarder can blackhole traffic. It is deliberately NOT the default: callers
    have to reach for it by name so the choice to disable verification is visible
    in the code.
    """

    async def verify(self, route_reply: MeshPacket) -> bool:
        return True


class RouteReplyKeyResolver(ABC):
    """Resolves the Ed25519 public key of a node given its source UHID, so an
    RREP's signature can be checked against the identity it claims.

    Returns ``None`` when the UHID is unknown — the verifier treats an
    unresolvable signer as untrusted and rejects the RREP (fail-closed: an
    unknown key can never produce a valid signature we would accept).

    No shared peer-key directory exists in the protocol today — callers that
    verify packets (reputation gossip, PoV token exchange) pass the sender public
    key in explicitly. This minimal resolver abstracts "UHID -> public key" for
    the routing layer so a host can plug in whatever key source it already
    maintains (handshake-established keys, a published identity directory, a
    prekey / identity store, etc.) without the routing layer taking a dependency
    on any one of them.
    """

    @abstractmethod
    def resolve_public_key(self, source_uhid: str) -> Optional[bytes]:
        """Return the Ed25519 public key registered for ``source_uhid``, or
        ``None`` if the node is unknown. A ``None`` result causes the RREP to be
        rejected."""


class Ed25519RouteReplyVerifier(RouteReplyVerifier):
    """Production verifier: accepts an RREP only if it carries a valid Ed25519
    signature produced by the node it claims to originate from.

    This closes the RREP-hijack hole. An AODV forward route is installed straight
    from an RREP's ``source_uhid``; without a signature check, any intermediate
    forwarder can forge an RREP for the destination and blackhole /
    man-in-the-middle the victim's traffic. Here we resolve the claimed source's
    public key and verify the signature over the exact same canonical bytes the
    source signed (:meth:`PacketSigningService._construct_signable_data`), so a
    forged or unsigned RREP fails and no route is installed.

    Fail-closed at every branch: a missing signature, an unresolvable / unknown
    source key, or a signature that does not verify all return ``False``. Only a
    signature that validates against a known key is accepted.

    Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here —
    that is already enforced by :class:`PacketSigningService` in the packet-ingest
    pipeline. This verifier is purely the source-identity gate the routing layer
    needs before trusting a route reply.
    """

    def __init__(self, key_resolver: RouteReplyKeyResolver) -> None:
        """Create the verifier.

        Args:
            key_resolver: Resolves an RREP source UHID to its Ed25519 public key.
                A ``None`` result (unknown signer) causes the RREP to be rejected.
        """
        if key_resolver is None:
            raise ValueError("key_resolver cannot be None")
        self._key_resolver = key_resolver
        # A stateless helper purely to build the canonical signable bytes — the
        # SAME layout the source signed and every other language SDK shares. No
        # new wire layout is introduced.
        self._signing = PacketSigningService()

    async def verify(self, route_reply: MeshPacket) -> bool:
        if route_reply is None:
            raise ValueError("route_reply cannot be None")

        # No signature -> cannot be trusted. (MeshPacket.signature defaults to
        # an empty bytes object.)
        if not route_reply.signature:
            return False

        # Resolve the claimed source's public key. Unknown signer -> reject
        # (fail-closed): an unresolvable key can never produce a signature we
        # would accept.
        public_key = self._key_resolver.resolve_public_key(route_reply.source_uhid)
        if not public_key:
            return False

        # Verify the Ed25519 signature over the canonical signable bytes — the
        # SAME layout the source signed. Reusing _construct_signable_data means
        # zero divergence from the packet-signing path and no fixture change.
        signable_data = self._signing._construct_signable_data(route_reply)
        return Ed25519SigningService.verify(
            public_key, signable_data, route_reply.signature
        )
