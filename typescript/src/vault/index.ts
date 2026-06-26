/**
 * Vault layer — systematic Cauchy-Reed-Solomon (K, N) erasure coding over
 * GF(2⁸) (0x11D, α=2).
 * SPDX-License-Identifier: MIT
 */

export { ReedSolomonCodec } from "./ReedSolomonCodec.js";
export {
  splitIntoDataShards,
  encodeData,
  reconstructData,
} from "./VaultCodec.js";
export {
  InMemoryVaultService,
  vaultTotalShards,
} from "./VaultService.js";
export type {
  IVaultService,
  VaultManifest,
  VaultHealth,
} from "./VaultService.js";
