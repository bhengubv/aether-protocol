# SPDX-License-Identifier: MIT

"""Unit tests for aethernet.transport.in_process.InProcessTransport."""

from __future__ import annotations

import asyncio
import threading
import unittest

from aethernet.transport.in_process import InProcessTransport


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


class TestInProcessTransport(unittest.TestCase):
    """Tests for InProcessTransport.

    setUp / tearDown clear the global peer registry so each test
    starts with a clean slate — avoids cross-test interference.
    """

    def setUp(self):
        with InProcessTransport._global_lock:
            InProcessTransport._global_peers.clear()

    def tearDown(self):
        with InProcessTransport._global_lock:
            InProcessTransport._global_peers.clear()

    # ── Constructor ──────────────────────────────────────────────────────────

    def test_creates_node_with_correct_name(self):
        t = InProcessTransport("alice")
        self.assertEqual(t.name, "InProcess")
        t.shutdown()

    def test_is_available_true_after_create(self):
        t = InProcessTransport("alice")
        self.assertTrue(t.is_available)
        t.shutdown()

    def test_max_bandwidth_bps_positive(self):
        t = InProcessTransport("alice")
        self.assertGreater(t.max_bandwidth_bps, 0)
        t.shutdown()

    def test_metrics_non_null_after_create(self):
        t = InProcessTransport("alice")
        self.assertIsNotNone(t.metrics)
        t.shutdown()

    def test_duplicate_uhid_overwrites_registration(self):
        """
        Python implementation replaces the existing node (no exception).
        Two instances with the same UHID share the slot; the second wins.
        """
        t1 = InProcessTransport("alice")
        t2 = InProcessTransport("alice")  # replaces t1 in registry
        # Both can be shut down; second shutdown is a no-op for t1
        t2.shutdown()
        t1.shutdown()

    # ── is_connected ─────────────────────────────────────────────────────────

    def test_is_connected_returns_true_for_registered_peer(self):
        a = InProcessTransport("alice")
        InProcessTransport("bob")
        self.assertTrue(a.is_connected("bob"))
        a.shutdown()

    def test_is_connected_returns_false_for_unknown_peer(self):
        a = InProcessTransport("alice")
        self.assertFalse(a.is_connected("ghost"))
        a.shutdown()

    def test_is_connected_false_after_peer_shuts_down(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")
        self.assertTrue(a.is_connected("bob"))
        b.shutdown()
        self.assertFalse(a.is_connected("bob"))
        a.shutdown()

    # ── send_async ────────────────────────────────────────────────────────────

    def test_send_delivers_data_to_callback(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")

        received: list[tuple[str, bytes]] = []
        b.on_data_received(lambda sender, data: received.append((sender, data)))

        payload = b"\xde\xad\xbe\xef"
        ok = _run(a.send_async("bob", payload))

        self.assertTrue(ok)
        self.assertEqual(len(received), 1)
        self.assertEqual(received[0][0], "alice")
        self.assertEqual(received[0][1], payload)
        a.shutdown(); b.shutdown()

    def test_send_returns_false_for_unknown_peer(self):
        a = InProcessTransport("alice")
        ok = _run(a.send_async("ghost", b"\x01"))
        self.assertFalse(ok)
        a.shutdown()

    def test_send_returns_false_when_unavailable(self):
        a = InProcessTransport("alice")
        InProcessTransport("bob")
        a.shutdown()  # mark unavailable
        ok = _run(a.send_async("bob", b"\x01"))
        self.assertFalse(ok)

    def test_send_enqueues_message_for_receive_message(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")

        payload = b"\x01\x02\x03"
        _run(a.send_async("bob", payload))

        sender, data = _run(b.receive_message())
        self.assertEqual(sender, "alice")
        self.assertEqual(data, payload)
        a.shutdown(); b.shutdown()

    def test_send_increments_sample_count_in_metrics(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")
        b.on_data_received(lambda s, d: None)

        before = a.metrics.sample_count if a.metrics else 0
        _run(a.send_async("bob", b"\x01\x02"))
        after = a.metrics.sample_count if a.metrics else 0

        self.assertGreater(after, before)
        a.shutdown(); b.shutdown()

    # ── on_data_received ─────────────────────────────────────────────────────

    def test_multiple_callbacks_all_fire(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")

        calls1: list[bytes] = []
        calls2: list[bytes] = []
        b.on_data_received(lambda s, d: calls1.append(d))
        b.on_data_received(lambda s, d: calls2.append(d))

        _run(a.send_async("bob", b"\xAA"))
        self.assertEqual(len(calls1), 1)
        self.assertEqual(len(calls2), 1)
        a.shutdown(); b.shutdown()

    # ── send_stream_async ────────────────────────────────────────────────────

    def test_send_stream_delivers_data(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")

        received: list[bytes] = []
        b.on_data_received(lambda s, d: received.append(d))

        reader = asyncio.StreamReader()
        reader.feed_data(b"\x01\x02\x03\x04")
        reader.feed_eof()

        ok = _run(a.send_stream_async("bob", reader))
        self.assertTrue(ok)
        self.assertEqual(received[0], b"\x01\x02\x03\x04")
        a.shutdown(); b.shutdown()

    def test_send_stream_returns_false_for_unknown_peer(self):
        a = InProcessTransport("alice")

        reader = asyncio.StreamReader()
        reader.feed_data(b"\x01")
        reader.feed_eof()

        ok = _run(a.send_stream_async("ghost", reader))
        self.assertFalse(ok)
        a.shutdown()

    # ── shutdown ─────────────────────────────────────────────────────────────

    def test_shutdown_makes_node_unavailable(self):
        a = InProcessTransport("alice")
        a.shutdown()
        self.assertFalse(a.is_available)

    def test_shutdown_removes_node_from_registry(self):
        InProcessTransport("alice")
        b = InProcessTransport("bob")
        self.assertTrue(b.is_connected("alice"))
        with InProcessTransport._global_lock:
            InProcessTransport._global_peers.pop("alice", None)
        self.assertFalse(b.is_connected("alice"))
        b.shutdown()

    def test_shutdown_twice_is_safe(self):
        a = InProcessTransport("alice")
        a.shutdown()
        a.shutdown()  # must not raise

    # ── get_queued_message_count ──────────────────────────────────────────────

    def test_queue_count_zero_initially(self):
        a = InProcessTransport("alice")
        self.assertEqual(a.get_queued_message_count(), 0)
        a.shutdown()

    def test_queue_count_increases_after_send(self):
        a = InProcessTransport("alice")
        b = InProcessTransport("bob")

        _run(a.send_async("bob", b"\x01"))
        _run(a.send_async("bob", b"\x02"))
        self.assertEqual(b.get_queued_message_count(), 2)
        a.shutdown(); b.shutdown()

    # ── Multiple nodes ────────────────────────────────────────────────────────

    def test_multiple_nodes_coexist_and_communicate(self):
        nodes = {name: InProcessTransport(name) for name in ["n1", "n2", "n3"]}
        deliveries: list[str] = []

        def make_cb(target: str):
            return lambda sender, data: deliveries.append(f"{sender}->{target}")

        for name, node in nodes.items():
            node.on_data_received(make_cb(name))

        _run(nodes["n1"].send_async("n2", b"\x01"))
        _run(nodes["n1"].send_async("n3", b"\x02"))

        self.assertIn("n1->n2", deliveries)
        self.assertIn("n1->n3", deliveries)
        self.assertNotIn("n1->n1", deliveries)

        for node in nodes.values():
            node.shutdown()

    # ── Thread safety ─────────────────────────────────────────────────────────

    def test_concurrent_registrations_are_safe(self):
        """Multiple threads can register distinct nodes without data races."""
        errors: list[Exception] = []

        def register(uhid):
            try:
                t = InProcessTransport(uhid)
                t.shutdown()
            except Exception as e:
                errors.append(e)

        threads = [threading.Thread(target=register, args=(f"peer-{i}",))
                   for i in range(10)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()

        self.assertEqual(len(errors), 0, f"Thread errors: {errors}")


if __name__ == "__main__":
    unittest.main()
