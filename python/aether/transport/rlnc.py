# SPDX-License-Identifier: MIT
"""
RLNC Engine — Random Linear Network Coding over GF(2⁸).

Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1  (0x11D, same as AES Rijndael).

Components
──────────
  _gf256_exp / _gf256_log — precomputed GF(2⁸) log/exp tables.
  gf256_mul / gf256_inv   — O(1) field arithmetic via table lookup.
  RlncEncoder              — systematic + repair packet generation.
  RlncDecoder              — incremental Gauss-Jordan elimination.
  RlncCodec                — IFecCodec-compatible adapter.

Wire format per packet:
  [ K coefficient bytes ][ symbolSize data bytes ]
"""

from __future__ import annotations

import os
from typing import List, Optional, Tuple

# ── GF(2⁸) tables ─────────────────────────────────────────────────────────────

_gf256_exp: bytearray = bytearray(512)
_gf256_log: bytearray = bytearray(256)

def _build_gf256_tables() -> None:
    x = 1
    for i in range(255):
        _gf256_exp[i] = x
        _gf256_log[x] = i
        x <<= 1
        if x & 0x100:
            x ^= 0x11D  # reduce mod primitive polynomial
        x &= 0xFF
    for i in range(255, 512):
        _gf256_exp[i] = _gf256_exp[i - 255]
    _gf256_log[1] = 0  # log_α(1) = 0

_build_gf256_tables()


def gf256_mul(a: int, b: int) -> int:
    """Multiply two GF(2⁸) elements using log/exp tables."""
    if a == 0 or b == 0:
        return 0
    return _gf256_exp[_gf256_log[a] + _gf256_log[b]]


def gf256_inv(a: int) -> int:
    """Multiplicative inverse in GF(2⁸): Inv(a) = α^(255 − log_α(a))."""
    if a == 0:
        raise ZeroDivisionError("rlnc: GF256 inverse of zero")
    return _gf256_exp[255 - _gf256_log[a]]


def gf256_add(a: int, b: int) -> int:
    """Addition in GF(2⁸) = XOR."""
    return a ^ b


# ── RlncEncoder ───────────────────────────────────────────────────────────────

class RlncEncoder:
    """
    Encodes K source symbols using systematic + random-repair RLNC packets.

    The first K packets are systematic (identity coefficient vectors; byte-identical
    to the source symbols).  Subsequent packets use random GF(2⁸) coefficients.
    """

    __slots__ = ("_source", "_next_index", "_systematic")

    def __init__(self, source: List[bytearray], systematic: bool = True) -> None:
        if not source:
            raise ValueError("rlnc: source must have at least one symbol")
        self._source:     List[bytearray] = source
        self._next_index: int             = 0
        self._systematic: bool            = systematic

    @property
    def generation_size(self) -> int:
        return len(self._source)

    @property
    def symbol_size(self) -> int:
        return len(self._source[0])

    def next_packet(self) -> Tuple[bytearray, bytearray]:
        """
        Return ``(coefficients, encoded_symbol)`` for the next packet.

        First *generation_size* packets are systematic when constructed with
        ``systematic=True``; subsequent packets are random repair symbols.
        """
        k = self.generation_size
        s = self.symbol_size

        if self._systematic and self._next_index < k:
            # Systematic: e_i coefficient vector.
            coefficients              = bytearray(k)
            coefficients[self._next_index] = 1
            encoded_symbol            = bytearray(self._source[self._next_index])
        else:
            # Repair: random GF(256) coefficient vector.
            coefficients = bytearray(os.urandom(k))
            if all(c == 0 for c in coefficients):
                coefficients[0] = 1
            encoded_symbol = self._encode_symbol(coefficients)

        self._next_index += 1
        return coefficients, encoded_symbol

    def _encode_symbol(self, coefficients: bytearray) -> bytearray:
        s   = self.symbol_size
        out = bytearray(s)
        for k_idx, sym in enumerate(self._source):
            c = coefficients[k_idx]
            if c == 0:
                continue
            for i in range(s):
                out[i] = gf256_add(out[i], gf256_mul(c, sym[i]))
        return out


# ── RlncDecoder ───────────────────────────────────────────────────────────────

class RlncDecoder:
    """
    Incremental Gauss-Jordan decoder over GF(2⁸).

    Maintains the accumulated coefficient matrix in Reduced Row Echelon Form
    (RREF) as packets arrive.  Decoding is immediate when :attr:`rank` equals K.
    """

    __slots__ = ("_k", "_symbol_size", "_pivot_coeff", "_pivot_data", "_rank")

    def __init__(self, k: int, symbol_size: int) -> None:
        self._k:           int                        = k
        self._symbol_size: int                        = symbol_size
        self._pivot_coeff: List[Optional[bytearray]]  = [None] * k
        self._pivot_data:  List[Optional[bytearray]]  = [None] * k
        self._rank:        int                        = 0

    @property
    def rank(self) -> int:
        """Number of linearly independent packets received."""
        return self._rank

    @property
    def is_complete(self) -> bool:
        """``True`` when all K source symbols can be reconstructed."""
        return self._rank == self._k

    def add_packet(self, coefficients: bytes, encoded_symbol: bytes) -> bool:
        """
        Submit an encoded packet.

        Returns ``True`` if rank increased (packet was linearly independent).
        """
        k = self._k
        s = self._symbol_size
        row  = bytearray(coefficients)
        data = bytearray(encoded_symbol)

        # ── Forward-elimination ──────────────────────────────────────────────
        for j in range(k):
            if row[j] == 0 or self._pivot_coeff[j] is None:
                continue
            c  = row[j]
            pr = self._pivot_coeff[j]
            pd = self._pivot_data[j]
            for i in range(k):
                row[i]  = gf256_add(row[i],  gf256_mul(c, pr[i]))
            for i in range(s):
                data[i] = gf256_add(data[i], gf256_mul(c, pd[i]))

        # ── Find pivot column ────────────────────────────────────────────────
        pivot_col = next((j for j in range(k) if row[j] != 0), -1)
        if pivot_col < 0:
            return False  # linearly dependent

        # ── Normalise: scale so pivot element = 1 ────────────────────────────
        inv = gf256_inv(row[pivot_col])
        for i in range(k):
            row[i]  = gf256_mul(inv, row[i])
        for i in range(s):
            data[i] = gf256_mul(inv, data[i])

        # ── Back-substitution ────────────────────────────────────────────────
        for r in range(k):
            pr = self._pivot_coeff[r]
            if pr is None:
                continue
            c = pr[pivot_col]
            if c == 0:
                continue
            pd = self._pivot_data[r]
            for i in range(k):
                pr[i]  = gf256_add(pr[i],  gf256_mul(c, row[i]))
            for i in range(s):
                pd[i] = gf256_add(pd[i], gf256_mul(c, data[i]))

        self._pivot_coeff[pivot_col] = row
        self._pivot_data[pivot_col]  = data
        self._rank += 1
        return True

    def try_decode(self) -> Optional[bytes]:
        """
        Return decoded source bytes when ``is_complete``, or ``None`` otherwise.
        """
        if not self.is_complete:
            return None
        k = self._k
        s = self._symbol_size
        result = bytearray(k * s)
        for j in range(k):
            result[j * s : j * s + s] = self._pivot_data[j]
        return bytes(result)


# ── RlncCodec ────────────────────────────────────────────────────────────────

def _split_into_symbols(source: bytes, k: int, symbol_size: int) -> List[bytearray]:
    symbols: List[bytearray] = []
    for i in range(k):
        sym    = bytearray(symbol_size)
        offset = i * symbol_size
        length = min(symbol_size, len(source) - offset)
        if length > 0:
            sym[:length] = source[offset : offset + length]
        symbols.append(sym)
    return symbols


class RlncCodec:
    """
    RLNC FEC codec over GF(2⁸) — bulk encode and decode API.

    Wire format per encoded packet:
        ``[ K coefficient bytes ][ symbolSize data bytes ]``

    Compatible with the IFecCodec interface used by the Aether transport layer.
    """

    codec_name:            str   = "RLNC-GF256"
    device_tier_required:  int   = 0
    overhead_fraction:     float = 0.05
    fixed_symbol_size_bytes: int = 0

    def __init__(self, generation_size: int = 16) -> None:
        if not 1 <= generation_size <= 255:
            raise ValueError("rlnc: generation_size must be in [1, 255]")
        self._k: int = generation_size

    @property
    def generation_size(self) -> int:
        return self._k

    def encode(self, source: bytes, target_symbol_count: int) -> bytes:
        """
        Encode *source* into *target_symbol_count* concatenated packets.
        Each packet = ``[ K coeff bytes ][ symbolSize bytes ]``.
        """
        if not source:
            raise ValueError("rlnc: source must not be empty")
        k           = self._k
        symbol_size = (len(source) + k - 1) // k
        packet_size = k + symbol_size
        symbols     = _split_into_symbols(source, k, symbol_size)
        enc         = RlncEncoder(symbols, systematic=True)
        output      = bytearray(target_symbol_count * packet_size)

        for i in range(target_symbol_count):
            coeff, data = enc.next_packet()
            offset      = i * packet_size
            output[offset : offset + k]               = coeff
            output[offset + k : offset + packet_size] = data

        return bytes(output)

    def try_decode(
        self,
        received_symbols: List[bytes],
        source_symbol_count: int,
    ) -> Optional[bytes]:
        """
        Reconstruct source from *received_symbols*.
        Returns decoded bytes or ``None`` if rank < K.
        """
        if not received_symbols:
            return None
        k           = self._k
        symbol_size = len(received_symbols[0]) - k
        if symbol_size <= 0:
            return None

        dec = RlncDecoder(k, symbol_size)
        for pkt in received_symbols:
            dec.add_packet(pkt[:k], pkt[k:])
            if dec.is_complete:
                break

        return dec.try_decode()
