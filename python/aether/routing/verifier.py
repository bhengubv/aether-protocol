"""RREP verifier interface."""

from __future__ import annotations

from abc import ABC, abstractmethod

from aether.protocol.mesh_packet import MeshPacket


class RouteReplyVerifier(ABC):
    """Verifies that a received RREP was actually signed by the node it claims to come from.

    Without this check an intermediate forwarder can forge an RREP and hijack
    traffic for the destination. The default AcceptAll is permissive — fine for
    tests, not for production. Hosts ship a real impl backed by their security service.
    """

    async def verify(self, route_reply: MeshPacket) -> bool:
        return True


class AcceptAllRouteReplyVerifier(RouteReplyVerifier):
    """Accepts every RREP without verification. Tests / demos only."""

    async def verify(self, route_reply: MeshPacket) -> bool:
        return True
