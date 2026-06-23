# SPDX-License-Identifier: MIT

"""
Real LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class module.

Speaks the RYLR-class AT command set (Reyax RYLR896/RYLR998 and compatibles) on an
SX127x/SX126x radio, mirroring the C#/Go/Rust/C `LoRaSerialTransport`: opens the serial
port, configures the radio, sends with ``AT+SEND``, and surfaces inbound ``+RCV`` frames.

Requires the optional ``pyserial`` dependency::

    pip install "aethernet-protocol[lora]"   # or: pip install pyserial

Verification status: real driver; imports lazily so the core package still imports without
pyserial. Runtime-UNVERIFIED — not exercised against a physical module.
"""

import asyncio
import threading
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional

from aethernet.transport.per_transport_metrics import PerTransportMetrics
from aethernet.transport.transport_service import TransportService


@dataclass
class LoRaOptions:
    """Configuration for a RYLR-class serial LoRa module."""

    port_name: str  # "COM5" or "/dev/ttyUSB0" — required
    baud_rate: int = 115200
    address: int = 1  # this node's LoRa address (1-65535)
    network_id: int = 18  # RYLR network id
    band_hz: int = 868_500_000  # EU868; US915 = 915_000_000
    spreading_factor: int = 9  # 7-12
    bandwidth_index: int = 7  # 7=125kHz, 8=250, 9=500
    coding_rate: int = 1  # 1=4/5
    preamble_length: int = 12


class LoRaSerialTransport(TransportService):
    """LoRa transport over a serial-attached RYLR-class module."""

    def __init__(self, options: LoRaOptions) -> None:
        self._opts = options
        self._metrics = PerTransportMetrics()
        self._lock = threading.Lock()
        self._serial: Optional[object] = None
        self._available = False
        self._running = False
        self._callbacks: List[Callable[[str, bytes], None]] = []
        self._peer_addrs: Dict[str, int] = {}
        self._reader: Optional[threading.Thread] = None

    # ── Lifecycle ──────────────────────────────────────────────────────────────

    def open(self) -> None:
        """Open the serial port, configure the radio, and start the reader thread."""
        try:
            import serial  # pyserial — optional dependency
        except ImportError as exc:  # pragma: no cover - import-guard
            raise RuntimeError(
                "LoRa transport requires pyserial: pip install \"aethernet-protocol[lora]\""
            ) from exc

        with self._lock:
            if self._available:
                return
            self._serial = serial.Serial(self._opts.port_name, self._opts.baud_rate, timeout=1)
            self._configure()
            self._running = True
            self._available = True
            self._reader = threading.Thread(target=self._read_loop, name="lora-reader", daemon=True)
            self._reader.start()

    def _configure(self) -> None:
        assert self._serial is not None
        for cmd in (
            f"AT+ADDRESS={self._opts.address}",
            f"AT+NETWORKID={self._opts.network_id}",
            f"AT+BAND={self._opts.band_hz}",
            f"AT+PARAMETER={self._opts.spreading_factor},{self._opts.bandwidth_index},"
            f"{self._opts.coding_rate},{self._opts.preamble_length}",
        ):
            self._serial.write((cmd + "\r\n").encode("ascii"))  # type: ignore[attr-defined]

    def close(self) -> None:
        """Stop the reader thread and close the serial port."""
        with self._lock:
            self._running = False
            self._available = False
            ser = self._serial
            self._serial = None
        if ser is not None:
            try:
                ser.close()  # type: ignore[attr-defined]
            except Exception:
                pass

    # ── TransportService ───────────────────────────────────────────────────────

    @property
    def name(self) -> str:
        return "Aether Red (LoRa/CircleLink)"

    @property
    def is_available(self) -> bool:
        with self._lock:
            return self._available

    @property
    def max_bandwidth_bps(self) -> int:
        return 37_500  # SF7/BW125 ~= 37.5 kbps

    @property
    def max_range_meters(self) -> int:
        return 15_000  # up to ~15 km LOS

    @property
    def power_cost_relative(self) -> int:
        return 8  # high TX power (1-10 scale)

    @property
    def max_concurrent_peers(self) -> int:
        return 255

    @property
    def metrics(self) -> PerTransportMetrics:
        return self._metrics

    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        if not data:
            return False
        with self._lock:
            if not self._available or self._serial is None:
                return False
            addr = self._peer_addrs.get(peer_uhid, 0)  # 0 = broadcast (managed-flood mesh)
            ser = self._serial
        # Hex-encode so the payload survives the AT text protocol; length field is the hex length.
        payload = data.hex().upper()
        cmd = f"AT+SEND={addr},{len(payload)},{payload}\r\n"
        try:
            ser.write(cmd.encode("ascii"))  # type: ignore[attr-defined]
            self._metrics.record_sample(0, True, len(data))
            return True
        except Exception:
            self._metrics.record_sample(0, False, 0)
            return False

    async def send_stream_async(self, peer_uhid: str, data_stream: asyncio.StreamReader) -> bool:
        data = await data_stream.read()
        return await self.send_async(peer_uhid, data)

    def is_connected(self, peer_uhid: str) -> bool:
        return self.is_available  # connectionless broadcast medium

    def on_data_received(self, callback: Callable[[str, bytes], None]) -> None:
        with self._lock:
            self._callbacks.append(callback)

    def register_peer(self, peer_uhid: str, address: int) -> None:
        """Map an AetherNet peer UHID to a numeric LoRa node address (1-65535) for directed sends."""
        if not peer_uhid:
            return
        with self._lock:
            self._peer_addrs[peer_uhid] = address

    # ── Receive ────────────────────────────────────────────────────────────────

    def _read_loop(self) -> None:
        while True:
            with self._lock:
                running = self._running
                ser = self._serial
            if not running or ser is None:
                break
            try:
                line = ser.readline()  # type: ignore[attr-defined]
            except Exception:
                break
            if not line:
                continue
            self._handle_line(line.decode("ascii", errors="ignore").strip())

    def _handle_line(self, line: str) -> None:
        # RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
        if not line.startswith("+RCV="):
            return
        parts = line[5:].split(",")
        if len(parts) < 3:
            return
        try:
            addr = int(parts[0])
            data = bytes.fromhex(parts[2])
        except ValueError:
            return
        with self._lock:
            callbacks = list(self._callbacks)
        for callback in callbacks:
            callback(str(addr), data)
