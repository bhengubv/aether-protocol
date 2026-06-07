// SPDX-License-Identifier: MIT
export { BitsetCodec, marshalChunkBitmapJson } from "./ChunkBitmap.js";
export type { ContentDescriptor, ContentDescriptorWire } from "./ContentDescriptor.js";
export { descriptorToWire, descriptorFromWire } from "./ContentDescriptor.js";
export type {
  DirectoryEntryAnnouncedEvent,
  IDirectoryService,
} from "./IDirectoryService.js";
export type {
  NamePublishPayloadWire,
  NameQueryPayloadWire,
} from "./DirectoryService.js";
export { DirectoryService, DEFAULT_QUERY_TIMEOUT_MS } from "./DirectoryService.js";
