// SPDX-License-Identifier: MIT

namespace AetherNet.Mesh;

/// <summary>
/// Somebody this phone has added, as far as the radios need to know them: their tag, their public key once it has
/// been shared, and whether they have added this phone back.
/// </summary>
/// <param name="Tag">Their AetherTag.</param>
/// <param name="PublicKey">Their public key, or null while only the tag is known (a typed tag, not yet met).</param>
/// <param name="AddedByThem">Whether they have added this phone back — which they can only have done by reaching it.</param>
public sealed record CircleContact(string Tag, byte[]? PublicKey, bool AddedByThem);

/// <summary>
/// The people this phone has added, from whoever keeps the address book — the app's own store on a phone that runs
/// its radios itself, or the contacts an app hands AetherNetService.
/// </summary>
/// <remarks>
/// The radios have no address book of their own. They only need to know whom to keep reachable, and to hear when
/// that changes, because a change can move who hosts the Wi-Fi Direct group.
/// </remarks>
public interface ICircleContacts
{
    /// <summary>Everyone added, right now.</summary>
    IReadOnlyList<CircleContact> Contacts { get; }

    /// <summary>Somebody was added or removed.</summary>
    event Action? Changed;
}
