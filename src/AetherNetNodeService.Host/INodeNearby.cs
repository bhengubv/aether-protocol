// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// AetherNet's nearby radios as one switch for the whole device — what a connected app shows as "AetherNet is on /
/// off". Off, the platform runs only the internet leg. The node host adapts it to
/// <see cref="IAetherNodeClient.SetNearbyAsync"/> and reports it in <see cref="NodeLinkStatus.NearbyOn"/>.
/// </summary>
public interface INodeNearby
{
    /// <summary>Whether the nearby radios are switched on.</summary>
    bool On { get; }

    /// <summary>Switch them on or off. Nothing happens when they already are.</summary>
    void Set(bool on);
}
