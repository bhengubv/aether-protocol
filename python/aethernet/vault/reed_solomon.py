# SPDX-License-Identifier: MIT
#
# Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault — the production
# erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
# reconstruct it"). Python port of AetherNet.Vault.ReedSolomonCodec, byte-identical to the C# reference
# and every other language implementation.
#
# FIELD: arithmetic is over GF(2^8) with primitive polynomial x^8+x^4+x^3+x^2+1 (0x11D, the AES/Rijndael
# polynomial), alpha = 2 — the SAME field as aethernet.transport.rlnc (RlncCodec). Identical field =>
# identical parity bytes, which is what makes a parity shard scattered by one node decodable by any
# other node on the mesh.
#
# SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0..K-1) is exactly
#   plaintext[i*shard_size .. i*shard_size+shard_size], zero-padded if short, shard_size = ceil(size/K)
# The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
# original.
#
# MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ^ y_j) over GF(256) with two
# disjoint sets of distinct field elements (y_0..y_{K-1} = 0..K-1, x_0..x_{M-1} = K..K+M-1). Every
# square submatrix of a Cauchy matrix is invertible, so stacked on the systematic K x K identity it
# yields a true MDS code: any K of the N generator rows are linearly independent => any K surviving
# shards reconstruct the original; K-1 or fewer is unrecoverable.

from __future__ import annotations

from typing import Dict, List

# ── GF(2^8) arithmetic — primitive polynomial 0x11D, alpha = 2 ──────────────────
# Byte-for-byte identical field to aethernet.transport.rlnc's GF256 (that module's tables are
# module-private, so the table-generation logic is mirrored here rather than referenced across the
# module boundary — identical tables are what guarantee identical parity bytes).

_GF_EXP = bytearray(512)  # _GF_EXP[i] = alpha^i; doubled to avoid modular wrap in mul
_GF_LOG = bytearray(256)  # _GF_LOG[v] = log_alpha(v) for v in [1, 255]


def _build_gf_tables() -> None:
    x = 1
    for i in range(255):
        _GF_EXP[i] = x
        _GF_LOG[x] = i
        x <<= 1
        if x & 0x100:
            x ^= 0x11D  # reduce mod p(x) = x^8+x^4+x^3+x^2+1
        x &= 0xFF
    for i in range(255, 512):
        _GF_EXP[i] = _GF_EXP[i - 255]
    _GF_LOG[1] = 0


_build_gf_tables()


def _gf_mul(a: int, b: int) -> int:
    if a == 0 or b == 0:
        return 0
    return _GF_EXP[_GF_LOG[a] + _GF_LOG[b]]


def _gf_inv(a: int) -> int:
    if a == 0:
        raise ZeroDivisionError("vault: GF256 inverse of zero")
    return _GF_EXP[255 - _GF_LOG[a]]


def split_into_data_shards(data: bytes, k: int) -> List[bytearray]:
    """Slice ``data`` into K equal zero-padded data shards of length ``shard_size = ceil(len(data)/K)``.
    This is the systematic prefix the encoder leaves unchanged (byte-identical to the C# vault data
    layout)."""
    if k < 1:
        raise ValueError("vault: K must be >= 1")
    if len(data) == 0:
        raise ValueError("vault: data must not be empty")
    shard_size = (len(data) + k - 1) // k
    shards: List[bytearray] = []
    for i in range(k):
        shard = bytearray(shard_size)
        offset = i * shard_size
        if offset < len(data):
            length = min(shard_size, len(data) - offset)
            shard[:length] = data[offset : offset + length]
        shards.append(shard)
    return shards


class ReedSolomonCodec:
    """Systematic Reed-Solomon (K data + M parity) erasure codec over GF(2^8).

    The K data shards are the plaintext partitioned into equal zero-padded slices (byte-identical to the
    Vault data layout); the M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any
    K of the K+M shards reconstruct the original.
    """

    def __init__(self, k: int, m: int) -> None:
        """
        Args:
            k: Number of data shards (must be >= 1).
            m: Number of parity shards (must be >= 0). K + M must be <= 256.
        """
        if k < 1:
            raise ValueError("vault: K must be >= 1")
        if m < 0:
            raise ValueError("vault: M must be >= 0")
        if k + m > 256:
            raise ValueError("vault: K + M must be <= 256")
        self._k = k
        self._m = m
        self._n = k + m
        # Parity generator rows: _parity[i] (i = 0..M-1) is the K-byte Cauchy coefficient vector for
        # parity shard K+i. Together with the implicit K x K systematic identity for the data shards
        # these form the full N x K MDS generator matrix.
        self._parity = self._build_cauchy_parity_matrix(k, m)

    @property
    def shard_count(self) -> int:
        """Total shard count (K + M)."""
        return self._n

    @property
    def data_shards(self) -> int:
        """K."""
        return self._k

    @property
    def parity_shards(self) -> int:
        """M."""
        return self._m

    def encode(self, data_shards: List[bytearray]) -> List[bytearray]:
        """Encode ``data_shards`` (K byte arrays of equal length ``shard_size``) into the full set of N
        shards. Shards 0..K-1 are the data shards unchanged (systematic); shards K..N-1 are the M
        Reed-Solomon parity shards. The returned shards are fresh copies — callers keep ownership."""
        if data_shards is None:
            raise ValueError("vault: data_shards must not be None")
        if len(data_shards) != self._k:
            raise ValueError(f"vault: expected {self._k} data shards, got {len(data_shards)}")

        shard_size = len(data_shards[0])
        for j in range(self._k):
            if data_shards[j] is None or len(data_shards[j]) != shard_size:
                raise ValueError("vault: all data shards must be non-null and the same length")

        shards: List[bytearray] = [bytearray() for _ in range(self._n)]

        # Systematic: the first K shards ARE the data shards (defensive copy — callers keep ownership).
        for j in range(self._k):
            shards[j] = bytearray(data_shards[j])

        # Parity: shard K+i = sum_j parity[i][j] * data_shards[j] over GF(256).
        for i in range(self._m):
            parity_shard = bytearray(shard_size)
            coeffs = self._parity[i]
            for j in range(self._k):
                c = coeffs[j]
                if c == 0:
                    continue
                src = data_shards[j]
                for b in range(shard_size):
                    parity_shard[b] ^= _gf_mul(c, src[b])
            shards[self._k + i] = parity_shard

        return shards

    def decode_data_shards(self, available: Dict[int, bytes]) -> List[bytearray]:
        """Reconstruct the K data shards from any K available shards. ``available`` maps a shard index
        (0..N-1) to its bytes; exactly K distinct entries are required, all of equal length. Returns the
        K data shards (indices 0..K-1, in order). Raises ``ValueError`` if fewer than K shards are
        supplied (K-1 or fewer is unrecoverable)."""
        if available is None:
            raise ValueError("vault: available must not be None")

        # Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
        indices = sorted(
            idx
            for idx, val in available.items()
            if 0 <= idx < self._n and val is not None
        )
        if len(indices) > self._k:
            indices = indices[: self._k]

        if len(indices) < self._k:
            raise ValueError(
                f"vault: cannot decode — only {len(indices)}/{self._k} shards available"
            )

        shard_size = len(available[indices[0]])
        for idx in indices:
            if len(available[idx]) != shard_size:
                raise ValueError("vault: all supplied shards must be the same length")

        # Fast path: if all K data shards (0..K-1) are present, no inversion is needed — the data is the
        # systematic prefix verbatim. This is the common, byte-identical-to-canonical recovery case.
        if all(idx < self._k for idx in indices):
            direct: List[bytearray] = [bytearray() for _ in range(self._k)]
            for idx in indices:
                direct[idx] = bytearray(available[idx])
            return direct

        # General path: build the K x K generator submatrix A for the picked shard indices, invert it,
        # and apply A^-1 to the picked symbol-vectors to recover the K source (data) symbols.
        a: List[bytearray] = [bytearray() for _ in range(self._k)]
        rhs: List[bytearray] = [bytearray() for _ in range(self._k)]
        for r in range(self._k):
            idx = indices[r]
            a[r] = self._generator_row(idx)
            rhs[r] = bytearray(available[idx])

        inv = self._invert_matrix(a)

        data: List[bytearray] = [bytearray() for _ in range(self._k)]
        for r in range(self._k):
            symbol = bytearray(shard_size)
            for col in range(self._k):
                coeff = inv[r][col]
                if coeff == 0:
                    continue
                src = rhs[col]
                for b in range(shard_size):
                    symbol[b] ^= _gf_mul(coeff, src[b])
            data[r] = symbol

        return data

    # ── file-level helpers (mirror the Go vault_codec.go EncodeData / ReconstructData) ──────────────

    def encode_data(self, data: bytes) -> List[bytearray]:
        """Split ``data`` into K systematic data shards and return the full set of N = K+M shards."""
        return self.encode(split_into_data_shards(data, self._k))

    def reconstruct_data(self, available: Dict[int, bytes], original_size: int) -> bytes:
        """Reconstruct the original blob of ``original_size`` bytes from any K surviving shards.
        ``available`` maps a shard index (0..N-1) to its bytes. Recovery concatenates the K recovered
        data shards in index order then trims to the original size. Raises ``ValueError`` if fewer than
        K shards are supplied."""
        data_shards = self.decode_data_shards(available)
        if original_size < 0:
            raise ValueError("vault: original_size must be >= 0")

        shard_size = len(data_shards[0])
        out = bytearray(self._k * shard_size)
        for j in range(self._k):
            out[j * shard_size : j * shard_size + shard_size] = data_shards[j]
        if original_size > len(out):
            raise ValueError("vault: original_size exceeds reconstructed length")
        return bytes(out[:original_size])

    # ── generator matrix ────────────────────────────────────────────────────────

    def _generator_row(self, index: int) -> bytearray:
        """The K-byte generator row for shard ``index`` (identity for a data shard, Cauchy coefficients
        for a parity shard)."""
        if index < self._k:
            # Systematic data row = standard basis vector e_index.
            row = bytearray(self._k)
            row[index] = 1
            return row
        # Parity row (copy — the caller mutates rows during inversion).
        return bytearray(self._parity[index - self._k])

    @staticmethod
    def _build_cauchy_parity_matrix(k: int, m: int) -> List[bytearray]:
        """Build the M x K Cauchy parity matrix over GF(256): C[i][j] = 1 / (x_i ^ y_j) with disjoint
        distinct element sets y_j = j (0..K-1) and x_i = K + i (K..K+M-1). Cauchy => every square
        submatrix invertible => MDS when stacked on the systematic identity."""
        matrix: List[bytearray] = []
        for i in range(m):
            row = bytearray(k)
            xi = (k + i) & 0xFF
            for j in range(k):
                yj = j & 0xFF
                # x_i and y_j are drawn from disjoint ranges, so x_i ^ y_j is never 0 -> always invertible.
                row[j] = _gf_inv(xi ^ yj)
            matrix.append(row)
        return matrix

    # ── GF(256) matrix inversion (Gauss-Jordan) ──────────────────────────────────

    def _invert_matrix(self, m: List[bytearray]) -> List[bytearray]:
        """Invert a K x K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack
        guarantees the picked submatrix is non-singular."""
        n = self._k
        # Augment [m | I].
        aug: List[bytearray] = []
        for r in range(n):
            row = bytearray(2 * n)
            row[:n] = m[r]
            row[n + r] = 1
            aug.append(row)

        for col in range(n):
            # Find a pivot row at or below `col` with a non-zero entry in this column.
            pivot = -1
            for r in range(col, n):
                if aug[r][col] != 0:
                    pivot = r
                    break
            if pivot < 0:
                raise ValueError("vault: singular matrix — shard set is not decodable")

            if pivot != col:
                aug[col], aug[pivot] = aug[pivot], aug[col]

            # Normalise the pivot row so the pivot element becomes 1.
            inv = _gf_inv(aug[col][col])
            row_col = aug[col]
            for cc in range(2 * n):
                row_col[cc] = _gf_mul(row_col[cc], inv)

            # Eliminate this column from every other row.
            for r in range(n):
                if r == col:
                    continue
                factor = aug[r][col]
                if factor == 0:
                    continue
                row_r = aug[r]
                for cc in range(2 * n):
                    row_r[cc] ^= _gf_mul(factor, row_col[cc])

        # Right half is the inverse.
        result: List[bytearray] = []
        for r in range(n):
            result.append(bytearray(aug[r][n:]))
        return result
