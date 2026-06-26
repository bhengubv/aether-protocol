# SPDX-License-Identifier: MIT

"""Vault erasure-coding layer.

A systematic Cauchy-Reed-Solomon (K data + M parity) codec over GF(2^8) — the production
erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
reconstruct it"). The K data shards are the plaintext partitioned into equal zero-padded slices;
the M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
original. Byte-identical to the C# ``AetherNet.Vault.ReedSolomonCodec`` and every other language port.
"""

from __future__ import annotations

from aethernet.vault.reed_solomon import (
    ReedSolomonCodec,
    split_into_data_shards,
)
from aethernet.vault.service import (
    VAULT_K,
    VAULT_M,
    InMemoryVaultService,
    IVaultService,
    VaultHealth,
    VaultManifest,
)

__all__ = [
    "ReedSolomonCodec",
    "split_into_data_shards",
    "InMemoryVaultService",
    "IVaultService",
    "VaultManifest",
    "VaultHealth",
    "VAULT_K",
    "VAULT_M",
]
