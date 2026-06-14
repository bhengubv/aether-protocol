// SPDX-License-Identifier: MIT

namespace AetherNet.Tipping;

/// <summary>
/// Supplies the local node's UHID to the tipping layer (the identity a tip is sent
/// "from", and the key the per-tipper daily cap and operator registration are keyed
/// on). Narrowed to exactly what tipping needs from the host's node identity so the
/// tipping services do not take a dependency on the whole host node-storage surface.
/// </summary>
public interface ILocalNodeProvider
{
    /// <summary>
    /// The local node's UHID, or null if the node has not been initialised yet (in
    /// which case tipping operations no-op rather than fabricate an identity).
    /// </summary>
    Task<string?> GetLocalUhidAsync();
}
