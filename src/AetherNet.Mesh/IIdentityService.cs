// SPDX-License-Identifier: MIT

namespace AetherNet.Mesh;

/// <summary>
/// Who this device is, as the app sees it.
///
/// <para>
/// The app does not create this and does not own it. The identity belongs to the <b>device</b> — it is
/// the device's address on the mesh, the way an IP address belongs to the machine rather than to any
/// program running on it. This app asks the node for it, and the node mints one only if the device has
/// never had one.
/// </para>
///
/// <para>
/// That is why there is no private key on this interface. An app that holds the device's key is an app
/// claiming ownership of the device's address, and if every app does that then a phone with fifteen
/// apps is fifteen nodes on the mesh — fifteen presence beacons, fifteen entries in every neighbour's
/// routing table, fifteen reputations for one person. When something must be signed, this asks the node
/// and gets a signature back.
/// </para>
/// </summary>
public interface IIdentityService
{
    /// <summary>The shareable AetherTag (e.g. <c>KXJB7-MN2P4</c>) — this device's address.</summary>
    string AetherTag { get; }

    /// <summary>The public key the tag is derived from.</summary>
    byte[] PublicKey { get; }

    /// <summary>
    /// The secret behind this device's rotating wire address. Derived from the identity and useful for
    /// nothing else — the identity itself never leaves the node.
    /// </summary>
    byte[] RoutingKey { get; }

    /// <summary>True when this run is the first time this device has ever had an identity.</summary>
    bool IsNewIdentity { get; }

    /// <summary>How the identity is protected on this device, for the UI to state honestly.</summary>
    string ProtectionDescription { get; }

    /// <summary>Sign bytes as this device.</summary>
    byte[] Sign(byte[] data);
}
