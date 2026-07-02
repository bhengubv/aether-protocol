# SPDX-License-Identifier: MIT

"""Unit tests for the Aether pre-key exchange service (PacketType.PreKeyRequest 25 /
PreKeyResponse 26).

Mirrors tests/AetherNet.Core.Tests/PreKeyExchangeTests.cs. Directed request/response
transport of a :class:`PreKeyBundle` over the mesh — sends land in ``sender.unicasts``.
The byte-identity gate loads the SHARED fixtures/prekey/vectors.json and asserts this
SDK emits exactly the canonical bytes for both the request and response payloads.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from pathlib import Path
from uuid import UUID, uuid4

from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.security.signal_protocol import PreKeyBundle
from aethernet.prekey import PreKeyBundleReceived, PreKeyExchangeService
from aethernet.prekey.service import (
    _encode_pre_key_request_payload,
    _encode_pre_key_response_payload,
)

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return PreKeyExchangeService(sender), sender


def _sample_bundle(uhid: str = "aether:bob:02") -> PreKeyBundle:
    return PreKeyBundle(
        uhid=uhid,
        identity_key=bytes([0x11]) * 32,
        identity_key_x25519=bytes([0x22]) * 32,
        pre_key_id=4242,
        pre_key=bytes([0x33]) * 32,
        signed_pre_key_id=77,
        signed_pre_key=bytes([0x44]) * 32,
        signed_pre_key_signature=bytes([0x55]) * 64,
    )


def _fixtures_dir() -> Path:
    here = Path(__file__).resolve()
    # python/tests/test_prekey.py → python/tests/.. → aether-protocol/
    return here.parent.parent.parent / "fixtures"


def _load_prekey_vectors() -> list[dict]:
    with (_fixtures_dir() / "prekey" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)["vectors"]


class PreKeyByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (SHARED fixtures/prekey/vectors.json) ─────────

    def test_request_payload_serializes_to_canonical_bytes(self):
        p = _encode_pre_key_request_payload(
            UUID("11112222-3333-4444-5555-666677778888"), "aether:alice:01"
        )
        self.assertEqual(
            b'{"request_id":"11112222-3333-4444-5555-666677778888","requester_uhid":"aether:alice:01"}',
            p,
        )

    def test_response_payload_serializes_to_canonical_bytes(self):
        p = _encode_pre_key_response_payload(
            UUID("7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a"), _sample_bundle()
        )
        self.assertEqual(
            b'{"request_id":"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a","uhid":"aether:bob:02",'
            b'"identity_key":"ERERERERERERERERERERERERERERERERERERERERERE=",'
            b'"identity_key_x25519":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=",'
            b'"pre_key_id":4242,"pre_key":"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=",'
            b'"signed_pre_key_id":77,"signed_pre_key":"REREREREREREREREREREREREREREREREREREREREREQ=",'
            b'"signed_pre_key_signature":"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ=="}',
            p,
        )

    def test_payloads_match_shared_fixture_vectors(self):
        for vector in _load_prekey_vectors():
            with self.subTest(name=vector["name"]):
                if vector["kind"] == "request":
                    got = _encode_pre_key_request_payload(
                        UUID(vector["request_id"]), vector["requester_uhid"]
                    )
                else:
                    bundle = PreKeyBundle(
                        uhid=vector["uhid"],
                        identity_key=bytes([0x11]) * 32,
                        identity_key_x25519=bytes([0x22]) * 32,
                        pre_key_id=vector["pre_key_id"],
                        pre_key=bytes([0x33]) * 32,
                        signed_pre_key_id=vector["signed_pre_key_id"],
                        signed_pre_key=bytes([0x44]) * 32,
                        signed_pre_key_signature=bytes([0x55]) * 64,
                    )
                    got = _encode_pre_key_response_payload(
                        UUID(vector["request_id"]), bundle
                    )
                self.assertEqual(vector["expected_json"].encode("utf-8"), got)

    def test_response_payload_round_trips_through_bundle(self):
        original = _sample_bundle()
        svc, _ = _new_svc("aether:alice:01")
        pkt = MeshPacket(
            type=PacketType.PreKeyResponse,
            source_uhid="aether:bob:02",
            destination_uhid="aether:alice:01",
            payload=_encode_pre_key_response_payload(uuid4(), original),
        )
        self.assertTrue(_run(svc.handle(pkt)))
        back = svc.get_received_bundle("aether:bob:02")
        self.assertIsNotNone(back)
        self.assertEqual(original.uhid, back.uhid)
        self.assertEqual(original.pre_key_id, back.pre_key_id)
        self.assertEqual(original.signed_pre_key_id, back.signed_pre_key_id)
        self.assertEqual(original.identity_key, back.identity_key)
        self.assertEqual(original.signed_pre_key_signature, back.signed_pre_key_signature)


class PreKeyExchangeServiceTests(unittest.TestCase):
    # ─── Request ─────────────────────────────────────────

    def test_request_sends_directed_pre_key_request_and_returns_id(self):
        svc, sender = _new_svc("aether:alice:01")

        req_id = _run(svc.request_bundle("aether:bob:02"))

        self.assertNotEqual(UUID(int=0), req_id)
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.PreKeyRequest, sent.packet.type)
        self.assertEqual("aether:bob:02", sent.next_hop_uhid)
        body = json.loads(sent.packet.payload.decode("utf-8"))
        self.assertEqual(str(req_id), body["request_id"])
        self.assertEqual("aether:alice:01", body["requester_uhid"])

    def test_request_empty_peer_raises(self):
        svc, _ = _new_svc()
        with self.assertRaises(ValueError):
            _run(svc.request_bundle(""))

    # ─── Handle request ──────────────────────────────────

    def test_handle_request_with_local_bundle_sends_directed_response_to_requester(self):
        svc, sender = _new_svc("aether:bob:02")
        svc.set_local_bundle(_sample_bundle("aether:bob:02"))

        req_id = uuid4()
        req_pkt = MeshPacket(
            type=PacketType.PreKeyRequest,
            source_uhid="aether:alice:01",
            destination_uhid="aether:bob:02",
            payload=_encode_pre_key_request_payload(req_id, "aether:alice:01"),
        )

        self.assertTrue(_run(svc.handle(req_pkt)))
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.PreKeyResponse, sent.packet.type)
        self.assertEqual("aether:alice:01", sent.next_hop_uhid)
        body = json.loads(sent.packet.payload.decode("utf-8"))
        self.assertEqual(str(req_id), body["request_id"])
        self.assertEqual("aether:bob:02", body["uhid"])
        self.assertEqual(4242, body["pre_key_id"])
        import base64

        self.assertEqual(64, len(base64.b64decode(body["signed_pre_key_signature"])))

    def test_handle_request_no_local_bundle_returns_false_and_sends_nothing(self):
        svc, sender = _new_svc()
        req_pkt = MeshPacket(
            type=PacketType.PreKeyRequest,
            source_uhid="aether:alice:01",
            payload=_encode_pre_key_request_payload(uuid4(), "aether:alice:01"),
        )

        self.assertFalse(_run(svc.handle(req_pkt)))
        self.assertEqual(0, len(sender.unicasts))

    # ─── Handle response ─────────────────────────────────

    def test_handle_response_caches_bundle_and_raises_event(self):
        svc, _ = _new_svc("aether:alice:01")
        got: dict = {}
        svc.on_bundle_received = lambda e: got.setdefault("e", e)

        req_id = uuid4()
        resp_pkt = MeshPacket(
            type=PacketType.PreKeyResponse,
            source_uhid="aether:bob:02",
            destination_uhid="aether:alice:01",
            payload=_encode_pre_key_response_payload(req_id, _sample_bundle("aether:bob:02")),
        )

        self.assertTrue(_run(svc.handle(resp_pkt)))
        self.assertIn("e", got)
        evt: PreKeyBundleReceived = got["e"]
        self.assertEqual(req_id, evt.request_id)
        self.assertEqual("aether:bob:02", evt.from_uhid)
        self.assertEqual("aether:bob:02", evt.bundle.uhid)

        cached = svc.get_received_bundle("aether:bob:02")
        self.assertIsNotNone(cached)
        self.assertEqual(4242, cached.pre_key_id)

    # ─── Wrong type ──────────────────────────────────────

    def test_handle_wrong_packet_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.Data,
            source_uhid="aether:x:01",
            payload=b"",
        )
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
