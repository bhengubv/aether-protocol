// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>What kind of screen a cast target is.</summary>
public enum CastKind
{
    /// <summary>Another AetherNet node that can display — a phone, a Circle-OS device, or a TV/dongle
    /// running the Aether node service. Reached over the mesh, no LAN or DLNA involved.</summary>
    Aether,

    /// <summary>A DLNA/UPnP smart TV on the local network that does NOT run Aether. Reached over the LAN,
    /// via an open standard — no Google Cast, no GMS.</summary>
    Dlna,
}

/// <summary>
/// One place you can send a video to watch on a bigger screen. An Aether target is a mesh node (its Id
/// is an AetherTag); a DLNA target is a TV on the LAN (its <see cref="ControlUrl"/> is where its
/// AVTransport service takes commands).
/// </summary>
/// <param name="Id">AetherTag for a mesh device; the renderer's USN/UDN for a TV.</param>
/// <param name="Name">What to show the person — a petname, or the TV's friendly name.</param>
public sealed record CastTarget(string Id, string Name, CastKind Kind, string? ControlUrl = null);
