# SPDX-License-Identifier: MIT

"""AetherNet Bandwidth Measurement Framework (ABMF) — W18-5.

Public API
----------
**Models**

- :class:`~aethernet.bandwidth.models.BandwidthConfidence` — quality tier enum
- :class:`~aethernet.bandwidth.models.BandwidthSample` — point-in-time estimate
- :class:`~aethernet.bandwidth.models.BandwidthProbeAck` — four-timestamp probe ACK
- :class:`~aethernet.bandwidth.models.BandwidthGossipPayload` — gossip warm-start
- :class:`~aethernet.bandwidth.models.NodeActivityState` — high-level node state
- :class:`~aethernet.bandwidth.models.TransportActivitySnapshot` — per-transport activity
- :class:`~aethernet.bandwidth.models.NodeActivitySnapshot` — full node activity

**Estimator**

- :class:`~aethernet.bandwidth.estimator.BandwidthEstimator` — BBRv3-inspired per-transport estimator

**Director**

- :class:`~aethernet.bandwidth.director.BandwidthDirector` — cross-transport synthesis & gossip

**Monitor**

- :class:`~aethernet.bandwidth.monitor.NodeActivityMonitor` — observable UI-facing monitor

Example
-------
::

    from aethernet.bandwidth import (
        BandwidthEstimator,
        BandwidthDirector,
        NodeActivityMonitor,
        BandwidthConfidence,
    )

    estimator = BandwidthEstimator("BLE", max_bandwidth_bps=2_000_000)
    director = BandwidthDirector()
    director.register(estimator)

    monitor = NodeActivityMonitor()
    monitor.register("BLE", estimator)
    monitor.start()
"""

from aethernet.bandwidth.models import (
    BandwidthConfidence,
    BandwidthGossipPayload,
    BandwidthProbeAck,
    BandwidthSample,
    NodeActivitySnapshot,
    NodeActivityState,
    TransportActivitySnapshot,
)
from aethernet.bandwidth.estimator import BandwidthEstimator
from aethernet.bandwidth.director import BandwidthDirector
from aethernet.bandwidth.monitor import NodeActivityMonitor

__all__ = [
    # Models
    "BandwidthConfidence",
    "BandwidthSample",
    "BandwidthProbeAck",
    "BandwidthGossipPayload",
    "NodeActivityState",
    "TransportActivitySnapshot",
    "NodeActivitySnapshot",
    # Core classes
    "BandwidthEstimator",
    "BandwidthDirector",
    "NodeActivityMonitor",
]
