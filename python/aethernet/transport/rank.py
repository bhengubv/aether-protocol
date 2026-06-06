# SPDX-License-Identifier: MIT

"""Transport ranking — orders available transports by composite score."""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, List

if TYPE_CHECKING:
    from aethernet.transport.transport_service import TransportService


@dataclass
class RankedTransport:
    """A transport paired with its pre-computed composite score."""

    transport: "TransportService"
    score: float


def rank_transports(transports: List["TransportService"]) -> List[RankedTransport]:
    """
    Order available transports by composite score (highest first).

    Unavailable transports are excluded.  For transports without live metrics,
    the static prior (max_bandwidth_bps / power_cost_relative) is used.

    Args:
        transports: All registered transport backends.

    Returns:
        List of :class:`RankedTransport` sorted descending by score.
    """
    result: List[RankedTransport] = []

    for t in transports:
        if not t.is_available:
            continue

        m = t.metrics
        if m is not None:
            score = m.composite_score(t.max_bandwidth_bps, t.power_cost_relative)
        else:
            power = max(t.power_cost_relative, 1)
            score = t.max_bandwidth_bps / power

        result.append(RankedTransport(transport=t, score=score))

    result.sort(key=lambda rt: rt.score, reverse=True)
    return result
