// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Host;

/// <summary>
/// The presence seam the node host adapts to <see cref="IAetherNodeClient"/>. A platform supplies this
/// over the SDK's single-link and radio-choice types, projecting them to a <see cref="NodeLinkStatus"/> —
/// a report of what is linked right now, never a control.
/// </summary>
public interface INodeLinkSource
{
    /// <summary>The current link/presence snapshot.</summary>
    NodeLinkStatus Current { get; }

    /// <summary>Raised when the snapshot changes — a radio came up or down, a peer linked or dropped.</summary>
    event Action? Changed;
}
