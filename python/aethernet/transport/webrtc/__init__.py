# SPDX-License-Identifier: MIT

"""Direct peer-to-peer WebRTC transport for AetherNet (aiortc).

Mirrors the C# (``AetherNet.Transport.WebRtc``) and Go (``go/transport/webrtc``)
implementations: a :class:`WebRtcTransport` implementing the
:class:`~aethernet.transport.transport_service.TransportService` contract, a
:class:`Signal` / :class:`Signaling` abstraction for carrying the SDP/ICE handshake, and
an :class:`InMemorySignalingBus` reference signalling bus for same-process scenarios and
tests.
"""

from aethernet.transport.webrtc.signaling import (
    InMemorySignalingBus,
    RelaySignaling,
    Signal,
    Signaling,
    SignalType,
    decode_signal_frame,
    encode_signal_frame,
)
from aethernet.transport.webrtc.transport import (
    WebRtcTransport,
    default_ice_servers,
)

__all__ = [
    "InMemorySignalingBus",
    "RelaySignaling",
    "Signal",
    "Signaling",
    "SignalType",
    "WebRtcTransport",
    "decode_signal_frame",
    "encode_signal_frame",
    "default_ice_servers",
]
