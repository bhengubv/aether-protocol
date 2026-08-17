// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Where to send, as opposed to who you are.
///
/// <para>
/// A <c>MeshPacket</c> header is readable before anything is decrypted, so whatever goes in it is
/// public. Putting the AetherTag there gives a passive observer a stable string that appears on
/// every packet this phone ever sends — the threat model's first critical finding, and the thing
/// that makes a person followable for as long as they own the device.
/// </para>
///
/// <para>
/// What goes there instead is an <see cref="EphemeralRoutingId"/>: derived from a key that only this
/// phone holds, changing every epoch, and unlinkable across epochs by anyone without that key. The
/// long-term identity travels inside the encrypted session, where it is meant to be.
/// </para>
/// </summary>
public static class WireAddress
{
    /// <summary>
    /// How far back an address is still accepted as ours. A packet sent moments before an epoch turns
    /// over arrives moments after it; without this, real traffic would be rejected on the boundary
    /// every single epoch.
    /// </summary>
    private const int EpochsAccepted = 1;

    /// <summary>This phone's address for the moment given.</summary>
    public static string For(byte[] routingKey, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        return EphemeralRoutingId.Derive(routingKey, (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
    }

    /// <summary>
    /// Is this address one of ours — now, or in the epoch just gone?
    /// </summary>
    public static bool IsMine(string? address, byte[] routingKey, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(address)) return false;
        ArgumentNullException.ThrowIfNull(routingKey);

        var seconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var epoch = EphemeralRoutingId.EpochFor(seconds);

        for (var back = 0; back <= EpochsAccepted; back++)
            if (string.Equals(EphemeralRoutingId.DeriveForEpoch(routingKey, epoch - back), address, StringComparison.Ordinal))
                return true;

        return false;
    }
}
