/*
 * Vault layer — systematic Cauchy-Reed-Solomon (K=10, M=4) erasure coding over
 * GF(2⁸) (primitive polynomial 0x11D, α=2).
 * SPDX-License-Identifier: MIT
 */

export { ReedSolomonCodec } from './ReedSolomonCodec';
export { splitIntoDataShards, encodeData, reconstructData } from './VaultCodec';
