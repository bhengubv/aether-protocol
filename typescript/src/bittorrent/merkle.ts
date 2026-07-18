// SPDX-License-Identifier: MIT
// BitTorrent v2 (BEP-52) SHA-256 merkle hashing + v2 info-hash.
import { createHash } from 'node:crypto';

export const MERKLE_BLOCK_SIZE = 16384;

function sha256(b: Buffer): Buffer {
  return createHash('sha256').update(b).digest();
}

export function merkleRoot(data: Buffer, blockSize = MERKLE_BLOCK_SIZE): Buffer {
  if (blockSize <= 0) throw new Error('block size must be positive');
  const leaves: Buffer[] = [];
  for (let i = 0; i < data.length; i += blockSize) {
    leaves.push(sha256(Buffer.from(data.subarray(i, Math.min(i + blockSize, data.length)))));
  }
  if (leaves.length === 0) return Buffer.alloc(32);
  return rootOf(leaves);
}

function rootOf(leafHashes: Buffer[]): Buffer {
  let level = leafHashes.slice();
  let width = 1;
  while (width < level.length) width <<= 1;
  const zero = Buffer.alloc(32);
  while (level.length < width) level.push(zero);
  while (level.length > 1) {
    const next: Buffer[] = [];
    for (let i = 0; i < level.length; i += 2) next.push(sha256(Buffer.concat([level[i], level[i + 1]])));
    level = next;
  }
  return level[0];
}

export function v2InfoHash(infoDictBytes: Buffer): Buffer {
  return sha256(infoDictBytes);
}

export function v2InfoHashTruncated(infoDictBytes: Buffer): Buffer {
  return v2InfoHash(infoDictBytes).subarray(0, 20);
}
