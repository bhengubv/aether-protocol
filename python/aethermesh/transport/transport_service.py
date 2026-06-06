# SPDX-License-Identifier: MIT

"""Abstract base class for transport services."""

from abc import ABC, abstractmethod
from typing import Callable, Optional
import asyncio


class TransportService(ABC):
    """
    Abstract base class for Aether transport implementations.

    Any physical communication channel that can send and receive byte arrays
    between peers can be a transport: BLE, Wi-Fi Direct, NearLink, etc.
    """

    @property
    @abstractmethod
    def name(self) -> str:
        """Human-readable identifier (e.g., 'BLE', 'Wi-Fi Direct', 'NearLink')."""
        pass

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """Whether the transport is currently usable on this device."""
        pass

    @property
    @abstractmethod
    def max_bandwidth_bps(self) -> int:
        """Maximum throughput in bytes per second."""
        pass

    @property
    @abstractmethod
    def max_range_meters(self) -> int:
        """Maximum communication range in meters."""
        pass

    @property
    @abstractmethod
    def power_cost_relative(self) -> int:
        """Relative power consumption (1 = low, 10 = high)."""
        pass

    @property
    @abstractmethod
    def max_concurrent_peers(self) -> int:
        """Maximum simultaneous peer connections."""
        pass

    @abstractmethod
    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        """
        Send a byte array to a specific peer.

        Args:
            peer_uhid: The destination peer's UHID.
            data: The data to send.

        Returns:
            True on success, False on failure.
        """
        pass

    @abstractmethod
    async def send_stream_async(self, peer_uhid: str, data_stream: asyncio.StreamReader) -> bool:
        """
        Send a stream to a peer (for large transfers, voice, video).

        Args:
            peer_uhid: The destination peer's UHID.
            data_stream: An async stream to send.

        Returns:
            True on success, False on failure.
        """
        pass

    @abstractmethod
    def is_connected(self, peer_uhid: str) -> bool:
        """Check if a connection is active to a peer."""
        pass

    @abstractmethod
    def on_data_received(self, callback: Callable[[str, bytes], None]) -> None:
        """
        Register a callback to be called when data arrives from a peer.

        Args:
            callback: A function that takes (sender_uhid: str, data: bytes).
        """
        pass
