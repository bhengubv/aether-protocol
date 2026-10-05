// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// Aether Aware inside the node: what the radios hear around this device, what the signature pack calls it, and what
/// has kept up with the person while they moved. The node host adapts it to <see cref="IAetherNodeClient"/>; a
/// platform supplies it from its own radios.
/// </summary>
/// <remarks>
/// It only ever reports. There is nothing to set here and nothing an app can ask it to do: Aware is switched on and
/// off like any other radio, through the radio list, so that a person finds it where they find the rest.
/// </remarks>
public interface INodeAwareSource
{
    /// <summary>What is around this device now, and why not when there is nothing.</summary>
    AwareReport Current { get; }

    /// <summary>Raised when <see cref="Current"/> changed and a connected app should redraw.</summary>
    event Action? Changed;
}
