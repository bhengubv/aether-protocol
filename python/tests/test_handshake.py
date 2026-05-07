# SPDX-License-Identifier: MIT

"""Tests for the Hello/HelloAck capability handshake.

Mirrors the C# `HandshakeServiceTests` suite. Verifies:
    * Hello / HelloAck round-trip
    * Version selection (higher / lower / no-overlap)
    * Capability intersection
    * Duplicate-Hello suppression
    * Backward-compat fallback for peers that never reply
"""

from __future__ import annotations

import json
from typing import List

import pytest

from aether.handshake import (
    HandshakeService,
    HelloPayload,
    IncompatiblePeerEvent,
    PeerCapabilities,
)
from aether.handshake.service import DEFAULT_CAPABILITIES, DEFAULT_IMPLEMENTATION
from aether.protocol.mesh_packet import MeshPacket, PacketType
from tests.fakes import FakeMeshSender


# ─── helpers ───────────────────────────────────────────────────────────


def _make_pair():
    """Build a pair of HandshakeService instances + their FakeMeshSender peers,
    with capability defaults that overlap on at least signal-x3dh + dtn-custody.
    """
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a)
    b = HandshakeService(sender=sender_b)
    return sender_a, sender_b, a, b


# ─── HelloPayload (de)serialisation ────────────────────────────────────


def test_hello_payload_json_shape_uses_snake_case_keys():
    """The JSON wire format MUST match C#'s HelloPayload exactly:
    snake_case keys, four fields.
    """
    payload = HelloPayload(
        min_version=1,
        max_version=2,
        capabilities=["signal-x3dh", "double-ratchet"],
        implementation="aether-python/1.0.0",
    )
    raw = payload.to_json_bytes()
    obj = json.loads(raw.decode("utf-8"))

    assert set(obj.keys()) == {
        "min_version", "max_version", "capabilities", "implementation"
    }
    assert obj["min_version"] == 1
    assert obj["max_version"] == 2
    assert obj["capabilities"] == ["signal-x3dh", "double-ratchet"]
    assert obj["implementation"] == "aether-python/1.0.0"


def test_hello_payload_round_trips():
    payload = HelloPayload(
        min_version=1,
        max_version=2,
        capabilities=["a", "b", "c"],
        implementation="test",
    )
    decoded = HelloPayload.from_json_bytes(payload.to_json_bytes())
    assert decoded.min_version == 1
    assert decoded.max_version == 2
    assert decoded.capabilities == ["a", "b", "c"]
    assert decoded.implementation == "test"


def test_hello_payload_tolerates_missing_optional_fields():
    obj = {"min_version": 1, "max_version": 2}
    decoded = HelloPayload.from_json_bytes(json.dumps(obj).encode("utf-8"))
    assert decoded.capabilities == []
    assert decoded.implementation == ""


def test_hello_payload_tolerates_extra_unknown_fields():
    obj = {
        "min_version": 1, "max_version": 2,
        "capabilities": [], "implementation": "x",
        "future_field": "ignored",
    }
    decoded = HelloPayload.from_json_bytes(json.dumps(obj).encode("utf-8"))
    assert decoded.min_version == 1


def test_hello_payload_rejects_out_of_byte_range():
    obj = {"min_version": 256, "max_version": 2}
    with pytest.raises(ValueError):
        HelloPayload.from_json_bytes(json.dumps(obj).encode("utf-8"))


# ─── round-trip flow ───────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_initiate_sends_hello_packet():
    sender_a, sender_b, a, b = _make_pair()

    await a.initiate("bob")

    assert len(sender_a.unicasts) == 1
    rec = sender_a.unicasts[0]
    assert rec.next_hop_uhid == "bob"
    assert rec.packet.type == PacketType.Hello
    assert rec.packet.source_uhid == "alice"
    assert rec.packet.destination_uhid == "bob"
    assert rec.packet.ttl == 1
    assert rec.packet.priority == 0


@pytest.mark.asyncio
async def test_handle_hello_replies_with_hello_ack_and_locks_in_caps():
    sender_a, sender_b, a, b = _make_pair()

    # Alice initiates -> emits Hello.
    await a.initiate("bob")
    hello = sender_a.unicasts[0].packet

    negotiated_events: List[PeerCapabilities] = []

    async def on_negotiated(c: PeerCapabilities) -> None:
        negotiated_events.append(c)

    b.add_peer_negotiated_handler(on_negotiated)

    # Bob receives Hello.
    await b.handle_hello(hello)

    # Bob has locked in alice's caps.
    bob_view = await b.get_peer_capabilities("alice")
    assert bob_view is not None
    assert bob_view.peer_uhid == "alice"
    assert bob_view.negotiated_version == 2
    assert bob_view.implementation_version == DEFAULT_IMPLEMENTATION
    assert bob_view.capabilities == frozenset(DEFAULT_CAPABILITIES)

    # Bob fired event.
    assert len(negotiated_events) == 1
    assert negotiated_events[0].peer_uhid == "alice"

    # Bob sent a HelloAck.
    assert len(sender_b.unicasts) == 1
    ack = sender_b.unicasts[0].packet
    assert ack.type == PacketType.HelloAck
    assert ack.source_uhid == "bob"
    assert ack.destination_uhid == "alice"


@pytest.mark.asyncio
async def test_full_round_trip_locks_in_both_sides():
    sender_a, sender_b, a, b = _make_pair()

    await a.initiate("bob")
    hello = sender_a.unicasts[0].packet
    await b.handle_hello(hello)
    ack = sender_b.unicasts[0].packet
    await a.handle_hello_ack(ack)

    a_view = await a.get_peer_capabilities("bob")
    b_view = await b.get_peer_capabilities("alice")
    assert a_view is not None and b_view is not None
    assert a_view.negotiated_version == 2
    assert b_view.negotiated_version == 2
    # Capability intersection is symmetric.
    assert a_view.capabilities == b_view.capabilities


# ─── version negotiation ────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_version_chosen_is_lowest_of_both_max_versions():
    """We support 1..3, peer supports 1..2 -> negotiated = 2."""
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a, our_min_version=1, our_max_version=3)
    b = HandshakeService(sender=sender_b, our_min_version=1, our_max_version=2)

    await a.initiate("bob")
    await b.handle_hello(sender_a.unicasts[0].packet)
    await a.handle_hello_ack(sender_b.unicasts[0].packet)

    a_view = await a.get_peer_capabilities("bob")
    b_view = await b.get_peer_capabilities("alice")
    assert a_view.negotiated_version == 2
    assert b_view.negotiated_version == 2


@pytest.mark.asyncio
async def test_version_overlap_with_higher_min():
    """We support 2..3, peer supports 1..2 -> overlap is exactly {2}."""
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a, our_min_version=2, our_max_version=3)
    b = HandshakeService(sender=sender_b, our_min_version=1, our_max_version=2)

    await a.initiate("bob")
    await b.handle_hello(sender_a.unicasts[0].packet)

    b_view = await b.get_peer_capabilities("alice")
    assert b_view.negotiated_version == 2


@pytest.mark.asyncio
async def test_no_overlap_fires_incompatible_peer():
    """We support 2..3, peer supports 1..1 -> no overlap, no lock-in."""
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a, our_min_version=2, our_max_version=3)
    b = HandshakeService(sender=sender_b, our_min_version=1, our_max_version=1)

    incompat: List[IncompatiblePeerEvent] = []

    async def on_incompat(evt: IncompatiblePeerEvent) -> None:
        incompat.append(evt)

    a.add_incompatible_peer_handler(on_incompat)

    # Bob initiates with v1..1; alice receives -> incompatible.
    await b.initiate("alice")
    bob_hello = sender_b.unicasts[0].packet
    await a.handle_hello(bob_hello)

    # Alice did NOT lock in.
    assert (await a.get_peer_capabilities("bob")) is None
    # Alice fired IncompatiblePeer.
    assert len(incompat) == 1
    assert incompat[0].peer_uhid == "bob"
    assert incompat[0].their_min_version == 1
    assert incompat[0].their_max_version == 1


@pytest.mark.asyncio
async def test_inverted_range_is_rejected():
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a)

    # Hand-crafted Hello with min > max.
    bad = HelloPayload(min_version=5, max_version=2, capabilities=[], implementation="bad")
    pkt = MeshPacket(
        type=PacketType.Hello,
        source_uhid="bob",
        destination_uhid="alice",
        payload=bad.to_json_bytes(),
    )

    incompat: List[IncompatiblePeerEvent] = []

    async def on_incompat(evt: IncompatiblePeerEvent) -> None:
        incompat.append(evt)

    a.add_incompatible_peer_handler(on_incompat)

    await a.handle_hello(pkt)

    assert (await a.get_peer_capabilities("bob")) is None
    assert len(incompat) == 1
    assert incompat[0].reason == "inverted version range"


# ─── capability intersection ────────────────────────────────────────────


@pytest.mark.asyncio
async def test_capability_intersection_is_only_shared_tags():
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(
        sender=sender_a, our_capabilities={"signal-x3dh", "voice", "exclusive-a"}
    )
    b = HandshakeService(
        sender=sender_b, our_capabilities={"signal-x3dh", "exclusive-b"}
    )

    await a.initiate("bob")
    await b.handle_hello(sender_a.unicasts[0].packet)

    b_view = await b.get_peer_capabilities("alice")
    # Bob locks in only the intersection (signal-x3dh).
    assert b_view.capabilities == frozenset({"signal-x3dh"})


@pytest.mark.asyncio
async def test_empty_capability_intersection_still_negotiates():
    sender_a = FakeMeshSender("alice")
    sender_b = FakeMeshSender("bob")
    a = HandshakeService(sender=sender_a, our_capabilities={"only-a"})
    b = HandshakeService(sender=sender_b, our_capabilities={"only-b"})

    await a.initiate("bob")
    await b.handle_hello(sender_a.unicasts[0].packet)

    b_view = await b.get_peer_capabilities("alice")
    assert b_view is not None  # version still negotiated
    assert b_view.capabilities == frozenset()  # intersection empty


# ─── duplicate-Hello suppression ───────────────────────────────────────


@pytest.mark.asyncio
async def test_initiate_twice_to_same_peer_sends_only_one_hello():
    sender_a, sender_b, a, b = _make_pair()

    await a.initiate("bob")
    await a.initiate("bob")

    assert len(sender_a.unicasts) == 1


@pytest.mark.asyncio
async def test_initiate_to_self_is_noop():
    sender_a, sender_b, a, b = _make_pair()
    await a.initiate("alice")
    assert len(sender_a.unicasts) == 0


@pytest.mark.asyncio
async def test_renegotiate_clears_state_and_allows_new_hello():
    sender_a, sender_b, a, b = _make_pair()

    await a.initiate("bob")
    assert len(sender_a.unicasts) == 1

    # Pretend we negotiated and then need to renegotiate.
    await a.handle_hello_ack(_build_ack_to_alice("bob"))
    await a.renegotiate("bob")

    # New Hello may now go out.
    sender_a.clear()
    await a.initiate("bob")
    assert len(sender_a.unicasts) == 1


def _build_ack_to_alice(from_peer: str) -> MeshPacket:
    """Helper: synthesise a HelloAck from `from_peer` -> alice with the
    default version range / capabilities."""
    payload = HelloPayload(
        min_version=1,
        max_version=2,
        capabilities=sorted(DEFAULT_CAPABILITIES),
        implementation=DEFAULT_IMPLEMENTATION,
    )
    return MeshPacket(
        type=PacketType.HelloAck,
        source_uhid=from_peer,
        destination_uhid="alice",
        payload=payload.to_json_bytes(),
    )


# ─── backward-compat fallback ──────────────────────────────────────────


@pytest.mark.asyncio
async def test_assume_legacy_v1_for_silent_peer_locks_in_v1_no_caps():
    sender_a, sender_b, a, b = _make_pair()

    events: List[PeerCapabilities] = []

    async def on_negotiated(c: PeerCapabilities) -> None:
        events.append(c)

    a.add_peer_negotiated_handler(on_negotiated)

    await a.assume_legacy_v1("ghost-peer")

    view = await a.get_peer_capabilities("ghost-peer")
    assert view is not None
    assert view.negotiated_version == 1
    assert view.capabilities == frozenset()
    assert view.implementation_version == ""
    assert len(events) == 1


@pytest.mark.asyncio
async def test_assume_legacy_v1_does_not_overwrite_existing_negotiation():
    sender_a, sender_b, a, b = _make_pair()

    # Real negotiation first.
    await a.initiate("bob")
    await b.handle_hello(sender_a.unicasts[0].packet)
    await a.handle_hello_ack(sender_b.unicasts[0].packet)

    pre = await a.get_peer_capabilities("bob")
    assert pre is not None
    assert pre.negotiated_version == 2

    # Now the timer fires for some reason.
    await a.assume_legacy_v1("bob")
    post = await a.get_peer_capabilities("bob")
    assert post is pre  # unchanged


# ─── malformed inputs ──────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_hello_with_empty_payload_is_ignored():
    sender_a = FakeMeshSender("alice")
    a = HandshakeService(sender=sender_a)

    pkt = MeshPacket(
        type=PacketType.Hello,
        source_uhid="bob",
        destination_uhid="alice",
        payload=b"",
    )
    await a.handle_hello(pkt)
    assert (await a.get_peer_capabilities("bob")) is None
    assert len(sender_a.unicasts) == 0


@pytest.mark.asyncio
async def test_hello_with_garbled_payload_is_ignored():
    sender_a = FakeMeshSender("alice")
    a = HandshakeService(sender=sender_a)

    pkt = MeshPacket(
        type=PacketType.Hello,
        source_uhid="bob",
        destination_uhid="alice",
        payload=b"\x00\xffnot-json",
    )
    await a.handle_hello(pkt)
    assert (await a.get_peer_capabilities("bob")) is None


@pytest.mark.asyncio
async def test_hello_from_self_is_ignored():
    sender_a = FakeMeshSender("alice")
    a = HandshakeService(sender=sender_a)

    payload = HelloPayload(
        min_version=1, max_version=2, capabilities=[], implementation="self"
    )
    pkt = MeshPacket(
        type=PacketType.Hello,
        source_uhid="alice",  # same as our sender.local_uhid
        destination_uhid="alice",
        payload=payload.to_json_bytes(),
    )
    await a.handle_hello(pkt)
    assert (await a.get_peer_capabilities("alice")) is None


@pytest.mark.asyncio
async def test_handle_hello_with_wrong_packet_type_raises():
    sender_a = FakeMeshSender("alice")
    a = HandshakeService(sender=sender_a)

    pkt = MeshPacket(type=PacketType.Data, source_uhid="bob")
    with pytest.raises(ValueError):
        await a.handle_hello(pkt)


# ─── packet type enum surface ──────────────────────────────────────────


def test_hello_and_hello_ack_are_50_and_51():
    """Wire-level constants — must not drift from the C# enum values
    or the cross-language fixtures break.
    """
    assert int(PacketType.Hello) == 50
    assert int(PacketType.HelloAck) == 51
