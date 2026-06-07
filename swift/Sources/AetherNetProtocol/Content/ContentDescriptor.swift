// SPDX-License-Identifier: MIT

import Foundation
import Crypto

/// Manifest for a piece of chunked content. Identifies the content by a root hash
/// computed over the per-chunk hashes, declares the chunk layout, and lets
/// receivers verify each chunk independently as it arrives.
///
/// Wire shape (JSON, snake_case): cross-language stable. Producers can publish a
/// descriptor once and any node can pull chunks and verify against it without
/// trusting the sender — content addressing makes the descriptor itself the
/// authority.
///
/// Added in v1.2.0 alongside ``DirectoryService`` — see ``IDirectoryService``.
public struct ContentDescriptor: Equatable, Codable, Sendable {
    /// SHA-256 over the concatenation of all chunk hashes, in order. Hex-encoded lowercase.
    public var rootHash: String

    /// Original file name as the publisher named it. Hint only — never used as a path on the receiver.
    public var name: String

    /// Total size of the original content in bytes.
    public var totalBytes: Int64

    /// Bytes per chunk for every chunk except possibly the last.
    public var chunkSizeBytes: Int

    /// Total number of chunks. Equal to ceil(``totalBytes`` / ``chunkSizeBytes``).
    public var chunkCount: Int

    /// SHA-256 of each chunk's bytes, in chunk-index order. Hex-encoded lowercase.
    public var chunkHashes: [String]

    /// Caller-defined MIME type or media kind. Opaque to the protocol.
    public var contentType: String

    /// UTC creation time of the descriptor (encoded on the wire as Unix ms).
    public var createdAt: Date

    public init(
        rootHash: String = "",
        name: String = "",
        totalBytes: Int64 = 0,
        chunkSizeBytes: Int = ProtocolConstants.defaultChunkSizeBytes,
        chunkCount: Int = 0,
        chunkHashes: [String] = [],
        contentType: String = "application/octet-stream",
        createdAt: Date = Date()
    ) {
        self.rootHash = rootHash
        self.name = name
        self.totalBytes = totalBytes
        self.chunkSizeBytes = chunkSizeBytes
        self.chunkCount = chunkCount
        self.chunkHashes = chunkHashes
        self.contentType = contentType
        self.createdAt = createdAt
    }

    // ─── Cross-language stable snake_case JSON ──────────────────────────────
    // Field order on the wire matches the C#/Go/Rust/Python/TS/Kotlin/C implementations.

    private enum CodingKeys: String, CodingKey {
        case rootHash = "root_hash"
        case name
        case totalBytes = "total_bytes"
        case chunkSizeBytes = "chunk_size_bytes"
        case chunkCount = "chunk_count"
        case chunkHashes = "chunk_hashes"
        case contentType = "content_type"
        case createdAt = "created_at"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.rootHash = try c.decodeIfPresent(String.self, forKey: .rootHash) ?? ""
        self.name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        self.totalBytes = try c.decodeIfPresent(Int64.self, forKey: .totalBytes) ?? 0
        self.chunkSizeBytes = try c.decodeIfPresent(Int.self, forKey: .chunkSizeBytes) ?? ProtocolConstants.defaultChunkSizeBytes
        self.chunkCount = try c.decodeIfPresent(Int.self, forKey: .chunkCount) ?? 0
        self.chunkHashes = try c.decodeIfPresent([String].self, forKey: .chunkHashes) ?? []
        self.contentType = try c.decodeIfPresent(String.self, forKey: .contentType) ?? "application/octet-stream"
        // Accept either Date (ISO-8601 default) or Unix ms; tolerate either on the wire.
        if let d = try? c.decodeIfPresent(Date.self, forKey: .createdAt) {
            self.createdAt = d ?? Date()
        } else if let ms = try c.decodeIfPresent(Int64.self, forKey: .createdAt) {
            self.createdAt = Date(timeIntervalSince1970: TimeInterval(ms) / 1000)
        } else {
            self.createdAt = Date()
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(rootHash, forKey: .rootHash)
        try c.encode(name, forKey: .name)
        try c.encode(totalBytes, forKey: .totalBytes)
        try c.encode(chunkSizeBytes, forKey: .chunkSizeBytes)
        try c.encode(chunkCount, forKey: .chunkCount)
        try c.encode(chunkHashes, forKey: .chunkHashes)
        try c.encode(contentType, forKey: .contentType)
        try c.encode(createdAt, forKey: .createdAt)
    }

    /// Build a descriptor from a buffer. Splits into ``chunkSizeBytes``-sized
    /// chunks (except the trailing chunk, which may be smaller), hashes each, and
    /// computes the root over the chunk-hash concatenation.
    public static func from(name: String, data: Data, contentType: String = "application/octet-stream", chunkSizeBytes: Int = 0) -> ContentDescriptor {
        let chunkSize = chunkSizeBytes > 0 ? chunkSizeBytes : ProtocolConstants.defaultChunkSizeBytes
        let chunkCount = data.isEmpty ? 0 : (data.count + chunkSize - 1) / chunkSize
        var hashes: [String] = []
        hashes.reserveCapacity(chunkCount)
        var concat = Data()
        concat.reserveCapacity(chunkCount * 32)

        for i in 0..<chunkCount {
            let start = i * chunkSize
            let end = min(start + chunkSize, data.count)
            let slice = data.subdata(in: start..<end)
            let h = SHA256.hash(data: slice)
            let bytes = Data(h)
            concat.append(bytes)
            hashes.append(bytes.map { String(format: "%02x", $0) }.joined())
        }

        let rootBytes = Data(SHA256.hash(data: concat))
        let rootHex = rootBytes.map { String(format: "%02x", $0) }.joined()

        return ContentDescriptor(
            rootHash: rootHex,
            name: name,
            totalBytes: Int64(data.count),
            chunkSizeBytes: chunkSize,
            chunkCount: chunkCount,
            chunkHashes: hashes,
            contentType: contentType
        )
    }

    /// Verify a chunk by recomputing its SHA-256 and comparing to ``chunkHashes``[index].
    public func verifyChunk(index: Int, bytes: Data) -> Bool {
        guard index >= 0 && index < chunkHashes.count else { return false }
        let h = Data(SHA256.hash(data: bytes))
        let hex = h.map { String(format: "%02x", $0) }.joined()
        return hex == chunkHashes[index]
    }
}
