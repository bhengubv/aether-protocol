# SPDX-License-Identifier: MIT

"""In-memory transport implementation for testing and local communication."""

import asyncio
import threading
from typing import Callable, Dict, List, Optional
from aethermesh.transport.per_transport_metrics import PerTransportMetrics
from aethermesh.transport.transport_service import TransportService


class InProcessTransport(TransportService):
    """
    In-memory transport using a class-level dictionary for inter-node communication.

    Thread-safe with threading.Lock. Useful for testing and local mesh simulation.
    """

    # Class-level registry of all nodes sharing this transport
    _global_peers: Dict[str, "InProcessTransport"] = {}
    _global_lock = threading.Lock()

    def __init__(self, peer_uhid: str) -> None:
        """
        Initialize an in-process transport instance.

        Args:
            peer_uhid: This node's UHID.
        """
        self._peer_uhid = peer_uhid
        self._is_available = True
        self._data_callbacks: List[Callable[[str, bytes], None]] = []
        self._message_queue: asyncio.Queue[tuple[str, bytes]] = asyncio.Queue()
        self._lock = threading.Lock()
        self._metrics = PerTransportMetrics()

        # Register this node globally
        with InProcessTransport._global_lock:
            InProcessTransport._global_peers[peer_uhid] = self

    @property
    def name(self) -> str:
        """Human-readable identifier."""
        return "InProcess"

    @property
    def is_available(self) -> bool:
        """Whether the transport is available."""
        return self._is_available

    @property
    def max_bandwidth_bps(self) -> int:
        """Maximum throughput (unlimited for in-memory)."""
        return 1_000_000_000  # 1 Gbps

    @property
    def max_range_meters(self) -> int:
        """Maximum range (unlimited for in-memory)."""
        return 10_000

    @property
    def power_cost_relative(self) -> int:
        """Relative power cost (lowest for in-memory)."""
        return 1

    @property
    def max_concurrent_peers(self) -> int:
        """Maximum concurrent peers."""
        return 1000

    @property
    def metrics(self) -> PerTransportMetrics:
        """Per-transport EWMA metrics (sample count, RTT, loss, throughput)."""
        return self._metrics

    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        """
        Send data to a peer through the global registry.

        Args:
            peer_uhid: The destination peer's UHID.
            data: The data to send.

        Returns:
            True if the peer exists and was delivered, False otherwise.
        """
        if not self._is_available:
            return False

        with InProcessTransport._global_lock:
            target = InProcessTransport._global_peers.get(peer_uhid)

        if target is None:
            return False

        # Deliver to target asynchronously
        try:
            await target._message_queue.put((self._peer_uhid, data))
            # Trigger callbacks
            for callback in target._data_callbacks:
                callback(self._peer_uhid, data)
            self._metrics.record_sample(0, True, len(data))
            return True
        except Exception:
            self._metrics.record_sample(0, False, 0)
            return False

    async def send_stream_async(self, peer_uhid: str, data_stream: asyncio.StreamReader) -> bool:
        """
        Send a stream to a peer by reading it completely.

        Args:
            peer_uhid: The destination peer's UHID.
            data_stream: An async stream to send.

        Returns:
            True on success, False on failure.
        """
        try:
            data = await data_stream.read()
            return await self.send_async(peer_uhid, data)
        except Exception:
            return False

    def is_connected(self, peer_uhid: str) -> bool:
        """Check if a peer is registered in the transport."""
        with InProcessTransport._global_lock:
            return peer_uhid in InProcessTransport._global_peers

    def on_data_received(self, callback: Callable[[str, bytes], None]) -> None:
        """
        Register a callback to be called when data arrives.

        Args:
            callback: A function that takes (sender_uhid: str, data: bytes).
        """
        with self._lock:
            self._data_callbacks.append(callback)

    def shutdown(self) -> None:
        """Unregister this node from the global registry."""
        with InProcessTransport._global_lock:
            InProcessTransport._global_peers.pop(self._peer_uhid, None)
        self._is_available = False

    async def receive_message(self) -> tuple[str, bytes]:
        """
        Wait for and retrieve the next message from the queue.

        Returns:
            A tuple of (sender_uhid, data).
        """
        return await self._message_queue.get()

    def get_queued_message_count(self) -> int:
        """Get the number of queued messages."""
        return self._message_queue.qsize()
