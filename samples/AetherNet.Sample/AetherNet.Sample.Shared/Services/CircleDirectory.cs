// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Who, out of everyone broadcasting nearby, is somebody this phone already knows.
///
/// <para>
/// A radio only ever sees a rotating address. That is the point — it is what stops a passive listener
/// following a person around — but it also means a beacon on its own tells you nothing about whose it
/// is. Without an answer to that question the only way to find out is to dial it and see who picks up,
/// and dialling strangers is how you end up in a group with someone who was never invited.
/// </para>
///
/// <para>
/// So recognition is by shared secret, not by broadcast. Two people who have added each other exchange
/// routing keys <em>inside</em> the established session, where nothing on the air can read them. From
/// then on each can derive the other's current address and match it against what the radio saw. A
/// stranger holds no key, so their beacon resolves to nobody, and nobody is exactly who we dial.
/// </para>
///
/// <para>
/// The rotation still holds against outsiders: an observer without a routing key sees an opaque value
/// that changes every epoch with no linkage across windows. Recognition is a capability you grant to
/// people you have chosen, and it is revoked by forgetting the key.
/// </para>
/// </summary>
public sealed class CircleDirectory
{
    private readonly EridDirectory _directory;
    private readonly AetherStore _store;
    private readonly IIdentityService _me;

    public CircleDirectory(AetherStore store, IIdentityService me)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));

        // Seeded with the SAME routing key the radios advertise from. Deriving it a second way here —
        // the protocol's own AddErid() takes it from the Signal service rather than the device
        // identity — would give this phone two different rotating addresses: one it broadcasts and one
        // its contacts compute. Nothing would error; recognition would simply never match, forever.
        _directory = new EridDirectory(_me.RoutingKey);

        // Everything learned in previous sessions, so a restart does not turn every contact back into
        // a stranger.
        foreach (var (tag, key) in _store.GetPeerRoutingKeys())
            _directory.RememberPeer(tag, key);
    }

    /// <summary>This phone's own rotating address for right now — what it puts in its beacon.</summary>
    public string MyAddress(DateTimeOffset? now = null) =>
        WireAddress.For(_me.RoutingKey, now);

    /// <summary>How many contacts have let this phone recognise them.</summary>
    public int KnownCount => _directory.KnownPeerCount;

    /// <summary>
    /// Whose address is this? Returns their AetherTag, or null when it belongs to nobody we know —
    /// which includes every stranger, and every contact who has not exchanged keys with us yet.
    /// </summary>
    /// <remarks>
    /// Both the current epoch and the one just gone are accepted. A beacon read moments after an
    /// epoch turns over was composed moments before it, and rejecting that would make recognition
    /// fail on the boundary every fifteen minutes for no reason.
    /// </remarks>
    public string? Recognise(string? address, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(address)) return null;

        var seconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return _directory.ResolvePeer(address, seconds)
            ?? _directory.ResolvePeer(address, seconds - EphemeralRoutingId.DefaultEpochSeconds);
    }

    /// <summary>
    /// Learn a contact's routing key, from inside their session. Persisted, so the recognition
    /// survives a restart.
    /// </summary>
    public void Learn(string peerTag, byte[] routingKey)
    {
        if (string.IsNullOrEmpty(peerTag)) return;
        ArgumentNullException.ThrowIfNull(routingKey);
        if (routingKey.Length == 0) return;

        _directory.RememberPeer(peerTag, routingKey);
        _store.UpsertPeerRoutingKey(peerTag, routingKey);
    }

    /// <summary>Do we already hold this contact's key? Sharing ours again costs a round trip.</summary>
    public bool Knows(string peerTag) =>
        !string.IsNullOrEmpty(peerTag) && _directory.EridForPeer(peerTag, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is not null;

    /// <summary>
    /// Stop recognising somebody. Called when a contact is removed — the relationship is what granted
    /// the capability, so ending it has to take the capability with it.
    /// </summary>
    public void Forget(string peerTag)
    {
        if (string.IsNullOrEmpty(peerTag)) return;
        _directory.ForgetPeer(peerTag);
        _store.RemovePeerRoutingKey(peerTag);
    }
}
