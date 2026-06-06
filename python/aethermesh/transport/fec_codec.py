# SPDX-License-Identifier: MIT

"""Forward Error Correction (FEC) codec interface."""

from abc import ABC, abstractmethod
from typing import Optional, Sequence


class FecCodec(ABC):
    """
    Abstract base for FEC codec decorators.

    Concrete implementations:
    - ``PolarSCLCodec``     — Arıkan polar codes + SCL decoder (BLE, ≤ 512 B blocks)
    - ``RaptorRFC5053Codec`` — rateless Raptor fountain codes (LoRa)

    The codec is transport-agnostic: it operates purely on byte arrays.
    The transport calls :meth:`encode` before writing to the wire and
    :meth:`try_decode` after accumulating received symbols.
    """

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    @abstractmethod
    def codec_name(self) -> str:
        """Human-readable identifier, e.g. ``'Polar-SCL'`` or ``'Raptor-RFC5053'``."""

    @property
    @abstractmethod
    def device_tier_required(self) -> int:
        """
        Minimum DeviceTier needed to run this codec:
        - 0 = Full (desktop / server / phone) — all codecs supported.
        - 1 = Constrained (embedded Linux) — Polar-SCL supported.
        - 2 = Ultra-constrained (MCU <64 KB RAM) — no FEC.
        """

    @property
    @abstractmethod
    def overhead_fraction(self) -> float:
        """
        Fractional redundancy added (e.g. ``0.30`` = 30 %).
        Used by the predictive selector when scoring FEC-decorated transports.
        """

    @property
    @abstractmethod
    def fixed_symbol_size_bytes(self) -> int:
        """
        Fixed symbol size in bytes for block codes (e.g. 64 for BLE Polar).
        Returns ``0`` for variable-symbol codecs (e.g. Raptor).
        """

    # ── Encode ────────────────────────────────────────────────────────────────

    @abstractmethod
    def encode(self, source: bytes, target_symbol_count: int) -> bytes:
        """
        Encode *source* into *target_symbol_count* concatenated output symbols.

        For systematic codes the first ⌈len(source) / symbol_size⌉ output symbols
        are byte-identical to the input; repair symbols follow.

        Args:
            source:              Original data to protect.
            target_symbol_count: Total output symbols to produce (≥ source symbols).

        Returns:
            Encoded bytes (all output symbols concatenated).

        Raises:
            ValueError: If *target_symbol_count* is less than the minimum required.
        """

    # ── Decode ────────────────────────────────────────────────────────────────

    @abstractmethod
    def try_decode(
        self,
        received_symbols: Sequence[bytes],
        source_symbol_count: int,
    ) -> Optional[bytes]:
        """
        Attempt to reconstruct source from received symbols.

        May succeed with fewer than *source_symbol_count* symbols (fountain
        property) or return ``None`` if too many were lost.

        Args:
            received_symbols:    Byte arrays of individual received symbols.
            source_symbol_count: Number of source symbols in the original object.

        Returns:
            Reconstructed source bytes on success, or ``None`` on failure.
        """
