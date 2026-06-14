/**
 * File-level helpers over ReedSolomonCodec: split a plaintext blob into K
 * systematic data shards (zero-padded), produce the full N-shard set, and
 * reconstruct the original blob from any K surviving shards. Byte-identical to
 * the C# vault data layout: shardSize = ceil(size/K), data shard i is
 * plaintext[i*shardSize .. (i+1)*shardSize] zero-padded, and recovery
 * concatenates the K recovered data shards in index order then trims to the
 * original size.
 *
 * SPDX-License-Identifier: MIT
 */

import { ReedSolomonCodec } from "./ReedSolomonCodec.js";

/**
 * Slices `data` into K equal zero-padded data shards of length
 * shardSize = ceil(data.length/K). This is the systematic prefix the encoder
 * leaves unchanged.
 */
export function splitIntoDataShards(
  data: Uint8Array,
  k: number,
): Uint8Array[] {
  if (k < 1) {
    throw new Error("vault: K must be >= 1");
  }
  if (data.length === 0) {
    throw new Error("vault: data must not be empty");
  }
  const shardSize = Math.floor((data.length + k - 1) / k);
  const shards: Uint8Array[] = new Array(k);
  for (let i = 0; i < k; i++) {
    const shard = new Uint8Array(shardSize);
    const offset = i * shardSize;
    if (offset < data.length) {
      let length = shardSize;
      if (offset + length > data.length) {
        length = data.length - offset;
      }
      shard.set(data.subarray(offset, offset + length), 0);
    }
    shards[i] = shard;
  }
  return shards;
}

/**
 * Splits `data` into K systematic data shards and returns the full set of
 * N = K+M shards.
 */
export function encodeData(
  codec: ReedSolomonCodec,
  data: Uint8Array,
): Uint8Array[] {
  const dataShards = splitIntoDataShards(data, codec.dataShards);
  return codec.encode(dataShards);
}

/**
 * Reconstructs the original blob of `originalSize` bytes from any K surviving
 * shards. `available` maps a shard index (0…N-1) to its bytes. Throws if fewer
 * than K shards are supplied.
 */
export function reconstructData(
  codec: ReedSolomonCodec,
  available: Map<number, Uint8Array>,
  originalSize: number,
): Uint8Array {
  const dataShards = codec.decodeDataShards(available);
  if (originalSize < 0) {
    throw new Error("vault: originalSize must be >= 0");
  }

  const k = codec.dataShards;
  const shardSize = dataShards[0].length;
  const out = new Uint8Array(k * shardSize);
  for (let j = 0; j < k; j++) {
    out.set(dataShards[j], j * shardSize);
  }
  if (originalSize > out.length) {
    throw new Error("vault: originalSize exceeds reconstructed length");
  }
  return out.subarray(0, originalSize);
}
