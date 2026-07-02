// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.Profiles;

/// <summary>
/// Exchanges peer profile metadata over <see cref="PacketType.ProfileSync"/>. Profiles are shared
/// <em>directed</em> (to a specific peer), not broadcast, for privacy. Received profiles are cached and
/// surfaced via <see cref="ProfileUpdated"/>.
/// </summary>
public interface IProfileService
{
    /// <summary>Raised when a peer's profile is received or refreshed.</summary>
    event EventHandler<ProfileSyncPayload>? ProfileUpdated;

    /// <summary>Set this node's own profile (stamps <see cref="ProfileSyncPayload.UpdatedAtMs"/> to now).</summary>
    void SetLocalProfile(string displayName, string avatarRef, string statusMessage);

    /// <summary>This node's current local profile.</summary>
    ProfileSyncPayload GetLocalProfile();

    /// <summary>Send this node's local profile directly to <paramref name="peerUhid"/>. Best-effort; returns delivery success.</summary>
    Task<bool> PublishProfileToAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process an incoming <see cref="PacketType.ProfileSync"/> packet: cache the sender's profile
    /// (keyed by its <see cref="ProfileSyncPayload.Uhid"/>) and raise <see cref="ProfileUpdated"/>.
    /// Returns false for the wrong packet type, a malformed payload, or our own profile echoed back.
    /// </summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>The cached profile for <paramref name="uhid"/>, or null if none is known.</summary>
    ProfileSyncPayload? GetProfile(string uhid);

    /// <summary>Snapshot of every peer profile this node has cached.</summary>
    IReadOnlyList<ProfileSyncPayload> GetKnownProfiles();
}
