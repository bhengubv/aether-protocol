// SPDX-License-Identifier: MIT
/**
 * Manifest for a piece of chunked content. Identifies the content by a root
 * hash computed over the per-chunk hashes, declares the chunk layout, and
 * lets receivers verify each chunk independently as it arrives.
 *
 * Wire shape: JSON, snake_case property names — cross-language stable. The
 * TypeScript API exposes camelCase fields (TS convention); the wire encoder
 * in DirectoryService maps to snake_case before transmission. Producers can
 * publish a descriptor once and any node can pull chunks and verify against
 * it without trusting the sender — content addressing makes the descriptor
 * itself the authority.
 *
 * Added in v1.2.0 — closes Issue #60.
 */
export interface ContentDescriptor {
  /** SHA-256 over the concatenation of all chunk hashes, in order. Hex-encoded lowercase. */
  rootHash: string;
  /** Original file name as the publisher named it. Hint only — never used as a path on the receiver. */
  name: string;
  /** Total size of the original content in bytes. */
  totalBytes: number;
  /** Bytes per chunk for every chunk except possibly the last. */
  chunkSizeBytes: number;
  /** Total number of chunks. Equal to ceil(totalBytes / chunkSizeBytes). */
  chunkCount: number;
  /** SHA-256 of each chunk's bytes, in chunk-index order. Hex-encoded lowercase. */
  chunkHashes: readonly string[];
  /** Caller-defined MIME type or media kind. Opaque to the protocol. */
  contentType: string;
  /** UTC ISO-8601 creation time of the descriptor. */
  createdAt: string;
}

/**
 * Wire shape of a {@link ContentDescriptor}: snake_case JSON keys for
 * cross-language interop. Exported so the directory service and any host
 * that needs to round-trip a descriptor through transport can share the
 * mapping.
 */
export interface ContentDescriptorWire {
  root_hash: string;
  name: string;
  total_bytes: number;
  chunk_size_bytes: number;
  chunk_count: number;
  chunk_hashes: string[];
  content_type: string;
  created_at: string;
}

/** Map a TS-idiom camelCase descriptor to the snake_case wire shape. */
export function descriptorToWire(d: ContentDescriptor): ContentDescriptorWire {
  return {
    root_hash: d.rootHash,
    name: d.name,
    total_bytes: d.totalBytes,
    chunk_size_bytes: d.chunkSizeBytes,
    chunk_count: d.chunkCount,
    chunk_hashes: [...d.chunkHashes],
    content_type: d.contentType,
    created_at: d.createdAt,
  };
}

/** Parse a snake_case wire descriptor into the camelCase TS shape. */
export function descriptorFromWire(w: ContentDescriptorWire): ContentDescriptor {
  return {
    rootHash: w.root_hash,
    name: w.name,
    totalBytes: w.total_bytes,
    chunkSizeBytes: w.chunk_size_bytes,
    chunkCount: w.chunk_count,
    chunkHashes: w.chunk_hashes,
    contentType: w.content_type,
    createdAt: w.created_at,
  };
}
