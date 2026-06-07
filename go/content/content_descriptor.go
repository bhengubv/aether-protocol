// SPDX-License-Identifier: MIT

package content

import (
	"time"
)

// ContentDescriptor is the cross-language stable manifest for a piece of
// chunked content. Identifies the content by a root hash computed over the
// per-chunk hashes, declares the chunk layout, and lets receivers verify each
// chunk independently as it arrives.
//
// Wire shape (JSON, snake_case) — must match the C# AetherNet.Content.Models.ContentDescriptor
// for cross-language interop. Tags are explicit so existing producers and
// consumers across language ports stay byte-equal.
type ContentDescriptor struct {
	// RootHash is the SHA-256 over the concatenation of all chunk hashes, in
	// order. Hex-encoded lowercase.
	RootHash string `json:"root_hash"`

	// Name is the original file name as the publisher named it. Hint only —
	// never used as a path on the receiver.
	Name string `json:"name"`

	// TotalBytes is the total size of the original content in bytes.
	TotalBytes int64 `json:"total_bytes"`

	// ChunkSizeBytes is the bytes per chunk for every chunk except possibly
	// the last.
	ChunkSizeBytes int32 `json:"chunk_size_bytes"`

	// ChunkCount is the total number of chunks. Equal to
	// ceil(TotalBytes / ChunkSizeBytes).
	ChunkCount int32 `json:"chunk_count"`

	// ChunkHashes is the SHA-256 of each chunk's bytes, in chunk-index order.
	// Hex-encoded lowercase.
	ChunkHashes []string `json:"chunk_hashes"`

	// ContentType is the caller-defined MIME type or media kind. Opaque to
	// the protocol.
	ContentType string `json:"content_type"`

	// CreatedAt is the UTC creation time of the descriptor.
	CreatedAt time.Time `json:"created_at"`
}
