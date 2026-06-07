// SPDX-License-Identifier: MIT

using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Event payload raised by <see cref="IDtnService.BundleReceived"/> the moment a
/// DTN bundle arrives whose final recipient is the local node — i.e., a bundle
/// addressed TO us has just been delivered locally by a peer or by the receive
/// pump itself.
///
/// <para>
/// Distinct from <see cref="DtnDeliveryReceipt"/> (raised via
/// <see cref="IDtnService.BundleDelivered"/>), which fires on the original
/// sender side once a delivery confirmation flows back. Consumers that want to
/// know "did a bundle arrive for me?" should subscribe to
/// <see cref="IDtnService.BundleReceived"/>; consumers that want to know "did
/// my outbound bundle reach the recipient?" should subscribe to
/// <see cref="IDtnService.BundleDelivered"/>.
/// </para>
///
/// <para>
/// Added in v1.2.0 to close the gap surfaced by Wave 16 — previously, receive-side
/// consumers had to inspect <see cref="IDtnService.HandleAsync"/> indirectly via
/// the host shell to know when a bundle had arrived.
/// </para>
/// </summary>
public sealed class DtnBundleReceivedEventArgs : EventArgs
{
    /// <summary>The globally-unique bundle identifier.</summary>
    public Guid BundleId { get; init; }

    /// <summary>UHID of the original sender of the bundle.</summary>
    public string SenderUhid { get; init; } = string.Empty;

    /// <summary>UHID of the recipient — always the local node when this event fires.</summary>
    public string RecipientUhid { get; init; } = string.Empty;

    /// <summary>The encrypted payload bytes as delivered. The DTN layer does not
    /// decrypt — consumers route this through their security layer.</summary>
    public byte[] EncryptedPayload { get; init; } = Array.Empty<byte>();

    /// <summary>Replication-aggressiveness class of the bundle.</summary>
    public BundlePriority Priority { get; init; }

    /// <summary>Number of custody transfers the bundle underwent before arriving here.</summary>
    public int HopCount { get; init; }

    /// <summary>UTC timestamp at which the bundle was received locally.</summary>
    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;
}
