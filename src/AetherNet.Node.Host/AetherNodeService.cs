// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node.Host;

/// <summary>
/// The reference in-process implementation of <see cref="IAetherNodeClient"/>. It adapts the device's real
/// <see cref="INodeIdentity"/>, a messaging seam (<see cref="INodeMessaging"/>) and a presence seam
/// (<see cref="INodeLinkSource"/>) to the bind contract.
///
/// <para>
/// The private key never passes through here — identity operations are <c>GetTag</c>, <c>GetPublicKey</c>
/// and <c>Sign</c>, exactly the closed surface of <see cref="INodeIdentity"/>. A locked node is reported as
/// <see cref="AetherNodeErrorCode.NodeUnavailable"/>, never as an absent identity, so a consumer never
/// mistakes "locked" for "mint a new one". A cross-process host (an Android bound <c>Service</c>) wraps this
/// same object and adds only the transport and the per-app grant check.
/// </para>
/// </summary>
public sealed class AetherNodeService : IAetherNodeClient
{
    private readonly INodeIdentity _identity;
    private readonly INodeMessaging _messaging;
    private readonly INodeLinkSource _link;

    public AetherNodeService(INodeIdentity identity, INodeMessaging messaging, INodeLinkSource link)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _link = link ?? throw new ArgumentNullException(nameof(link));
    }

    /// <inheritdoc />
    public async Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.GetOrMintAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.SignAsync(data.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _messaging.SendAsync(to, payload, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _messaging.GetInboxAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_link.Current);

    /// <inheritdoc />
    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        if (listener is null)
        {
            throw new ArgumentNullException(nameof(listener));
        }
        return new Subscription(this, listener);
    }

    private static AetherNodeException Locked(Exception inner)
        => new(AetherNodeErrorCode.NodeUnavailable, "the node is present but locked", inner);

    private sealed class Subscription : IDisposable
    {
        private readonly AetherNodeService _owner;
        private readonly IAetherNodeEvents _listener;

        public Subscription(AetherNodeService owner, IAetherNodeEvents listener)
        {
            _owner = owner;
            _listener = listener;
            _owner._messaging.Inbound += OnInbound;
            _owner._link.Changed += OnLinkChanged;
        }

        private void OnInbound(InboundMessage message) => _listener.OnInbound(message);

        private void OnLinkChanged() => _listener.OnLinkChanged(_owner._link.Current);

        public void Dispose()
        {
            _owner._messaging.Inbound -= OnInbound;
            _owner._link.Changed -= OnLinkChanged;
        }
    }
}
