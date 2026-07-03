// SPDX-License-Identifier: MIT

import Foundation

/// The kind of state change a ``SyncRecord`` carries.
///
/// Mirrors the C# `SyncOp` enum (`src/AetherNet.Security/Sync/SyncRecord.cs`).
public enum SyncOp: UInt8, Sendable {
    /// Create or update the item.
    case upsert = 0
    /// Delete the item.
    case delete = 1
    /// Mark the item read (read-state sync).
    case read = 2
}

/// One state change to a synced item (a message, a read-marker, a deletion),
/// emitted by one of a user's devices and gossiped to that user's other devices
/// so they all converge on the same state — with no server.
///
/// The ``encryptedPayload`` is already end-to-end encrypted to the user's device
/// set, so any node that relays the record (over the mesh or via DTN
/// store-and-forward) learns nothing about its content.
///
/// `recordId` is the record's globally-unique id, held as its 16 raw big-endian
/// (RFC-4122) bytes — the exact byte order the wire format uses and that the
/// fixture UUID strings encode. Use ``init(recordIdString:...)`` /
/// ``recordIdString`` to convert to and from the canonical dashed UUID text.
///
/// Byte-identical across every AetherNet SDK (verified against
/// `fixtures/sync/vectors.json`); mirrors the C# `SyncRecord` record.
public struct SyncRecord: Equatable, Sendable {
    /// Globally-unique id for this record, as 16 raw big-endian bytes.
    public let recordId: [UInt8]
    /// The device that produced the record.
    public let deviceId: String
    /// Create/update, delete, or read-marker.
    public let op: SyncOp
    /// The item this record is about (the sync key).
    public let itemId: String
    /// The device's monotonic counter at emit time.
    public let logicalClock: Int64
    /// Wall-clock time (Unix ms) the record was created.
    public let createdAtMs: Int64
    /// The E2E-encrypted item content (opaque; empty for a delete/read).
    public let encryptedPayload: Data

    /// Creates a record from an explicit 16-byte big-endian record id.
    public init(
        recordId: [UInt8],
        deviceId: String,
        op: SyncOp,
        itemId: String,
        logicalClock: Int64,
        createdAtMs: Int64,
        encryptedPayload: Data
    ) {
        precondition(recordId.count == 16, "recordId must be 16 bytes")
        self.recordId = recordId
        self.deviceId = deviceId
        self.op = op
        self.itemId = itemId
        self.logicalClock = logicalClock
        self.createdAtMs = createdAtMs
        self.encryptedPayload = encryptedPayload
    }

    /// Creates a record from a dashed UUID string (e.g. `"00112233-4455-…"`),
    /// stored as its 16 raw big-endian bytes. Returns `nil` for a malformed id.
    public init?(
        recordIdString: String,
        deviceId: String,
        op: SyncOp,
        itemId: String,
        logicalClock: Int64,
        createdAtMs: Int64,
        encryptedPayload: Data
    ) {
        guard let bytes = SyncRecordSerializer.recordIdBytes(fromUuidString: recordIdString) else {
            return nil
        }
        self.init(
            recordId: bytes,
            deviceId: deviceId,
            op: op,
            itemId: itemId,
            logicalClock: logicalClock,
            createdAtMs: createdAtMs,
            encryptedPayload: encryptedPayload)
    }

    /// The record id as a lower-case dashed UUID string.
    public var recordIdString: String {
        SyncRecordSerializer.uuidString(fromRecordIdBytes: recordId)
    }
}
