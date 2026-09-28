// SPDX-License-Identifier: MIT

using AetherNet.Routing;

namespace AetherNet.Mesh;

/// <summary>
/// The wire-address resolver for an app with no mesh of its own — the counterpart of <see cref="NullRadioMesh"/>.
/// It recognises nobody, so it relays for nobody, and the only address it calls its own is its AetherTag.
/// Recognising contacts behind their rotating addresses needs the routing key, and that lives in the node that
/// runs the radios — never in an app that connects to it.
/// </summary>
public sealed class NoMeshWireAddressResolver : IWireAddressResolver
{
    private readonly IIdentityService _me;

    public NoMeshWireAddressResolver(IIdentityService me)
        => _me = me ?? throw new ArgumentNullException(nameof(me));

    public bool IsLocal(string wireAddress)
        => string.Equals(wireAddress, _me.AetherTag, StringComparison.Ordinal);

    public string? Recognise(string wireAddress) => null;
}
