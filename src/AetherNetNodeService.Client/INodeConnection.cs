// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>
/// A live connection to AetherNetService that knows when the service's end has gone — killed, crashed, updated. The
/// binder client on a phone and the pipe client on a computer are both one, so <see cref="BoundNodeClient"/> connects
/// again the same way on either.
/// </summary>
public interface INodeConnection
{
    /// <summary>Whether AetherNetService is still on the other end.</summary>
    bool IsAlive { get; }

    /// <summary>Raised once when AetherNetService's end goes. Not raised when the app lets go itself.</summary>
    event Action? Died;
}
