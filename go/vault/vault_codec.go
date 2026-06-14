// SPDX-License-Identifier: MIT
//
// File-level helpers over ReedSolomonCodec: split a plaintext blob into K systematic data shards
// (zero-padded), produce the full N-shard set, and reconstruct the original blob from any K
// surviving shards. Byte-identical to the C# vault data layout: shardSize = ceil(size/K), data shard
// i is plaintext[i*shardSize .. (i+1)*shardSize] zero-padded, and recovery concatenates the K
// recovered data shards in index order then trims to the original size.
package vault

import "errors"

// SplitIntoDataShards slices data into K equal zero-padded data shards of length
// shardSize = ceil(len(data)/K). This is the systematic prefix the encoder leaves unchanged.
func SplitIntoDataShards(data []byte, k int) ([][]byte, error) {
	if k < 1 {
		return nil, errors.New("vault: K must be >= 1")
	}
	if len(data) == 0 {
		return nil, errors.New("vault: data must not be empty")
	}
	shardSize := (len(data) + k - 1) / k
	shards := make([][]byte, k)
	for i := 0; i < k; i++ {
		shard := make([]byte, shardSize)
		offset := i * shardSize
		if offset < len(data) {
			length := shardSize
			if offset+length > len(data) {
				length = len(data) - offset
			}
			copy(shard, data[offset:offset+length])
		}
		shards[i] = shard
	}
	return shards, nil
}

// EncodeData splits data into K systematic data shards and returns the full set of N = K+M shards.
func (c *ReedSolomonCodec) EncodeData(data []byte) ([][]byte, error) {
	dataShards, err := SplitIntoDataShards(data, c.k)
	if err != nil {
		return nil, err
	}
	return c.Encode(dataShards)
}

// ReconstructData reconstructs the original blob of originalSize bytes from any K surviving shards.
// available maps a shard index (0…N-1) to its bytes. It returns an error if fewer than K shards
// are supplied.
func (c *ReedSolomonCodec) ReconstructData(available map[int][]byte, originalSize int) ([]byte, error) {
	dataShards, err := c.DecodeDataShards(available)
	if err != nil {
		return nil, err
	}
	if originalSize < 0 {
		return nil, errors.New("vault: originalSize must be >= 0")
	}

	shardSize := len(dataShards[0])
	out := make([]byte, c.k*shardSize)
	for j := 0; j < c.k; j++ {
		copy(out[j*shardSize:], dataShards[j])
	}
	if originalSize > len(out) {
		return nil, errors.New("vault: originalSize exceeds reconstructed length")
	}
	return out[:originalSize], nil
}
