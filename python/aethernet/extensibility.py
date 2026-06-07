# SPDX-License-Identifier: MIT

"""Extension seams hosts can wire up to participate in incentive accounting,
cloud-relay fallbacks, and feature gating. Default no-op implementations let
the protocol layer call through these uniformly.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from decimal import Decimal
from typing import Optional

from aethernet.protocol.mesh_packet import MeshPacket
from aethernet.models import DtnBundle, SosAlert


class IncentiveProvider(ABC):
    """Records relays for reward calculation; decides whether a packet jumps the priority queue."""

    async def record_relay(self, local_uhid: str, packet: MeshPacket) -> None:
        return None

    async def should_prioritize(self, packet: MeshPacket) -> bool:
        return False

    async def record_creator_tip(
        self, creator_uhid: str, amount: Decimal, content_hash: str
    ) -> None:
        """Called when the local user tips a content author. Distinct from
        record_relay (relay credit - paid to nodes that forward bytes); this
        records direct creator -> consumer settlement (paid to the user who
        AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay)
        wire their settlement logic here. Default no-op does nothing.
        Added in v1.2.0 - closes Issue #61 surfaced by Wave 16.
        """
        return None


class BackendClient(ABC):
    """Optional cloud-relay seam. Default returns False everywhere."""

    async def relay_message(
        self, sender_uhid: str, recipient_uhid: str, encrypted_content: bytes, priority: int
    ) -> bool:
        return False

    async def sync_dtn_bundle(self, bundle: DtnBundle) -> bool:
        return False

    async def sync_sos(self, alert: SosAlert) -> bool:
        return False


class FeatureFlagProvider(ABC):
    """Gates protocol features behind remote configuration. Default: every feature enabled."""

    async def is_enabled(self, feature_name: str) -> bool:
        return True


class NoopIncentiveProvider(IncentiveProvider):
    pass


class NoopBackendClient(BackendClient):
    pass


class NoopFeatureFlagProvider(FeatureFlagProvider):
    pass
