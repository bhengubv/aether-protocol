// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Host;

/// <summary>
/// How the host tells the radios whom to keep reachable. A platform with radios supplies this over its mesh;
/// a host with none supplies nothing, and <see cref="IAetherNodeClient.MeetAsync"/> is then a no-op.
/// </summary>
public interface INodeMeeting
{
    /// <summary>The contacts to keep reachable, replacing the previous set.</summary>
    void Meet(IReadOnlyList<NodeContact> contacts);
}
