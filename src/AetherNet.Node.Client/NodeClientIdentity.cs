// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node.Client;

/// <summary>
/// The device's identity as a connected app sees it: <see cref="INodeIdentity"/> answered by the node over
/// <see cref="IAetherNodeClient"/>. The app never mints — asking for the tag asks the node, which holds it.
///
/// <para>
/// <see cref="DeriveKeyAsync"/> is refused. A derived key never crosses the node boundary (see
/// <see cref="IAetherNodeClient"/>), so whatever needs one — the rotating wire address — runs in the node.
/// A node that is present but unreachable right now surfaces as <see cref="NodeIdentityUnavailableException"/>,
/// which is <see cref="INodeIdentity"/>'s own "not now, and never a reason to mint a replacement".
/// </para>
/// </summary>
public sealed class NodeClientIdentity : INodeIdentity
{
    private readonly IAetherNodeClient _node;

    public NodeClientIdentity(IAetherNodeClient node)
        => _node = node ?? throw new ArgumentNullException(nameof(node));

    public async ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _node.GetTagAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNodeException ex) when (ex.Code == AetherNodeErrorCode.NodeUnavailable)
        {
            throw new NodeIdentityUnavailableException(ex.Message, ex);
        }
    }

    public async ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _node.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNodeException ex) when (ex.Code == AetherNodeErrorCode.NodeUnavailable)
        {
            throw new NodeIdentityUnavailableException(ex.Message, ex);
        }
    }

    public async ValueTask<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _node.SignAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNodeException ex) when (ex.Code == AetherNodeErrorCode.NodeUnavailable)
        {
            throw new NodeIdentityUnavailableException(ex.Message, ex);
        }
    }

    public ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"A derived key ('{purpose}') never leaves the node; whatever needs it runs in the node.");
}
