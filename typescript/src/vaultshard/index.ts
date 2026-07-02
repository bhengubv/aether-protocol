/**
 * aether-vault shard-request WIRE binding (PacketType.VaultShardRequest = 42).
 * SPDX-License-Identifier: MIT
 */

export {
  VaultShardRequestService,
  serializeVaultShardRequestPayload,
  deserializeVaultShardRequestPayload,
} from "./VaultShardRequestService.js";
export type {
  VaultShardRequest,
  VaultShardRequestPayload,
} from "./VaultShardRequestService.js";
