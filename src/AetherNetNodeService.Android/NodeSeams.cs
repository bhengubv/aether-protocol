// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNetNodeService;
using AetherNetNodeService.Host;

namespace AetherNetNodeService.Android;

/// <summary>
/// The messaging seam for a node that holds identity but does not yet run the mesh — the shape a standalone
/// identity node starts as, before transports are wired into it. It refuses a send honestly (there is no
/// route) and reports an empty inbox; it never raises an inbound event. Auth and minting work fully; sending
/// waits for the mesh to be wired into the node.
/// </summary>
public sealed class NullNodeMessaging : INodeMessaging
{
    public event Action<InboundMessage>? Inbound
    {
        add { }
        remove { }
    }

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(OutboundResult.Refused("this node is not running the mesh yet"));

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InboundMessage>>([]);
}

/// <summary>
/// The presence seam for a node that is not yet running the mesh: always <see cref="NodeLinkStatus.Offline"/>,
/// never changing. Replaced by a real radio-backed source when the node gains transports.
/// </summary>
public sealed class OfflineNodeLinkSource : INodeLinkSource
{
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public NodeLinkStatus Current => NodeLinkStatus.Offline;
}
