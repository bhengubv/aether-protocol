"""Packet signing service with nonce deduplication and expiry."""

import struct
import hashlib
import time
from typing import Dict, Tuple
from datetime import datetime, timedelta
from aether.protocol.mesh_packet import MeshPacket
from aether.security.ed25519_service import Ed25519SigningService


class PacketSigningService:
    """
    Service for signing MeshPackets and deduplicating based on nonce.

    Maintains a deduplication cache of (sender_uhid, nonce) pairs with
    a 5-minute TTL to prevent replay attacks.
    """

    def __init__(self, max_cache_size: int = 10000) -> None:
        """
        Initialize the packet signing service.

        Args:
            max_cache_size: Maximum number of entries to keep in the nonce cache.
        """
        self._nonce_cache: Dict[Tuple[str, bytes], datetime] = {}
        self._max_cache_size = max_cache_size
        self._last_cleanup = time.time()

    def sign_packet(self, packet: MeshPacket, private_key: bytes) -> None:
        """
        Sign a packet using Ed25519.

        Constructs the signable data as per the protocol spec and signs it.

        Args:
            packet: The MeshPacket to sign.
            private_key: 32-byte Ed25519 private key.
        """
        if packet is None:
            raise ValueError("packet cannot be None")
        if private_key is None:
            raise ValueError("private_key cannot be None")

        signable_data = self._construct_signable_data(packet)
        packet.signature = Ed25519SigningService.sign(private_key, signable_data)

    def verify_packet(self, packet: MeshPacket, public_key: bytes) -> bool:
        """
        Verify a packet's signature and check for replay attacks.

        Args:
            packet: The MeshPacket to verify.
            public_key: 32-byte Ed25519 public key.

        Returns:
            True if the signature is valid and the packet has not been replayed.
        """
        if packet is None:
            raise ValueError("packet cannot be None")
        if public_key is None:
            raise ValueError("public_key cannot be None")

        # Verify signature
        signable_data = self._construct_signable_data(packet)
        if not Ed25519SigningService.verify(public_key, signable_data, packet.signature):
            return False

        # Check for replay
        if self._is_replayed(packet.source_uhid, packet.packet_nonce):
            return False

        # Record the nonce
        self._record_nonce(packet.source_uhid, packet.packet_nonce)

        return True

    def _construct_signable_data(self, packet: MeshPacket) -> bytes:
        """
        Construct the signable data as per the protocol spec (Section 2.3).

        The signature covers:
            PacketNonce (8 bytes)
            || TimestampMs (8 bytes, little-endian int64)
            || Type (4 bytes, little-endian int32)
            || SourceUhidLength (4 bytes, little-endian int32)
            || SourceUhid (UTF-8 bytes)
            || DestinationUhidLength (4 bytes, little-endian int32)
            || DestinationUhid (UTF-8 bytes)
            || SHA-256(Payload) (32 bytes)
            || Ttl (4 bytes, little-endian int32)
            || Priority (4 bytes, little-endian int32)
        """
        parts = []

        # PacketNonce (8 bytes)
        parts.append(packet.packet_nonce[:8] if len(packet.packet_nonce) >= 8 else packet.packet_nonce.ljust(8, b'\x00'))

        # TimestampMs (8 bytes, little-endian int64)
        parts.append(struct.pack("<q", packet.timestamp_ms))

        # Type (4 bytes, little-endian int32)
        parts.append(struct.pack("<i", packet.type))

        # SourceUhid length and data
        source_bytes = packet.source_uhid.encode("utf-8")
        parts.append(struct.pack("<i", len(source_bytes)))
        parts.append(source_bytes)

        # DestinationUhid length and data
        dest_bytes = packet.destination_uhid.encode("utf-8")
        parts.append(struct.pack("<i", len(dest_bytes)))
        parts.append(dest_bytes)

        # SHA-256(Payload)
        payload_hash = hashlib.sha256(packet.payload).digest()
        parts.append(payload_hash)

        # Ttl (4 bytes, little-endian int32)
        parts.append(struct.pack("<i", packet.ttl))

        # Priority (4 bytes, little-endian int32)
        parts.append(struct.pack("<i", packet.priority))

        return b"".join(parts)

    def _is_replayed(self, sender_uhid: str, nonce: bytes) -> bool:
        """
        Check if a (sender_uhid, nonce) pair has been seen recently.

        Args:
            sender_uhid: The sender's UHID.
            nonce: The packet nonce.

        Returns:
            True if this pair was seen within the last 5 minutes.
        """
        # Periodically clean up expired entries
        now = time.time()
        if now - self._last_cleanup > 60:  # Clean up every 60 seconds
            self._cleanup_expired_entries()
            self._last_cleanup = now

        cache_key = (sender_uhid, nonce)
        return cache_key in self._nonce_cache

    def _record_nonce(self, sender_uhid: str, nonce: bytes) -> None:
        """
        Record that we've seen a (sender_uhid, nonce) pair.

        Args:
            sender_uhid: The sender's UHID.
            nonce: The packet nonce.
        """
        cache_key = (sender_uhid, nonce)
        expiry = datetime.utcnow() + timedelta(seconds=300)  # 5-minute TTL

        self._nonce_cache[cache_key] = expiry

        # If cache is getting too large, do a cleanup
        if len(self._nonce_cache) > self._max_cache_size:
            self._cleanup_expired_entries()

    def _cleanup_expired_entries(self) -> None:
        """Remove expired entries from the nonce cache."""
        now = datetime.utcnow()
        expired_keys = [
            key for key, expiry in self._nonce_cache.items() if expiry <= now
        ]
        for key in expired_keys:
            del self._nonce_cache[key]
