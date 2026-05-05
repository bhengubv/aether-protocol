// SPDX-License-Identifier: MIT

namespace Aether.Models;

/// <summary>
/// Lifecycle state of a DTN bundle.
/// </summary>
public enum BundleStatus : byte
{
    /// <summary>Bundle is queued locally and waiting for a delivery opportunity.</summary>
    Pending = 0,
    /// <summary>Bundle is in custody on this node, awaiting forwarding to a closer carrier.</summary>
    InCustody = 1,
    /// <summary>Bundle has been delivered to its recipient.</summary>
    Delivered = 2,
    /// <summary>Bundle exceeded its TTL before delivery.</summary>
    Expired = 3,
    /// <summary>Bundle delivery failed permanently (no eligible carriers, recipient unknown).</summary>
    Failed = 4,
}

/// <summary>
/// Priority class influencing replication aggressiveness and delivery preference.
/// </summary>
public enum BundlePriority : byte
{
    Low = 0,
    Normal = 1,
    High = 2,
    /// <summary>Emergency. Replicates to every eligible carrier regardless of proximity.</summary>
    Sos = 3,
}

/// <summary>
/// A delay-tolerant network bundle. Store-and-forward unit carried opportunistically
/// across the mesh until it reaches its recipient. Custody can be transferred between
/// nodes; the original sender holds responsibility until a custody transfer is acknowledged.
/// </summary>
public sealed class DtnBundle
{
    /// <summary>Globally unique bundle identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the node that originally sent the bundle.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>UHID of the bundle's intended recipient.</summary>
    public string RecipientUhid { get; set; } = string.Empty;

    /// <summary>Encrypted payload. Treated as opaque bytes by the DTN layer.</summary>
    public byte[] EncryptedPayload { get; set; } = [];

    /// <summary>Replication-aggressiveness class.</summary>
    public BundlePriority Priority { get; set; } = BundlePriority.Normal;

    /// <summary>Current lifecycle state.</summary>
    public BundleStatus Status { get; set; } = BundleStatus.Pending;

    /// <summary>Number of replicas of this bundle currently believed to exist in the mesh.</summary>
    public int CopyCount { get; set; } = 1;

    /// <summary>Maximum number of replicas that may exist concurrently.</summary>
    public int MaxCopies { get; set; } = Aether.Constants.ProtocolConstants.DtnMaxCopies;

    /// <summary>Geohash recorded at the sender at bundle-creation time, when shared.</summary>
    public string? SenderGeohash { get; set; }

    /// <summary>Last known geohash of the recipient, if known to the sender.</summary>
    public string? RecipientLastGeohash { get; set; }

    /// <summary>Number of custody transfers this bundle has undergone.</summary>
    public int HopCount { get; set; }

    /// <summary>UTC timestamp of bundle creation.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time after which the bundle should be discarded.</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(Aether.Constants.ProtocolConstants.DtnBundleTtlHours);

    /// <summary>Returns true if the bundle has expired.</summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}

/// <summary>
/// Record of a custody transfer between two nodes.
/// </summary>
public sealed class CustodyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BundleId { get; set; }
    public string FromUhid { get; set; } = string.Empty;
    public string ToUhid { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public DateTime TransferredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Receipt sent back to the original sender once a bundle is delivered.
/// </summary>
public sealed class DtnDeliveryReceipt
{
    public Guid BundleId { get; set; }
    public string RecipientUhid { get; set; } = string.Empty;
    public int TotalHops { get; set; }
    public int TotalCustodyTransfers { get; set; }
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}
