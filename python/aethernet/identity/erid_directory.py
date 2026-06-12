# SPDX-License-Identifier: MIT

"""EridDirectory — resolves rotating ERID wire addresses to and from the stable peer
identities behind them — the piece that lets an ESTABLISHED relationship follow a peer's
rotating address while an outsider cannot.

A node derives its OWN secret routing key once (via
:func:`~aethernet.identity.ephemeral_routing_id.derive_routing_key`) and shares it with a
peer INSIDE the established Signal session — never on the wire. Each side stores the
other's routing key here, so either can compute the other's current ERID for addressing,
and reverse-resolve an inbound ERID back to the peer it belongs to. An outsider holds no
routing key and can do neither.

Port of the C# reference (``src/AetherNet.Core/Identity/EridDirectory.cs``).
"""

from __future__ import annotations

from aethernet.identity.ephemeral_routing_id import (
    DEFAULT_EPOCH_SECONDS,
    DEFAULT_LENGTH,
    derive,
)


class EridDirectory:
    """In-memory directory mapping peer UHIDs to their secret routing keys."""

    def __init__(
        self,
        my_routing_key: bytes,
        epoch_seconds: int = DEFAULT_EPOCH_SECONDS,
        erid_length: int = DEFAULT_LENGTH,
    ) -> None:
        """Create a directory for a node holding ``my_routing_key`` (copied defensively).

        Raises
        ------
        ValueError
            If ``my_routing_key`` is empty or ``epoch_seconds`` is not positive.
        """
        if not my_routing_key:
            raise ValueError("my_routing_key cannot be empty")
        if epoch_seconds <= 0:
            raise ValueError("epoch_seconds must be positive")
        self._my_routing_key = bytes(my_routing_key)
        self._epoch_seconds = epoch_seconds
        self._erid_length = erid_length
        self._peer_keys: dict[str, bytes] = {}

    def my_erid(self, unix_seconds: int) -> str:
        """Our own current ERID for the epoch containing ``unix_seconds``."""
        return derive(
            self._my_routing_key, unix_seconds, self._epoch_seconds, self._erid_length
        )

    def remember_peer(self, peer_uhid: str, peer_routing_key: bytes) -> None:
        """Store a peer's routing key, learned inside an established session.

        Idempotent; a later call replaces an earlier key for the same peer.

        Raises
        ------
        ValueError
            If ``peer_uhid`` or ``peer_routing_key`` is empty.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if not peer_routing_key:
            raise ValueError("peer_routing_key cannot be empty")
        self._peer_keys[peer_uhid] = bytes(peer_routing_key)

    def forget_peer(self, peer_uhid: str) -> bool:
        """Forget a peer (session torn down / excommunicated). False if unknown."""
        return self._peer_keys.pop(peer_uhid, None) is not None

    def erid_for_peer(self, peer_uhid: str, unix_seconds: int) -> str | None:
        """The current ERID a known peer presents this epoch, or ``None`` if we hold no
        key for them."""
        key = self._peer_keys.get(peer_uhid)
        if key is None:
            return None
        return derive(key, unix_seconds, self._epoch_seconds, self._erid_length)

    def resolve_peer(self, erid: str, unix_seconds: int) -> str | None:
        """Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the
        given epoch, or ``None`` if no known peer currently presents it. O(n) over known
        peers — a node's actual relationship count."""
        if not erid:
            return None
        for uhid, key in self._peer_keys.items():
            if derive(key, unix_seconds, self._epoch_seconds, self._erid_length) == erid:
                return uhid
        return None

    @property
    def known_peer_count(self) -> int:
        """Number of peers whose routing key we currently hold."""
        return len(self._peer_keys)
