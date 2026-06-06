# SPDX-License-Identifier: MIT

"""Replication strategy for DTN bundles."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import List, Optional

from aethernet.models import (
    BundlePriority,
    DtnBundle,
    NodeCapabilities,
    PeerInfo,
)


class ReplicationStrategy(ABC):
    """Decides which connected peers should receive a copy of a bundle on the
    next replication pass.
    """

    @abstractmethod
    def select_targets(
        self,
        bundle: DtnBundle,
        peers: List[PeerInfo],
        local_geohash: Optional[str],
    ) -> List[str]:
        ...


def _shared_prefix(a: Optional[str], b: str) -> int:
    if not a or not b:
        return 0
    n = min(len(a), len(b))
    for i in range(n):
        if a[i] != b[i]:
            return i
    return n


class GeohashEpidemicStrategy(ReplicationStrategy):
    """Default strategy.

    SOS bundles fan out to every eligible DTN-carrier peer up to the copy cap.
    Normal bundles prefer peers whose geohash shares a longer prefix with the
    recipient's last known geohash than the local node — i.e. peers that are at
    least as close to the recipient as we are. Ties broken by reliability.
    """

    def select_targets(
        self,
        bundle: DtnBundle,
        peers: List[PeerInfo],
        local_geohash: Optional[str],
    ) -> List[str]:
        slots = bundle.max_copies - bundle.copy_count
        if slots <= 0:
            return []

        eligible = [
            p
            for p in peers
            if p.uhid
            and p.uhid != bundle.sender_uhid
            and not p.is_blocked
            and (p.capabilities & int(NodeCapabilities.DTN_CARRIER))
        ]
        if not eligible:
            return []

        if bundle.priority == BundlePriority.SOS:
            return [p.uhid for p in eligible[:slots]]

        if bundle.recipient_last_geohash:
            local_prox = _shared_prefix(local_geohash, bundle.recipient_last_geohash)
            ranked = sorted(
                (
                    (
                        _shared_prefix(p.geohash, bundle.recipient_last_geohash),
                        p.reliability_score,
                        p,
                    )
                    for p in eligible
                ),
                key=lambda t: (-t[0], -t[1]),
            )
            ranked = [r for r in ranked if r[0] >= local_prox]
            return [r[2].uhid for r in ranked[:slots]]

        ranked = sorted(eligible, key=lambda p: -p.reliability_score)
        return [p.uhid for p in ranked[:slots]]
