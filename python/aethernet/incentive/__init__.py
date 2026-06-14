# SPDX-License-Identifier: MIT

"""Incentive layer — the generic, value-agnostic relay-tip envelope and its
on-mesh dispatch service.

A ``TipPacketPayload`` carried inside a ``PacketType.TipPacket`` (24) signals
that one node wishes to credit another for some kind of relayed traffic. The
protocol attaches NO units, NO policy, and NO settlement semantics — what (if
anything) the signal is worth is entirely the host's business, expressed through
the injected ``MeshTipSettlementProvider``. A bare node carries the tip signal but
never moves value.
"""

from __future__ import annotations

from aethernet.incentive.tip_packet_payload import TipPacketPayload
from aethernet.incentive.mesh_tip_service import (
    MeshTipService,
    MeshTipSettlementProvider,
    NoopMeshTipSettlementProvider,
)

__all__ = [
    "TipPacketPayload",
    "MeshTipService",
    "MeshTipSettlementProvider",
    "NoopMeshTipSettlementProvider",
]
