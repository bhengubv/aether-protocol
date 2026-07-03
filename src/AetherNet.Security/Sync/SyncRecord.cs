// SPDX-License-Identifier: MIT

namespace AetherNet.Security.Sync;

/// <summary>The kind of state change a <see cref="SyncRecord"/> carries.</summary>
public enum SyncOp : byte
{
    /// <summary>Create or update the item.</summary>
    Upsert = 0,
    /// <summary>Delete the item.</summary>
    Delete = 1,
    /// <summary>Mark the item read (read-state sync).</summary>
    Read = 2,
}

/// <summary>
/// One state change to a synced item (a message, a read-marker, a deletion),
/// emitted by one of a user's devices and gossiped to that user's other devices
/// so they all converge on the same state — with no server.
///
/// The <see cref="EncryptedPayload"/> is already end-to-end encrypted to the
/// user's device set, so any node that relays the record (over the mesh or via
/// DTN store-and-forward) learns nothing about its content.
/// </summary>
/// <param name="RecordId">Globally-unique id for this record.</param>
/// <param name="DeviceId">The device that produced the record.</param>
/// <param name="Op">Create/update, delete, or read-marker.</param>
/// <param name="ItemId">The item this record is about (the sync key).</param>
/// <param name="LogicalClock">The device's monotonic counter at emit time.</param>
/// <param name="CreatedAtMs">Wall-clock time (Unix ms) the record was created.</param>
/// <param name="EncryptedPayload">The E2E-encrypted item content (opaque; empty for a delete/read).</param>
public sealed record SyncRecord(
    Guid RecordId,
    string DeviceId,
    SyncOp Op,
    string ItemId,
    long LogicalClock,
    long CreatedAtMs,
    byte[] EncryptedPayload);
