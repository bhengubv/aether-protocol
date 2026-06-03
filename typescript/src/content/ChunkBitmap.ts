// SPDX-License-Identifier: MIT
/**
 * ChunkBitmap wire-format codec for the Aether Chunk Shuffle / SAPI protocol.
 *
 * Wire format:
 *   • JSON, snake_case property names.
 *   • Bitset: LSB-first within each byte — bit i is set in byte (i>>3), at
 *     position (i&7). Length = ceil(chunk_count / 8).
 *   • Bitset transmitted as standard Base64 (with padding).
 *   • Field order in canonical JSON: root_hash, chunk_count, have_bitset,
 *     generation.
 */

export class BitsetCodec {
  /**
   * Encode indices into an LSB-first compact bitset.
   * Returns a Uint8Array of length ceil(chunkCount / 8).
   */
  static encode(chunkCount: number, haveIndices: readonly number[]): Uint8Array {
    if (chunkCount <= 0) return new Uint8Array(0);
    const bytes = new Uint8Array(Math.ceil(chunkCount / 8));
    for (const i of haveIndices) {
      if (i < 0 || i >= chunkCount)
        throw new RangeError(`Index ${i} out of range [0, ${chunkCount})`);
      bytes[i >> 3] |= 1 << (i & 7);
    }
    return bytes;
  }

  /** Decode a compact bitset back to sorted chunk indices. */
  static decode(bitset: Uint8Array, chunkCount: number): number[] {
    const result: number[] = [];
    const limit = Math.min(chunkCount, bitset.length * 8);
    for (let i = 0; i < limit; i++) {
      if (bitset[i >> 3] & (1 << (i & 7))) result.push(i);
    }
    return result;
  }
}

/**
 * Produce the canonical wire JSON for a ChunkBitmapPayload.
 * Field order is fixed: root_hash → chunk_count → have_bitset → generation.
 */
export function marshalChunkBitmapJson(
  rootHash: string,
  chunkCount: number,
  haveBitset: Uint8Array,
  generation: number,
): string {
  const b64 = Buffer.from(haveBitset).toString("base64");
  return (
    `{"root_hash":${JSON.stringify(rootHash)}` +
    `,"chunk_count":${chunkCount}` +
    `,"have_bitset":${JSON.stringify(b64)}` +
    `,"generation":${generation}}`
  );
}
