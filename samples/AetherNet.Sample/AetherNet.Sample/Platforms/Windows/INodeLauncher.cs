// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Pipe;

/// <summary>Finds AetherNetService on this computer and starts it — what binding does for an app on a phone.</summary>
public interface INodeLauncher
{
    /// <summary>Whether AetherNetService is installed here.</summary>
    bool IsInstalled { get; }

    /// <summary>Start AetherNetService. False when it is not installed or would not start.</summary>
    bool Start();
}
