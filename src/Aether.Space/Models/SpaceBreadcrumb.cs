// SPDX-License-Identifier: MIT
namespace Aether.Space.Models;

/// <summary>
/// A geo-pinned digital notice dropped by a user at a physical location.
///
/// Passing devices auto-pull it, cache it, and re-host it for other passersby.
/// Content is addressed by hash (IContentService); the breadcrumb carries only metadata.
///
/// Wire format: JSON, transmitted as <c>PacketType.SpaceBreadcrumb (40)</c>.
/// </summary>
public sealed class SpaceBreadcrumb
{
    /// <summary>IContentService hash of the actual payload (text/image/binary).</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>6-character Geohash of the drop location (~1.2 km² cell).</summary>
    public string GeoHash { get; set; } = string.Empty;

    /// <summary>UHID of the node that dropped the breadcrumb.</summary>
    public string AnchorUhid { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Time-to-live in hours. Default 72 h; max 168 h (1 week).
    /// <see cref="BreadcrumbType.Emergency"/> breadcrumbs are fixed at 720 h.
    /// </summary>
    public int TtlHours { get; set; } = 72;

    /// <summary>Category of the breadcrumb.</summary>
    public BreadcrumbType Type { get; set; } = BreadcrumbType.Notice;

    /// <summary>
    /// Ed25519 signature over (ContentHash + GeoHash + CreatedAtUtc ISO-8601).
    /// Empty byte array if the breadcrumb has not been signed.
    /// </summary>
    public byte[] Signature { get; set; } = [];

    /// <summary>UTC expiry calculated from <see cref="CreatedAtUtc"/> + <see cref="TtlHours"/>.</summary>
    public DateTime ExpiresAtUtc => CreatedAtUtc.AddHours(TtlHours);

    /// <summary>True when <see cref="ExpiresAtUtc"/> has passed.</summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
}
