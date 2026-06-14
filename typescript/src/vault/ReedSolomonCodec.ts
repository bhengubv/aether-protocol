/**
 * Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault —
 * the production erasure-coding promised by the vault contract ("a file is split
 * into K+M shards; any K shards reconstruct it"). TypeScript port of
 * AetherNet.Vault.ReedSolomonCodec, byte-identical to the C# reference and every
 * other language implementation (Go, etc.).
 *
 * FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1
 * (0x11D, the AES/Rijndael polynomial), α = 2 — the SAME field as the RLNC codec.
 * Identical field ⇒ identical parity bytes, which is what makes a parity shard
 * scattered by one node decodable by any other node on the mesh.
 *
 * SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
 *   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short,
 *   shardSize = ceil(size/K)
 * The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N
 * shards reconstruct the original.
 *
 * MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ⊕ y_j)
 * over GF(256) with two disjoint sets of distinct field elements
 * (y_0…y_{K-1} = 0…K-1, x_0…x_{M-1} = K…K+M-1). Every square submatrix of a
 * Cauchy matrix is invertible, so stacked on the systematic K×K identity it
 * yields a true MDS code: any K of the N generator rows are linearly independent
 * ⇒ any K surviving shards reconstruct the original; K-1 or fewer is
 * unrecoverable.
 *
 * SPDX-License-Identifier: MIT
 */

// ── GF(2⁸) arithmetic — primitive polynomial 0x11D, α = 2 ──────────────────────
// Byte-for-byte identical field to the RLNC GF256 (those tables are module-private,
// so the table-generation logic is mirrored here rather than referenced across the
// module boundary — identical tables are what guarantee identical parity bytes).

const GF_EXP = new Uint8Array(512); // GF_EXP[i] = α^i; doubled to avoid modular wrap in mul
const GF_LOG = new Uint8Array(256); // GF_LOG[v] = log_α(v) for v ∈ [1, 255]

(function initGfTables() {
  let x = 1;
  for (let i = 0; i < 255; i++) {
    GF_EXP[i] = x;
    GF_LOG[x] = i;
    x <<= 1;
    if (x & 0x100) {
      x ^= 0x11d; // reduce mod p(x) = x⁸+x⁴+x³+x²+1
    }
  }
  for (let i = 255; i < 512; i++) {
    GF_EXP[i] = GF_EXP[i - 255];
  }
  GF_LOG[1] = 0;
})();

function gfMul(a: number, b: number): number {
  if (a === 0 || b === 0) {
    return 0;
  }
  return GF_EXP[GF_LOG[a] + GF_LOG[b]];
}

function gfInv(a: number): number {
  if (a === 0) {
    throw new Error("vault: GF256 inverse of zero");
  }
  return GF_EXP[255 - GF_LOG[a]];
}

/**
 * A systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). The K
 * data shards are the plaintext partitioned into equal zero-padded slices
 * (byte-identical to the vault data layout); the M parity shards are
 * Cauchy-Reed-Solomon over the canonical 0x11D field. Any K of the K+M shards
 * reconstruct the original.
 */
export class ReedSolomonCodec {
  private readonly k: number;
  private readonly m: number;
  private readonly n: number;
  /**
   * The generator rows: parity[i] (i = 0…M-1) is the K-byte Cauchy coefficient
   * vector for parity shard K+i. Together with the implicit K×K systematic
   * identity for the data shards these form the full N×K MDS generator matrix.
   */
  private readonly parity: Uint8Array[];

  /**
   * Creates a codec with `k` data shards and `m` parity shards. `k` must be ≥ 1,
   * `m` must be ≥ 0, and `k+m` must be ≤ 256.
   */
  constructor(k: number, m: number) {
    if (k < 1) {
      throw new Error("vault: K must be >= 1");
    }
    if (m < 0) {
      throw new Error("vault: M must be >= 0");
    }
    if (k + m > 256) {
      throw new Error("vault: K + M must be <= 256");
    }
    this.k = k;
    this.m = m;
    this.n = k + m;
    this.parity = buildCauchyParityMatrix(k, m);
  }

  /** Total shard count (K + M). */
  get shardCount(): number {
    return this.n;
  }

  /** K. */
  get dataShards(): number {
    return this.k;
  }

  /** M. */
  get parityShards(): number {
    return this.m;
  }

  /**
   * Encodes `dataShards` (K byte arrays of equal length shardSize) into the full
   * set of N shards. Shards 0…K-1 are the data shards unchanged (systematic);
   * shards K…N-1 are the M Reed-Solomon parity shards. The returned shards are
   * fresh copies — callers keep ownership of the input.
   */
  encode(dataShards: Uint8Array[]): Uint8Array[] {
    if (!dataShards) {
      throw new Error("vault: dataShards must not be null");
    }
    if (dataShards.length !== this.k) {
      throw new Error("vault: wrong number of data shards");
    }

    const shardSize = dataShards[0].length;
    for (let j = 0; j < this.k; j++) {
      if (!dataShards[j] || dataShards[j].length !== shardSize) {
        throw new Error(
          "vault: all data shards must be non-null and the same length",
        );
      }
    }

    const shards: Uint8Array[] = new Array(this.n);

    // Systematic: the first K shards ARE the data shards (defensive copy).
    for (let j = 0; j < this.k; j++) {
      shards[j] = dataShards[j].slice();
    }

    // Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
    for (let i = 0; i < this.m; i++) {
      const parityShard = new Uint8Array(shardSize);
      const coeffs = this.parity[i];
      for (let j = 0; j < this.k; j++) {
        const coeff = coeffs[j];
        if (coeff === 0) {
          continue;
        }
        const src = dataShards[j];
        for (let b = 0; b < shardSize; b++) {
          parityShard[b] ^= gfMul(coeff, src[b]);
        }
      }
      shards[this.k + i] = parityShard;
    }

    return shards;
  }

  /**
   * Reconstructs the K data shards from any K available shards. `available` maps
   * a shard index (0…N-1) to its bytes; exactly K distinct entries are required,
   * all of equal length. Returns the K data shards (indices 0…K-1, in order).
   * Throws if fewer than K shards are supplied (K-1 or fewer is unrecoverable).
   */
  decodeDataShards(available: Map<number, Uint8Array>): Uint8Array[] {
    if (!available) {
      throw new Error("vault: available must not be null");
    }

    // Take the K lowest-indexed available shards (deterministic; any K suffice
    // for an MDS code).
    const indices: number[] = [];
    for (const [idx, val] of available) {
      if (idx >= 0 && idx < this.n && val) {
        indices.push(idx);
      }
    }
    indices.sort((a, b) => a - b);
    if (indices.length > this.k) {
      indices.length = this.k;
    }

    if (indices.length < this.k) {
      throw new Error(
        "vault: cannot decode — fewer than K shards available",
      );
    }

    const shardSize = available.get(indices[0])!.length;
    for (const idx of indices) {
      if (available.get(idx)!.length !== shardSize) {
        throw new Error("vault: all supplied shards must be the same length");
      }
    }

    // Fast path: if all K data shards (0…K-1) are present, no inversion is needed
    // — the data is the systematic prefix verbatim. This is the common,
    // byte-identical-to-canonical recovery case.
    let allData = true;
    for (const idx of indices) {
      if (idx >= this.k) {
        allData = false;
        break;
      }
    }
    if (allData) {
      const direct: Uint8Array[] = new Array(this.k);
      for (const idx of indices) {
        direct[idx] = available.get(idx)!.slice();
      }
      return direct;
    }

    // General path: build the K×K generator submatrix A for the picked shard
    // indices, invert it, and apply A⁻¹ to the picked symbol-vectors to recover
    // the K source (data) symbols.
    const a: Uint8Array[] = new Array(this.k);
    const rhs: Uint8Array[] = new Array(this.k);
    for (let r = 0; r < this.k; r++) {
      const idx = indices[r];
      a[r] = this.generatorRow(idx);
      rhs[r] = available.get(idx)!.slice();
    }

    const inv = this.invertMatrix(a);

    const data: Uint8Array[] = new Array(this.k);
    for (let r = 0; r < this.k; r++) {
      const symbol = new Uint8Array(shardSize);
      for (let col = 0; col < this.k; col++) {
        const coeff = inv[r][col];
        if (coeff === 0) {
          continue;
        }
        const src = rhs[col];
        for (let b = 0; b < shardSize; b++) {
          symbol[b] ^= gfMul(coeff, src[b]);
        }
      }
      data[r] = symbol;
    }

    return data;
  }

  // ── generator matrix ────────────────────────────────────────────────────────

  /**
   * Returns the K-byte generator row for `index` (identity for a data shard,
   * Cauchy coefficients for a parity shard).
   */
  private generatorRow(index: number): Uint8Array {
    if (index < this.k) {
      // Systematic data row = standard basis vector e_index.
      const row = new Uint8Array(this.k);
      row[index] = 1;
      return row;
    }
    // Parity row (copy — the caller mutates rows during inversion).
    return this.parity[index - this.k].slice();
  }

  // ── GF(256) matrix inversion (Gauss-Jordan) ──────────────────────────────────

  /**
   * Inverts a K×K GF(256) matrix via Gauss-Jordan elimination. The
   * Cauchy/identity stack guarantees the picked submatrix is non-singular.
   */
  private invertMatrix(m: Uint8Array[]): Uint8Array[] {
    const n = this.k;
    // Augment [m | I].
    const aug: Uint8Array[] = new Array(n);
    for (let r = 0; r < n; r++) {
      aug[r] = new Uint8Array(2 * n);
      aug[r].set(m[r].subarray(0, n), 0);
      aug[r][n + r] = 1;
    }

    for (let col = 0; col < n; col++) {
      // Find a pivot row at or below `col` with a non-zero entry in this column.
      let pivot = -1;
      for (let r = col; r < n; r++) {
        if (aug[r][col] !== 0) {
          pivot = r;
          break;
        }
      }
      if (pivot < 0) {
        throw new Error(
          "vault: singular matrix — shard set is not decodable",
        );
      }

      if (pivot !== col) {
        const tmp = aug[col];
        aug[col] = aug[pivot];
        aug[pivot] = tmp;
      }

      // Normalise the pivot row so the pivot element becomes 1.
      const inv = gfInv(aug[col][col]);
      for (let cc = 0; cc < 2 * n; cc++) {
        aug[col][cc] = gfMul(aug[col][cc], inv);
      }

      // Eliminate this column from every other row.
      for (let r = 0; r < n; r++) {
        if (r === col) {
          continue;
        }
        const factor = aug[r][col];
        if (factor === 0) {
          continue;
        }
        for (let cc = 0; cc < 2 * n; cc++) {
          aug[r][cc] ^= gfMul(factor, aug[col][cc]);
        }
      }
    }

    // Right half is the inverse.
    const result: Uint8Array[] = new Array(n);
    for (let r = 0; r < n; r++) {
      result[r] = aug[r].slice(n, 2 * n);
    }
    return result;
  }
}

/**
 * Builds the M×K Cauchy parity matrix over GF(256): C[i][j] = 1 / (x_i ⊕ y_j)
 * with disjoint distinct element sets y_j = j (0…K-1) and x_i = K + i (K…K+M-1).
 * Cauchy ⇒ every square submatrix invertible ⇒ MDS when stacked on the
 * systematic identity.
 */
function buildCauchyParityMatrix(k: number, m: number): Uint8Array[] {
  const matrix: Uint8Array[] = new Array(m);
  for (let i = 0; i < m; i++) {
    const row = new Uint8Array(k);
    const xi = (k + i) & 0xff;
    for (let j = 0; j < k; j++) {
      const yj = j & 0xff;
      // x_i and y_j are drawn from disjoint ranges, so x_i ⊕ y_j is never 0 →
      // always invertible.
      row[j] = gfInv(xi ^ yj);
    }
    matrix[i] = row;
  }
  return matrix;
}
