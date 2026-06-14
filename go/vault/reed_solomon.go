// SPDX-License-Identifier: MIT
//
// Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault — the production
// erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
// reconstruct it"). Go port of AetherNet.Vault.ReedSolomonCodec, byte-identical to the C# reference
// and every other language implementation.
//
// FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D, the AES/Rijndael
// polynomial), α = 2 — the SAME field as transport/rlnc.go (RlncCodec). Identical field ⇒ identical
// parity bytes, which is what makes a parity shard scattered by one node decodable by any other node
// on the mesh.
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
//
//	plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short, shardSize = ceil(size/K)
//
// The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
// original.
//
// MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ⊕ y_j) over GF(256) with two
// disjoint sets of distinct field elements (y_0…y_{K-1} = 0…K-1, x_0…x_{M-1} = K…K+M-1). Every square
// submatrix of a Cauchy matrix is invertible, so stacked on the systematic K×K identity it yields a
// true MDS code: any K of the N generator rows are linearly independent ⇒ any K surviving shards
// reconstruct the original; K-1 or fewer is unrecoverable.
package vault

import (
	"errors"
	"sort"
)

// ── GF(2⁸) arithmetic — primitive polynomial 0x11D, α = 2 ──────────────────────
// Byte-for-byte identical field to transport/rlnc.go's GF256 (that package's tables are
// unexported, so the table-generation logic is mirrored here rather than referenced across the
// package boundary — identical tables are what guarantee identical parity bytes).

var (
	gfExp [512]byte // gfExp[i] = α^i; doubled to avoid modular wrap in Mul
	gfLog [256]byte // gfLog[v] = log_α(v) for v ∈ [1, 255]
)

func init() {
	x := 1
	for i := 0; i < 255; i++ {
		gfExp[i] = byte(x)
		gfLog[x] = byte(i)
		x <<= 1
		if x&0x100 != 0 {
			x ^= 0x11D // reduce mod p(x) = x⁸+x⁴+x³+x²+1
		}
	}
	for i := 255; i < 512; i++ {
		gfExp[i] = gfExp[i-255]
	}
	gfLog[1] = 0
}

func gfMul(a, b byte) byte {
	if a == 0 || b == 0 {
		return 0
	}
	return gfExp[int(gfLog[a])+int(gfLog[b])]
}

func gfInv(a byte) byte {
	if a == 0 {
		panic("vault: GF256 inverse of zero")
	}
	return gfExp[255-int(gfLog[a])]
}

// ReedSolomonCodec is a systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). The K
// data shards are the plaintext partitioned into equal zero-padded slices (byte-identical to the
// vault data layout); the M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any
// K of the K+M shards reconstruct the original.
type ReedSolomonCodec struct {
	k int
	m int
	n int
	// parity holds the generator rows: parity[i] (i = 0…M-1) is the K-byte Cauchy coefficient
	// vector for parity shard K+i. Together with the implicit K×K systematic identity for the data
	// shards these form the full N×K MDS generator matrix.
	parity [][]byte
}

// NewReedSolomonCodec creates a codec with k data shards and m parity shards.
// k must be ≥ 1, m must be ≥ 0, and k+m must be ≤ 256.
func NewReedSolomonCodec(k, m int) (*ReedSolomonCodec, error) {
	if k < 1 {
		return nil, errors.New("vault: K must be >= 1")
	}
	if m < 0 {
		return nil, errors.New("vault: M must be >= 0")
	}
	if k+m > 256 {
		return nil, errors.New("vault: K + M must be <= 256")
	}
	return &ReedSolomonCodec{
		k:      k,
		m:      m,
		n:      k + m,
		parity: buildCauchyParityMatrix(k, m),
	}, nil
}

// ShardCount returns the total shard count (K + M).
func (c *ReedSolomonCodec) ShardCount() int { return c.n }

// DataShards returns K.
func (c *ReedSolomonCodec) DataShards() int { return c.k }

// ParityShards returns M.
func (c *ReedSolomonCodec) ParityShards() int { return c.m }

// Encode encodes dataShards (K byte slices of equal length shardSize) into the full set of N shards.
// Shards 0…K-1 are the data shards unchanged (systematic); shards K…N-1 are the M Reed-Solomon
// parity shards. The returned shards are fresh copies — callers keep ownership of the input.
func (c *ReedSolomonCodec) Encode(dataShards [][]byte) ([][]byte, error) {
	if dataShards == nil {
		return nil, errors.New("vault: dataShards must not be nil")
	}
	if len(dataShards) != c.k {
		return nil, errors.New("vault: wrong number of data shards")
	}

	shardSize := len(dataShards[0])
	for j := 0; j < c.k; j++ {
		if dataShards[j] == nil || len(dataShards[j]) != shardSize {
			return nil, errors.New("vault: all data shards must be non-nil and the same length")
		}
	}

	shards := make([][]byte, c.n)

	// Systematic: the first K shards ARE the data shards (defensive copy — callers keep ownership).
	for j := 0; j < c.k; j++ {
		clone := make([]byte, shardSize)
		copy(clone, dataShards[j])
		shards[j] = clone
	}

	// Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
	for i := 0; i < c.m; i++ {
		parityShard := make([]byte, shardSize)
		coeffs := c.parity[i]
		for j := 0; j < c.k; j++ {
			coeff := coeffs[j]
			if coeff == 0 {
				continue
			}
			src := dataShards[j]
			for b := 0; b < shardSize; b++ {
				parityShard[b] ^= gfMul(coeff, src[b])
			}
		}
		shards[c.k+i] = parityShard
	}

	return shards, nil
}

// DecodeDataShards reconstructs the K data shards from any K available shards. The available map maps
// a shard index (0…N-1) to its bytes; exactly K distinct entries are required, all of equal length.
// It returns the K data shards (indices 0…K-1, in order). It returns an error if fewer than K shards
// are supplied (K-1 or fewer is unrecoverable).
func (c *ReedSolomonCodec) DecodeDataShards(available map[int][]byte) ([][]byte, error) {
	if available == nil {
		return nil, errors.New("vault: available must not be nil")
	}

	// Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
	indices := make([]int, 0, len(available))
	for idx, val := range available {
		if idx >= 0 && idx < c.n && val != nil {
			indices = append(indices, idx)
		}
	}
	sort.Ints(indices)
	if len(indices) > c.k {
		indices = indices[:c.k]
	}

	if len(indices) < c.k {
		return nil, errors.New("vault: cannot decode — fewer than K shards available")
	}

	shardSize := len(available[indices[0]])
	for _, idx := range indices {
		if len(available[idx]) != shardSize {
			return nil, errors.New("vault: all supplied shards must be the same length")
		}
	}

	// Fast path: if all K data shards (0…K-1) are present, no inversion is needed — the data is the
	// systematic prefix verbatim. This is the common, byte-identical-to-canonical recovery case.
	allData := true
	for _, idx := range indices {
		if idx >= c.k {
			allData = false
			break
		}
	}
	if allData {
		direct := make([][]byte, c.k)
		for _, idx := range indices {
			clone := make([]byte, shardSize)
			copy(clone, available[idx])
			direct[idx] = clone
		}
		return direct, nil
	}

	// General path: build the K×K generator submatrix A for the picked shard indices, invert it,
	// and apply A⁻¹ to the picked symbol-vectors to recover the K source (data) symbols.
	a := make([][]byte, c.k)
	rhs := make([][]byte, c.k)
	for r := 0; r < c.k; r++ {
		idx := indices[r]
		a[r] = c.generatorRow(idx)
		clone := make([]byte, shardSize)
		copy(clone, available[idx])
		rhs[r] = clone
	}

	inv, err := c.invertMatrix(a)
	if err != nil {
		return nil, err
	}

	data := make([][]byte, c.k)
	for r := 0; r < c.k; r++ {
		symbol := make([]byte, shardSize)
		for col := 0; col < c.k; col++ {
			coeff := inv[r][col]
			if coeff == 0 {
				continue
			}
			src := rhs[col]
			for b := 0; b < shardSize; b++ {
				symbol[b] ^= gfMul(coeff, src[b])
			}
		}
		data[r] = symbol
	}

	return data, nil
}

// ── generator matrix ────────────────────────────────────────────────────────

// generatorRow returns the K-byte generator row for shard index (identity for a data shard, Cauchy
// coefficients for a parity shard).
func (c *ReedSolomonCodec) generatorRow(index int) []byte {
	if index < c.k {
		// Systematic data row = standard basis vector e_index.
		row := make([]byte, c.k)
		row[index] = 1
		return row
	}
	// Parity row (copy — the caller mutates rows during inversion).
	row := make([]byte, c.k)
	copy(row, c.parity[index-c.k])
	return row
}

// buildCauchyParityMatrix builds the M×K Cauchy parity matrix over GF(256): C[i][j] = 1 / (x_i ⊕ y_j)
// with disjoint distinct element sets y_j = j (0…K-1) and x_i = K + i (K…K+M-1). Cauchy ⇒ every square
// submatrix invertible ⇒ MDS when stacked on the systematic identity.
func buildCauchyParityMatrix(k, m int) [][]byte {
	matrix := make([][]byte, m)
	for i := 0; i < m; i++ {
		row := make([]byte, k)
		xi := byte(k + i)
		for j := 0; j < k; j++ {
			yj := byte(j)
			// x_i and y_j are drawn from disjoint ranges, so x_i ⊕ y_j is never 0 → always invertible.
			row[j] = gfInv(xi ^ yj)
		}
		matrix[i] = row
	}
	return matrix
}

// ── GF(256) matrix inversion (Gauss-Jordan) ──────────────────────────────────

// invertMatrix inverts a K×K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack
// guarantees the picked submatrix is non-singular.
func (c *ReedSolomonCodec) invertMatrix(m [][]byte) ([][]byte, error) {
	n := c.k
	// Augment [m | I].
	aug := make([][]byte, n)
	for r := 0; r < n; r++ {
		aug[r] = make([]byte, 2*n)
		copy(aug[r][:n], m[r])
		aug[r][n+r] = 1
	}

	for col := 0; col < n; col++ {
		// Find a pivot row at or below `col` with a non-zero entry in this column.
		pivot := -1
		for r := col; r < n; r++ {
			if aug[r][col] != 0 {
				pivot = r
				break
			}
		}
		if pivot < 0 {
			return nil, errors.New("vault: singular matrix — shard set is not decodable")
		}

		if pivot != col {
			aug[col], aug[pivot] = aug[pivot], aug[col]
		}

		// Normalise the pivot row so the pivot element becomes 1.
		inv := gfInv(aug[col][col])
		for cc := 0; cc < 2*n; cc++ {
			aug[col][cc] = gfMul(aug[col][cc], inv)
		}

		// Eliminate this column from every other row.
		for r := 0; r < n; r++ {
			if r == col {
				continue
			}
			factor := aug[r][col]
			if factor == 0 {
				continue
			}
			for cc := 0; cc < 2*n; cc++ {
				aug[r][cc] ^= gfMul(factor, aug[col][cc])
			}
		}
	}

	// Right half is the inverse.
	result := make([][]byte, n)
	for r := 0; r < n; r++ {
		result[r] = make([]byte, n)
		copy(result[r], aug[r][n:])
	}
	return result, nil
}
