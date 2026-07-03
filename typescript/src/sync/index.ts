/**
 * Decentralised multi-device sync (no server): SyncRecord binary envelope,
 * deterministic last-write-wins reconciliation, and signed DeviceLink records.
 * SPDX-License-Identifier: MIT
 */

export {
  SyncOp,
  SYNC_RECORD_VERSION,
  serializeSyncRecord,
  deserializeSyncRecord,
  uuidToBytes,
  bytesToUuid,
} from "./SyncRecord.js";
export type { SyncRecord } from "./SyncRecord.js";

export {
  compareSyncRecords,
  winner,
  merge,
} from "./SyncReconciler.js";

export {
  DEVICE_LINK_VERSION,
  signedBody,
  createDeviceLink,
  verifyDeviceLink,
  serializeDeviceLink,
  deserializeDeviceLink,
} from "./DeviceLink.js";
export type { DeviceLink } from "./DeviceLink.js";
