// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Routing;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The app's answer to the library's <see cref="IWireAddressResolver"/> seam: it turns a rotating wire
/// address into the stable identity behind it, using the same <see cref="CircleDirectory"/> the rest of
/// the app recognises contacts with.
///
/// <para>
/// The library's inbound dispatcher keys the messaging ratchet and its handlers on stable identities, but
/// a radio only ever sees an ERID that rotates every epoch. This resolver closes that gap: it says whether
/// a destination address is one of ours (so a message a peer sealed to our current ERID is delivered here
/// rather than dropped as not-ours), and turns a recognised source ERID back into the contact's stable
/// tag. A stranger's address, or a contact who has not shared a routing key yet, resolves to nobody — so
/// recognition stays a capability granted only to people this phone has chosen.
/// </para>
/// </summary>
public sealed class CircleDirectoryWireResolver : IWireAddressResolver
{
    private readonly CircleDirectory _circle;
    private readonly string _myTag;

    public CircleDirectoryWireResolver(CircleDirectory circle, IIdentityService me)
    {
        _circle = circle ?? throw new ArgumentNullException(nameof(circle));
        ArgumentNullException.ThrowIfNull(me);
        _myTag = me.AetherTag;
    }

    /// <summary>Our stable tag, or any rotating address derived from our own routing key.</summary>
    public bool IsLocal(string wireAddress) =>
        !string.IsNullOrEmpty(wireAddress)
        && (string.Equals(wireAddress, _myTag, StringComparison.Ordinal) || _circle.IsMine(wireAddress));

    /// <summary>The contact behind this rotating address, or null for a stranger / an unrecognised address.</summary>
    public string? Recognise(string wireAddress) => _circle.Recognise(wireAddress);
}
