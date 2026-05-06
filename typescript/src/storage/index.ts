/**
 * Storage primitives for the Aether protocol — KV-store interface,
 * reference implementations, and the encryption-at-rest wrapper.
 *
 * SPDX-License-Identifier: MIT
 */
export type { KeyValueStore } from "./KeyValueStore.js";
export { InMemoryKeyValueStore } from "./InMemoryKeyValueStore.js";
export { FileSystemKeyValueStore } from "./FileSystemKeyValueStore.js";
export {
  EncryptedKeyValueStore,
  ENCRYPTED_KV_KEY_SIZE,
  ENCRYPTED_KV_NONCE_SIZE,
  ENCRYPTED_KV_TAG_SIZE,
  ENCRYPTED_KV_VERSION_HEADER_SIZE,
  ENCRYPTED_KV_MIN_BLOB_SIZE,
} from "./EncryptedKeyValueStore.js";
export type { DataAtRestKeyProvider } from "./DataAtRestKeyProvider.js";
export { StaticDataAtRestKeyProvider } from "./DataAtRestKeyProvider.js";
export {
  DerivedDataAtRestKeyProvider,
  DEFAULT_DERIVED_KEY_COST,
} from "./DerivedDataAtRestKeyProvider.js";
