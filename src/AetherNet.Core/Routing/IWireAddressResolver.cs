// SPDX-License-Identifier: MIT

namespace AetherNet.Routing;

/// <summary>
/// Resolves the address written on a packet — which may rotate every epoch (an ERID) or be a stable tag —
/// to a stable identity, and answers whether an address is one of this node's own.
///
/// <para>
/// The <see cref="MeshRelay"/> and <see cref="AetherNet.Messaging.MeshInboundDispatcher"/> need two facts
/// about a packet's endpoints — "is this for me" and "do I recognise both ends" — but must not know how
/// addresses rotate or where the contact set lives. That knowledge is the host's: on Android it is
/// <c>CircleDirectory</c> over the rotating-address (ERID) directory; a flat host may map every address to
/// itself. Supplying a resolver is what turns relaying on — without one, a node recognises nobody and so
/// carries for nobody (it is never an open relay by default).
/// </para>
/// </summary>
public interface IWireAddressResolver
{
    /// <summary>True if <paramref name="wireAddress"/> is one of this node's own current-or-recent addresses.</summary>
    bool IsLocal(string wireAddress);

    /// <summary>
    /// The stable identity behind a wire address, or <c>null</c> if this node does not recognise it
    /// (i.e. it is not a contact). Returning null for one or both ends is what stops the relay carrying.
    /// </summary>
    string? Recognise(string wireAddress);
}
